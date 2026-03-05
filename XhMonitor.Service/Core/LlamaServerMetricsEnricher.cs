using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using XhMonitor.Core.Models;

namespace XhMonitor.Service.Core;

public sealed class LlamaServerMetricsEnricher : IProcessMetricsEnricher
{
    private const string LlamaProcessName = "llama-server";
    private const string MetricsHost = "127.0.0.1";
    private const string MetricsPath = "/metrics";

    private readonly ILogger<LlamaServerMetricsEnricher> _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _httpSemaphore = new(2, 2);
    private readonly Dictionary<int, LlamaProcessState> _states = new();

    public LlamaServerMetricsEnricher(
        IHttpClientFactory httpClientFactory,
        ILogger<LlamaServerMetricsEnricher> logger)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("llama-metrics");
    }

    public async Task EnrichAsync(IReadOnlyList<ProcessMetrics> metrics, CancellationToken cancellationToken = default)
    {
        if (metrics.Count == 0)
        {
            _states.Clear();
            return;
        }

        var livePids = new HashSet<int>(metrics.Count);
        var tasks = new List<Task>();

        foreach (var processMetrics in metrics)
        {
            livePids.Add(processMetrics.Info.ProcessId);

            if (!TryGetLlamaMetricsPort(processMetrics.Info, out var port))
            {
                continue;
            }

            SetMetric(processMetrics, LlamaMetricKeys.Port, port, string.Empty, "Port");
            tasks.Add(EnrichSingleAsync(processMetrics, port, cancellationToken));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        CleanupStates(livePids);
    }

    private async Task EnrichSingleAsync(ProcessMetrics processMetrics, int port, CancellationToken cancellationToken)
    {
        await _httpSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metricsText = await FetchMetricsTextAsync(port, cancellationToken).ConfigureAwait(false);
            if (metricsText == null)
            {
                return;
            }

            if (!LlamaPrometheusTextParser.TryParse(metricsText.AsSpan(), out var snapshot))
            {
                return;
            }

            var nowTicks = Stopwatch.GetTimestamp();
            var pid = processMetrics.Info.ProcessId;

            SetMetric(processMetrics, LlamaMetricKeys.Port, port, string.Empty, "Port");

            if (snapshot.RequestsProcessing.HasValue)
            {
                SetMetric(processMetrics, LlamaMetricKeys.ReqProcessing, snapshot.RequestsProcessing.Value, string.Empty, "Req Processing");
            }

            if (snapshot.RequestsDeferred.HasValue)
            {
                SetMetric(processMetrics, LlamaMetricKeys.ReqDeferred, snapshot.RequestsDeferred.Value, string.Empty, "Req Deferred");
            }

            if (snapshot.TokensPredictedTotal.HasValue)
            {
                SetMetric(processMetrics, LlamaMetricKeys.OutTokensTotal, snapshot.TokensPredictedTotal.Value, "tok", "Out Tokens");
            }

            if (snapshot.TokensPredictedTotal.HasValue && snapshot.TokensPredictedSecondsTotal.HasValue)
            {
                var tokensTotal = snapshot.TokensPredictedTotal.Value;
                var predictedSecondsTotal = snapshot.TokensPredictedSecondsTotal.Value;

                if (_states.TryGetValue(pid, out var state)
                    && state.Port == port
                    && LlamaDerivedMetricsCalculator.TryCompute(
                        state.TokensPredictedTotal,
                        state.TokensPredictedSecondsTotal,
                        state.WallTicks,
                        tokensTotal,
                        predictedSecondsTotal,
                        nowTicks,
                        out var genTpsCompute,
                        out var busyPercent))
                {
                    SetMetric(processMetrics, LlamaMetricKeys.GenTpsCompute, genTpsCompute, "tok/s", "Gen TPS");
                    SetMetric(processMetrics, LlamaMetricKeys.BusyPercent, busyPercent, "%", "Busy");
                }

                _states[pid] = new LlamaProcessState
                {
                    Port = port,
                    TokensPredictedTotal = tokensTotal,
                    TokensPredictedSecondsTotal = predictedSecondsTotal,
                    WallTicks = nowTicks
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enrich llama-server metrics for PID {ProcessId}", processMetrics.Info.ProcessId);
        }
        finally
        {
            _httpSemaphore.Release();
        }
    }

    private async Task<string?> FetchMetricsTextAsync(int port, CancellationToken cancellationToken)
    {
        var url = $"http://{MetricsHost}:{port}{MetricsPath}";
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetLlamaMetricsPort(ProcessInfo processInfo, out int port)
    {
        port = default;

        if (!string.Equals(processInfo.ProcessName, LlamaProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commandLine = processInfo.CommandLine;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        if (!LlamaServerCommandLineParser.HasMetricsFlag(commandLine))
        {
            return false;
        }

        return LlamaServerCommandLineParser.TryParsePort(commandLine, out port);
    }

    private void CleanupStates(HashSet<int> livePids)
    {
        if (_states.Count == 0)
        {
            return;
        }

        foreach (var pid in _states.Keys.Where(pid => !livePids.Contains(pid)).ToList())
        {
            _states.Remove(pid);
        }
    }

    private static void SetMetric(ProcessMetrics processMetrics, string metricId, double value, string unit, string displayName)
    {
        processMetrics.Metrics[metricId] = new MetricValue
        {
            Value = value,
            Unit = unit,
            DisplayName = displayName,
            Timestamp = DateTime.UtcNow
        };
    }

    private sealed class LlamaProcessState
    {
        public required int Port { get; init; }
        public required double TokensPredictedTotal { get; init; }
        public required double TokensPredictedSecondsTotal { get; init; }
        public required long WallTicks { get; init; }
    }
}

internal static class LlamaMetricKeys
{
    public const string Port = "llama_port";
    public const string GenTpsCompute = "llama_gen_tps_compute";
    public const string BusyPercent = "llama_busy_percent";
    public const string ReqProcessing = "llama_req_processing";
    public const string ReqDeferred = "llama_req_deferred";
    public const string OutTokensTotal = "llama_out_tokens_total";
}

internal static class LlamaServerCommandLineParser
{
    private static readonly Regex MetricsFlagRegex =
        new(@"(?:^|\s)--metrics(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PortRegex =
        new(@"(?:^|\s)--port(?:\s+|=)(\d{1,5})(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool HasMetricsFlag(string commandLine) => MetricsFlagRegex.IsMatch(commandLine);

    public static bool TryParsePort(string commandLine, out int port)
    {
        port = default;

        var match = PortRegex.Match(commandLine);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed is <= 0 or > 65535)
        {
            return false;
        }

        port = parsed;
        return true;
    }
}

internal readonly struct LlamaPrometheusSnapshot
{
    public double? TokensPredictedTotal { get; init; }
    public double? TokensPredictedSecondsTotal { get; init; }
    public double? RequestsProcessing { get; init; }
    public double? RequestsDeferred { get; init; }
}

internal static class LlamaPrometheusTextParser
{
    private const string TokensPredictedTotalName = "llamacpp:tokens_predicted_total";
    private const string TokensPredictedSecondsTotalName = "llamacpp:tokens_predicted_seconds_total";
    private const string RequestsProcessingName = "llamacpp:requests_processing";
    private const string RequestsDeferredName = "llamacpp:requests_deferred";

    public static bool TryParse(ReadOnlySpan<char> text, out LlamaPrometheusSnapshot snapshot)
    {
        double? tokensPredictedTotal = null;
        double? tokensPredictedSecondsTotal = null;
        double? requestsProcessing = null;
        double? requestsDeferred = null;

        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var lineEnd = text.Slice(lineStart).IndexOf('\n');
            if (lineEnd < 0)
            {
                lineEnd = text.Length - lineStart;
            }

            var line = text.Slice(lineStart, lineEnd).Trim();
            lineStart += lineEnd + 1;

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var spaceIndex = line.IndexOf(' ');
            if (spaceIndex < 0)
            {
                continue;
            }

            var nameSpan = line[..spaceIndex];
            var labelIndex = nameSpan.IndexOf('{');
            if (labelIndex >= 0)
            {
                nameSpan = nameSpan[..labelIndex];
            }

            var valueSpan = line[(spaceIndex + 1)..].Trim();
            if (!double.TryParse(valueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            if (nameSpan.SequenceEqual(TokensPredictedTotalName))
            {
                tokensPredictedTotal = value;
            }
            else if (nameSpan.SequenceEqual(TokensPredictedSecondsTotalName))
            {
                tokensPredictedSecondsTotal = value;
            }
            else if (nameSpan.SequenceEqual(RequestsProcessingName))
            {
                requestsProcessing = value;
            }
            else if (nameSpan.SequenceEqual(RequestsDeferredName))
            {
                requestsDeferred = value;
            }
        }

        snapshot = new LlamaPrometheusSnapshot
        {
            TokensPredictedTotal = tokensPredictedTotal,
            TokensPredictedSecondsTotal = tokensPredictedSecondsTotal,
            RequestsProcessing = requestsProcessing,
            RequestsDeferred = requestsDeferred
        };

        return tokensPredictedTotal.HasValue
               || tokensPredictedSecondsTotal.HasValue
               || requestsProcessing.HasValue
               || requestsDeferred.HasValue;
    }
}

internal static class LlamaDerivedMetricsCalculator
{
    public static bool TryCompute(
        double previousTokensPredictedTotal,
        double previousTokensPredictedSecondsTotal,
        long previousWallTicks,
        double currentTokensPredictedTotal,
        double currentTokensPredictedSecondsTotal,
        long currentWallTicks,
        out double genTpsCompute,
        out double busyPercent)
    {
        genTpsCompute = 0;
        busyPercent = 0;

        if (currentTokensPredictedTotal < previousTokensPredictedTotal)
        {
            return false;
        }

        if (currentTokensPredictedSecondsTotal < previousTokensPredictedSecondsTotal)
        {
            return false;
        }

        if (currentWallTicks <= previousWallTicks)
        {
            return false;
        }

        var deltaTokens = currentTokensPredictedTotal - previousTokensPredictedTotal;
        var deltaPredictSeconds = currentTokensPredictedSecondsTotal - previousTokensPredictedSecondsTotal;
        var deltaWallSeconds = Stopwatch.GetElapsedTime(previousWallTicks, currentWallTicks).TotalSeconds;

        if (deltaWallSeconds <= 0)
        {
            return false;
        }

        if (deltaPredictSeconds <= 0)
        {
            return false;
        }

        genTpsCompute = deltaTokens / deltaPredictSeconds;

        var busy = (deltaPredictSeconds / deltaWallSeconds) * 100.0;
        busyPercent = Math.Clamp(busy, 0, 100);

        return true;
    }
}
