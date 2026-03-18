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
                if (string.Equals(processMetrics.Info.ProcessName, LlamaProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "llama-server 进程未解析到 metrics 端口，跳过 enrich。PID={ProcessId}, CommandLine={CommandLine}",
                        processMetrics.Info.ProcessId,
                        processMetrics.Info.CommandLine);
                }
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

            if (snapshot.PromptTokensPerSecond.HasValue)
            {
                SetMetric(processMetrics, LlamaMetricKeys.PromptTpsAvg, snapshot.PromptTokensPerSecond.Value, "tok/s", "Prompt TPS Avg");
            }

            if (snapshot.PredictedTokensPerSecond.HasValue)
            {
                SetMetric(processMetrics, LlamaMetricKeys.GenTpsAvg, snapshot.PredictedTokensPerSecond.Value, "tok/s", "Gen TPS Avg");
            }

            if (snapshot.TokensPredictedTotal.HasValue)
            {
                SetMetric(processMetrics, LlamaMetricKeys.OutTokensTotal, snapshot.TokensPredictedTotal.Value, "tok", "Out Tokens");
            }

            if (snapshot.DecodeTotal.HasValue)
            {
                SetMetric(processMetrics, LlamaMetricKeys.DecodeTotal, snapshot.DecodeTotal.Value, "calls", "Decode");
            }

            if (snapshot.TokensPredictedTotal.HasValue && snapshot.TokensPredictedSecondsTotal.HasValue)
            {
                var tokensTotal = snapshot.TokensPredictedTotal.Value;
                var predictedSecondsTotal = snapshot.TokensPredictedSecondsTotal.Value;
                var outTokensLive = tokensTotal;

                if (_states.TryGetValue(pid, out var stateSnapshot)
                    && stateSnapshot != null
                    && stateSnapshot.Port == port)
                {
                    var isBusy = IsBusySample(stateSnapshot, snapshot);

                    if (tokensTotal.Equals(stateSnapshot.TokensPredictedTotal))
                    {
                        outTokensLive = stateSnapshot.OutTokensLive;
                        if (snapshot.DecodeTotal.HasValue
                            && stateSnapshot.DecodeTotal.HasValue
                            && snapshot.DecodeTotal.Value > stateSnapshot.DecodeTotal.Value)
                        {
                            outTokensLive += snapshot.DecodeTotal.Value - stateSnapshot.DecodeTotal.Value;
                        }
                    }
                    else
                    {
                        outTokensLive = tokensTotal;
                    }

                    if (!isBusy)
                    {
                        outTokensLive = tokensTotal;
                    }

                    outTokensLive = Math.Max(outTokensLive, tokensTotal);

                    SetMetric(processMetrics, LlamaMetricKeys.OutTokensLive, outTokensLive, "tok", "Out Tokens Live");

                    var deltaWallSeconds = Stopwatch.GetElapsedTime(stateSnapshot.WallTicks, nowTicks).TotalSeconds;
                    if (deltaWallSeconds > 0)
                    {
                        var deltaLiveTokens = Math.Max(0, outTokensLive - stateSnapshot.OutTokensLive);
                        var genTpsLive = deltaLiveTokens / deltaWallSeconds;
                        SetMetric(processMetrics, LlamaMetricKeys.GenTpsLive, genTpsLive, "tok/s", "Gen TPS Live");
                        SetMetric(processMetrics, LlamaMetricKeys.BusyPercentLive, deltaLiveTokens > 0 ? 100 : 0, "%", "Busy Live");
                    }

                    if (LlamaDerivedMetricsCalculator.TryComputeOrZeroWhenIdle(
                            stateSnapshot.TokensPredictedTotal,
                            stateSnapshot.TokensPredictedSecondsTotal,
                            stateSnapshot.WallTicks,
                            tokensTotal,
                            predictedSecondsTotal,
                            nowTicks,
                            isBusy: isBusy,
                            out var genTpsCompute,
                            out var busyPercent))
                    {
                        SetMetric(processMetrics, LlamaMetricKeys.GenTpsCompute, genTpsCompute, "tok/s", "Gen TPS");
                        SetMetric(processMetrics, LlamaMetricKeys.BusyPercent, busyPercent, "%", "Busy");
                    }
                    else
                    {
                        processMetrics.Metrics.Remove(LlamaMetricKeys.GenTpsCompute);
                        processMetrics.Metrics.Remove(LlamaMetricKeys.BusyPercent);
                    }
                }
                else
                {
                    SetMetric(processMetrics, LlamaMetricKeys.OutTokensLive, tokensTotal, "tok", "Out Tokens Live");
                    processMetrics.Metrics.Remove(LlamaMetricKeys.GenTpsCompute);
                    processMetrics.Metrics.Remove(LlamaMetricKeys.BusyPercent);
                    processMetrics.Metrics.Remove(LlamaMetricKeys.GenTpsLive);
                    processMetrics.Metrics.Remove(LlamaMetricKeys.BusyPercentLive);
                }

                _states[pid] = new LlamaProcessState
                {
                    Port = port,
                    TokensPredictedTotal = tokensTotal,
                    TokensPredictedSecondsTotal = predictedSecondsTotal,
                    WallTicks = nowTicks,
                    DecodeTotal = snapshot.DecodeTotal,
                    OutTokensLive = outTokensLive
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
                _logger.LogDebug("llama metrics 请求返回非成功状态码。Port={Port}, StatusCode={StatusCode}", port, (int)response.StatusCode);
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
            _logger.LogDebug("llama metrics 请求失败。Port={Port}", port);
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

        return LlamaServerCommandLineParser.TryResolveMetricsPort(commandLine, out port);
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

    private static bool IsBusySample(LlamaProcessState state, LlamaPrometheusSnapshot snapshot)
    {
        // 某些 llama-server 构建下 `llamacpp:requests_processing` 不可靠（可能长期保持非 0）。
        // Busy 判定以“计数器是否增长”为准，避免输出结束后仍保持 busy。
        return (snapshot.DecodeTotal.HasValue
                && state.DecodeTotal.HasValue
                && snapshot.DecodeTotal.Value > state.DecodeTotal.Value)
               || (snapshot.TokensPredictedTotal.HasValue
                   && snapshot.TokensPredictedTotal.Value > state.TokensPredictedTotal)
               || (snapshot.TokensPredictedSecondsTotal.HasValue
                   && snapshot.TokensPredictedSecondsTotal.Value > state.TokensPredictedSecondsTotal);
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
        public double? DecodeTotal { get; init; }
        public required double OutTokensLive { get; init; }
    }
}

internal static class LlamaMetricKeys
{
    public const string Port = "llama_port";
    public const string GenTpsCompute = "llama_gen_tps_compute";
    public const string GenTpsAvg = "llama_gen_tps_avg";
    public const string PromptTpsAvg = "llama_prompt_tps_avg";
    public const string BusyPercent = "llama_busy_percent";
    public const string GenTpsLive = "llama_gen_tps_live";
    public const string BusyPercentLive = "llama_busy_percent_live";
    public const string ReqProcessing = "llama_req_processing";
    public const string ReqDeferred = "llama_req_deferred";
    public const string OutTokensTotal = "llama_out_tokens_total";
    public const string OutTokensLive = "llama_out_tokens_live";
    public const string DecodeTotal = "llama_decode_total";
}

internal static class LlamaServerCommandLineParser
{
    internal const int DefaultPort = 8080;

    private static readonly Regex MetricsFlagRegex =
        new(@"(?:^|\s)--metrics(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PortRegex =
        new(@"(?:^|\s)(?:--port|-p)(?:\s+|=)(\d{1,5})(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool HasMetricsFlag(string commandLine) => MetricsFlagRegex.IsMatch(commandLine);

    public static bool TryResolveMetricsPort(string commandLine, out int port)
    {
        if (TryParsePort(commandLine, out port))
        {
            return true;
        }

        if (HasMetricsFlag(commandLine))
        {
            port = DefaultPort;
            return true;
        }

        port = default;
        return false;
    }

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
    public double? PredictedTokensPerSecond { get; init; }
    public double? PromptTokensPerSecond { get; init; }
    public double? RequestsProcessing { get; init; }
    public double? RequestsDeferred { get; init; }
    public double? DecodeTotal { get; init; }
}

internal static class LlamaPrometheusTextParser
{
    private const string TokensPredictedTotalName = "llamacpp:tokens_predicted_total";
    private const string TokensPredictedSecondsTotalName = "llamacpp:tokens_predicted_seconds_total";
    private const string PredictedTokensPerSecondName = "llamacpp:predicted_tokens_seconds";
    private const string PromptTokensPerSecondName = "llamacpp:prompt_tokens_seconds";
    private const string RequestsProcessingName = "llamacpp:requests_processing";
    private const string RequestsDeferredName = "llamacpp:requests_deferred";
    private const string DecodeTotalName = "llamacpp:n_decode_total";

    public static bool TryParse(ReadOnlySpan<char> text, out LlamaPrometheusSnapshot snapshot)
    {
        double? tokensPredictedTotal = null;
        double? tokensPredictedSecondsTotal = null;
        double? predictedTokensPerSecond = null;
        double? promptTokensPerSecond = null;
        double? requestsProcessing = null;
        double? requestsDeferred = null;
        double? decodeTotal = null;

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
            else if (nameSpan.SequenceEqual(PredictedTokensPerSecondName))
            {
                predictedTokensPerSecond = value;
            }
            else if (nameSpan.SequenceEqual(PromptTokensPerSecondName))
            {
                promptTokensPerSecond = value;
            }
            else if (nameSpan.SequenceEqual(RequestsProcessingName))
            {
                requestsProcessing = value;
            }
            else if (nameSpan.SequenceEqual(RequestsDeferredName))
            {
                requestsDeferred = value;
            }
            else if (nameSpan.SequenceEqual(DecodeTotalName))
            {
                decodeTotal = value;
            }
        }

        snapshot = new LlamaPrometheusSnapshot
        {
            TokensPredictedTotal = tokensPredictedTotal,
            TokensPredictedSecondsTotal = tokensPredictedSecondsTotal,
            PredictedTokensPerSecond = predictedTokensPerSecond,
            PromptTokensPerSecond = promptTokensPerSecond,
            RequestsProcessing = requestsProcessing,
            RequestsDeferred = requestsDeferred,
            DecodeTotal = decodeTotal
        };

        return tokensPredictedTotal.HasValue
               || tokensPredictedSecondsTotal.HasValue
               || predictedTokensPerSecond.HasValue
               || promptTokensPerSecond.HasValue
               || requestsProcessing.HasValue
               || requestsDeferred.HasValue
               || decodeTotal.HasValue;
    }
}

internal static class LlamaDerivedMetricsCalculator
{
    public static bool TryComputeOrZeroWhenIdle(
        double previousTokensPredictedTotal,
        double previousTokensPredictedSecondsTotal,
        long previousWallTicks,
        double currentTokensPredictedTotal,
        double currentTokensPredictedSecondsTotal,
        long currentWallTicks,
        bool isBusy,
        out double genTpsCompute,
        out double busyPercent)
    {
        if (TryCompute(
                previousTokensPredictedTotal,
                previousTokensPredictedSecondsTotal,
                previousWallTicks,
                currentTokensPredictedTotal,
                currentTokensPredictedSecondsTotal,
                currentWallTicks,
                out genTpsCompute,
                out busyPercent))
        {
            return true;
        }

        if (isBusy)
        {
            genTpsCompute = 0;
            busyPercent = 0;
            return false;
        }

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

        if (deltaTokens != 0 || deltaPredictSeconds != 0)
        {
            return false;
        }

        // 空闲：t0 到 t1 间无计算发生，避免保留上一次的非 0 值导致误读。
        return true;
    }

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
