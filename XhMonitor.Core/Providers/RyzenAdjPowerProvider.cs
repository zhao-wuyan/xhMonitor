using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Providers;

public sealed class RyzenAdjPowerProvider : IPowerProvider
{
    private static readonly PowerScheme[] DefaultSchemes =
    [
        new PowerScheme(55, 100, 55),
        new PowerScheme(85, 120, 85),
        new PowerScheme(120, 140, 120)
    ];

    private readonly PowerScheme[] _schemes;
    private readonly IRyzenAdjCli _ryzenAdj;
    private readonly TimeSpan _pollingInterval;
    private readonly ILogger<RyzenAdjPowerProvider>? _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private PowerStatus? _cachedStatus;
    private DateTime _lastAttemptAtUtc = DateTime.MinValue;
    private bool _lastAttemptSucceeded;
    private bool _hasEverSucceeded;
    private int _startupFailureCount;
    private bool _disabled;

    public RyzenAdjPowerProvider(IRyzenAdjCli ryzenAdj, ILogger<RyzenAdjPowerProvider>? logger = null)
        : this(ryzenAdj, TimeSpan.FromSeconds(3), null, logger)
    {
    }

    public RyzenAdjPowerProvider(IRyzenAdjCli ryzenAdj, TimeSpan pollingInterval, ILogger<RyzenAdjPowerProvider>? logger = null)
        : this(ryzenAdj, pollingInterval, null, logger)
    {
    }

    public RyzenAdjPowerProvider(
        IRyzenAdjCli ryzenAdj,
        TimeSpan pollingInterval,
        PowerScheme[]? schemes,
        ILogger<RyzenAdjPowerProvider>? logger = null)
    {
        _ryzenAdj = ryzenAdj ?? throw new ArgumentNullException(nameof(ryzenAdj));
        _pollingInterval = pollingInterval < TimeSpan.Zero ? TimeSpan.Zero : pollingInterval;
        _schemes = schemes is { Length: > 0 } ? schemes : DefaultSchemes;
        _logger = logger;
    }

    public bool IsSupported() => _ryzenAdj.IsAvailable && !_disabled;

    public async Task<PowerStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        if (!IsSupported())
        {
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastAttemptAtUtc < _pollingInterval)
        {
            return _lastAttemptSucceeded ? _cachedStatus : null;
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsSupported())
            {
                return null;
            }

            nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastAttemptAtUtc < _pollingInterval)
            {
                return _lastAttemptSucceeded ? _cachedStatus : null;
            }

            _lastAttemptAtUtc = nowUtc;
            var snapshot = await _ryzenAdj.GetSnapshotAsync(ct).ConfigureAwait(false);
            var limits = SnapshotToLimits(snapshot);
            var schemeIndex = MatchSchemeIndex(limits);

            var status = new PowerStatus(
                CurrentWatts: ConvertToWatts(snapshot.StapmValue),
                LimitWatts: ConvertToWatts(snapshot.StapmLimit),
                SchemeIndex: schemeIndex,
                Limits: limits);

            _cachedStatus = status;
            _lastAttemptSucceeded = true;
            _hasEverSucceeded = true;
            _startupFailureCount = 0;

            return status;
        }
        catch (Exception ex)
        {
            _lastAttemptSucceeded = false;

            if (!_hasEverSucceeded)
            {
                _startupFailureCount++;
                if (_startupFailureCount >= 3)
                {
                    _disabled = true;
                    _logger?.LogWarning(
                        "[RyzenAdjPowerProvider] Disabled power monitoring after {FailureCount} startup failures",
                        _startupFailureCount);
                }
            }

            _logger?.LogError(ex, "[RyzenAdjPowerProvider] Failed to get power status");
            return null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<PowerSchemeSwitchResult> SwitchToNextSchemeAsync(CancellationToken ct = default)
    {
        if (!IsSupported())
        {
            return PowerSchemeSwitchResult.Fail("RyzenAdj not available");
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        PowerScheme? next = null;
        int? currentIndex = null;
        var nextIndex = 0;
        try
        {
            var snapshot = await _ryzenAdj.GetSnapshotAsync(ct).ConfigureAwait(false);
            var currentLimits = SnapshotToLimits(snapshot);
            currentIndex = MatchSchemeIndex(currentLimits);
            nextIndex = currentIndex.HasValue ? (currentIndex.Value + 1) % _schemes.Length : 0;
            next = _schemes[nextIndex];

            await _ryzenAdj.ApplyLimitsAsync(next, ct).ConfigureAwait(false);

            _cachedStatus = new PowerStatus(
                CurrentWatts: ConvertToWatts(snapshot.StapmValue),
                LimitWatts: next.StapmWatts,
                SchemeIndex: nextIndex,
                Limits: next);
            _lastAttemptAtUtc = DateTime.UtcNow;
            _lastAttemptSucceeded = true;
            _hasEverSucceeded = true;
            _startupFailureCount = 0;

            return new PowerSchemeSwitchResult(
                Success: true,
                Message: "OK",
                PreviousSchemeIndex: currentIndex,
                NewSchemeIndex: nextIndex,
                NewScheme: next);
        }
        catch (Exception ex)
        {
            if (next != null)
            {
                var verify = await TryVerifyAppliedAsync(next, ct).ConfigureAwait(false);
                if (verify != null)
                {
                    _cachedStatus = verify;
                    _lastAttemptAtUtc = DateTime.UtcNow;
                    _lastAttemptSucceeded = true;
                    _hasEverSucceeded = true;
                    _startupFailureCount = 0;

                    var verifiedLimits = verify.Limits;
                    var verifiedIndex = MatchSchemeIndex(verifiedLimits) ?? nextIndex;

                    _logger?.LogWarning(
                        ex,
                        "[RyzenAdjPowerProvider] Switch scheme command failed, but limits were applied (verified)");

                    return new PowerSchemeSwitchResult(
                        Success: true,
                        Message: "OK",
                        PreviousSchemeIndex: currentIndex,
                        NewSchemeIndex: verifiedIndex,
                        NewScheme: verifiedLimits);
                }
            }

            _logger?.LogError(ex, "[RyzenAdjPowerProvider] Failed to switch scheme");
            return PowerSchemeSwitchResult.Fail(ex.Message);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<PowerStatus?> TryVerifyAppliedAsync(PowerScheme expected, CancellationToken ct)
    {
        try
        {
            var snapshot = await _ryzenAdj.GetSnapshotAsync(ct).ConfigureAwait(false);
            var limits = SnapshotToLimits(snapshot);

            if (!IsClose(limits.StapmWatts, expected.StapmWatts) ||
                !IsClose(limits.FastWatts, expected.FastWatts) ||
                !IsClose(limits.SlowWatts, expected.SlowWatts))
            {
                return null;
            }

            var schemeIndex = MatchSchemeIndex(limits);
            return new PowerStatus(
                CurrentWatts: ConvertToWatts(snapshot.StapmValue),
                LimitWatts: ConvertToWatts(snapshot.StapmLimit),
                SchemeIndex: schemeIndex,
                Limits: limits);
        }
        catch
        {
            return null;
        }
    }

    private static PowerScheme SnapshotToLimits(RyzenAdjSnapshot snapshot)
    {
        return new PowerScheme(
            StapmWatts: (int)Math.Round(ConvertToWatts(snapshot.StapmLimit)),
            FastWatts: (int)Math.Round(ConvertToWatts(snapshot.FastLimit)),
            SlowWatts: (int)Math.Round(ConvertToWatts(snapshot.SlowLimit)));
    }

    private static double ConvertToWatts(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return 0.0;
        }

        // RyzenAdj 输出在不同平台/版本下可能是 mW 或 W。这里做一个保守判定：
        // - 大于 1000 的值基本可以视为 mW（如 45000mW）
        // - 否则按 W 处理（如 45W）
        return value > 1000 ? value / 1000.0 : value;
    }

    private int? MatchSchemeIndex(PowerScheme limits)
    {
        for (var i = 0; i < _schemes.Length; i++)
        {
            var scheme = _schemes[i];
            if (IsClose(limits.StapmWatts, scheme.StapmWatts) &&
                IsClose(limits.FastWatts, scheme.FastWatts) &&
                IsClose(limits.SlowWatts, scheme.SlowWatts))
            {
                return i;
            }
        }

        return null;
    }

    private static bool IsClose(int a, int b) => Math.Abs(a - b) <= 1;
}
