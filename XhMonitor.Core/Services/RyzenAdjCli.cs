using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

public sealed class RyzenAdjCli : IRyzenAdjCli
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private readonly ILogger<RyzenAdjCli>? _logger;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);

    public string? ExecutablePath { get; }

    public RyzenAdjCli(string? configuredPath, string baseDirectory, ILogger<RyzenAdjCli>? logger = null)
    {
        _logger = logger;
        ExecutablePath = ResolveExecutablePath(configuredPath, baseDirectory);

        if (!string.IsNullOrWhiteSpace(ExecutablePath))
        {
            _logger?.LogInformation("[RyzenAdjCli] Using executable: {Path}", ExecutablePath);
        }
        else
        {
            _logger?.LogWarning("[RyzenAdjCli] ryzenadj.exe not found. Power monitoring will be disabled.");
        }
    }

    public async Task<RyzenAdjSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        EnsureAvailable();
        var result = await RunAsync("-i", DefaultTimeout, ct).ConfigureAwait(false);

        if (!TryParseInfoOutput(result.StdOut, out var snapshot, out var error))
        {
            throw new InvalidOperationException($"Failed to parse RyzenAdj output: {error}");
        }

        return snapshot;
    }

    public async Task ApplyLimitsAsync(PowerScheme scheme, CancellationToken ct = default)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(scheme);

        var stapmMw = ConvertWattsToMilliwatts(scheme.StapmWatts);
        var fastMw = ConvertWattsToMilliwatts(scheme.FastWatts);
        var slowMw = ConvertWattsToMilliwatts(scheme.SlowWatts);

        var args = $"--stapm-limit={stapmMw} --fast-limit={fastMw} --slow-limit={slowMw}";
        _ = await RunAsync(args, DefaultTimeout, ct).ConfigureAwait(false);
    }

    private static int ConvertWattsToMilliwatts(int watts)
        => Math.Max(0, watts) * 1000;

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("ryzenadj.exe not found. Set Power:RyzenAdjPath or place ryzenadj.exe under tools/RyzenAdj.");
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var exeDir = string.Empty;
        try
        {
            exeDir = Path.GetDirectoryName(ExecutablePath!) ?? string.Empty;
        }
        catch
        {
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath!,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(exeDir) ? Environment.CurrentDirectory : exeDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };

        var started = process.Start();
        if (!started)
        {
            throw new InvalidOperationException("Failed to start ryzenadj.exe");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            throw new TimeoutException($"ryzenadj.exe timed out after {timeout.TotalSeconds:F1}s. Args: {arguments}");
        }

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var stdOutPreview = TruncateForLog(stdOut, 800);
            var stdErrPreview = TruncateForLog(stdErr, 800);
            _logger?.LogWarning(
                "[RyzenAdjCli] ExitCode={ExitCode}. StdErr={StdErr}. StdOut={StdOut}",
                process.ExitCode,
                stdErrPreview,
                stdOutPreview);

            var hint = process.ExitCode == -1 && string.IsNullOrWhiteSpace(stdErr) && string.IsNullOrWhiteSpace(stdOut)
                ? "Hint: run the service as Administrator and ensure ryzenadj.exe is placed with WinRing0x64.dll/WinRing0x64.sys/inpoutx64.dll in the same directory."
                : string.Empty;

            throw new InvalidOperationException(
                $"ryzenadj.exe exited with code {process.ExitCode}. StdErr: {stdErrPreview}. StdOut: {stdOutPreview}. {hint}".Trim());
        }

        return (process.ExitCode, stdOut, stdErr);
    }

    private static string TruncateForLog(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (maxChars <= 0)
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n");
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..maxChars] + "...(truncated)";
    }

    private static string? ResolveExecutablePath(string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim();
            if (Directory.Exists(trimmed))
            {
                var inDir = Path.Combine(trimmed, "ryzenadj.exe");
                if (File.Exists(inDir))
                {
                    return inDir;
                }
            }

            if (File.Exists(trimmed))
            {
                return trimmed;
            }
        }

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "tools", "RyzenAdj", "ryzenadj.exe"),
            Path.Combine(baseDirectory, "ryzenadj.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var part in pathEnv.Split(Path.PathSeparator))
        {
            var dir = part.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir, "ryzenadj.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    public static bool TryParseInfoOutput(string output, out RyzenAdjSnapshot snapshot, out string error)
    {
        snapshot = new RyzenAdjSnapshot(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(output))
        {
            error = "Empty output";
            return false;
        }

        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|') || trimmed.StartsWith("|---", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[0];
            var valueText = parts[1];

            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            values[name] = value;
        }

        bool TryGet(string key, out double value)
        {
            value = 0;
            return values.TryGetValue(key, out value) && !double.IsNaN(value);
        }

        if (!TryGet("STAPM LIMIT", out var stapmLimit) ||
            !TryGet("STAPM VALUE", out var stapmValue) ||
            !TryGet("PPT LIMIT FAST", out var fastLimit) ||
            !TryGet("PPT VALUE FAST", out var fastValue) ||
            !TryGet("PPT LIMIT SLOW", out var slowLimit) ||
            !TryGet("PPT VALUE SLOW", out var slowValue))
        {
            error = "Missing required keys in output table";
            return false;
        }

        snapshot = new RyzenAdjSnapshot(
            stapmLimit,
            stapmValue,
            fastLimit,
            fastValue,
            slowLimit,
            slowValue);
        return true;
    }
}
