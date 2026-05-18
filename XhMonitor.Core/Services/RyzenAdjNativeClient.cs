using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

public sealed class RyzenAdjNativeClient : IRyzenAdjCli, IDisposable
{
    private static readonly TimeSpan DefaultInitializationCooldown = TimeSpan.FromSeconds(30);

    private readonly string? _libraryPath;
    private readonly string? _libraryDirectory;
    private readonly ILogger<RyzenAdjNativeClient>? _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly TimeSpan _initializationCooldown;
    private IntPtr _handle;
    private DateTime _lastInitializationFailureAtUtc = DateTime.MinValue;
    private bool _disposed;

    public RyzenAdjNativeClient(string? configuredPath, string baseDirectory, ILogger<RyzenAdjNativeClient>? logger = null)
        : this(configuredPath, baseDirectory, DefaultInitializationCooldown, logger)
    {
    }

    internal RyzenAdjNativeClient(
        string? configuredPath,
        string baseDirectory,
        TimeSpan initializationCooldown,
        ILogger<RyzenAdjNativeClient>? logger = null)
    {
        _libraryPath = ResolveLibraryPath(configuredPath, baseDirectory);
        _libraryDirectory = string.IsNullOrWhiteSpace(_libraryPath)
            ? null
            : Path.GetDirectoryName(_libraryPath);
        _initializationCooldown = initializationCooldown < TimeSpan.Zero
            ? TimeSpan.Zero
            : initializationCooldown;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_libraryPath))
        {
            _logger?.LogInformation("[RyzenAdjNativeClient] Using library: {Path}", _libraryPath);
        }
        else
        {
            _logger?.LogWarning("[RyzenAdjNativeClient] libryzenadj.dll not found. Native power monitoring will be disabled.");
        }
    }

    public bool IsAvailable => OperatingSystem.IsWindows() &&
        !string.IsNullOrWhiteSpace(_libraryPath) &&
        File.Exists(_libraryPath);

    public string? ExecutablePath => _libraryPath;

    public async Task<RyzenAdjSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ryzenAdj = EnsureInitialized();
            using var scope = NativeLibraryDirectoryScope.Enter(_libraryDirectory);
            var refreshResult = NativeMethods.refresh_table(ryzenAdj);
            if (refreshResult != 0)
            {
                throw new InvalidOperationException($"refresh_table failed with code {refreshResult}");
            }

            return new RyzenAdjSnapshot(
                StapmLimit: NativeMethods.get_stapm_limit(ryzenAdj),
                StapmValue: NativeMethods.get_stapm_value(ryzenAdj),
                FastLimit: NativeMethods.get_fast_limit(ryzenAdj),
                FastValue: NativeMethods.get_fast_value(ryzenAdj),
                SlowLimit: NativeMethods.get_slow_limit(ryzenAdj),
                SlowValue: NativeMethods.get_slow_value(ryzenAdj));
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ApplyLimitsAsync(PowerScheme scheme, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ryzenAdj = EnsureInitialized();
            using var scope = NativeLibraryDirectoryScope.Enter(_libraryDirectory);
            ThrowIfNativeError(nameof(NativeMethods.set_stapm_limit), NativeMethods.set_stapm_limit(ryzenAdj, ConvertWattsToMilliwatts(scheme.StapmWatts)));
            ThrowIfNativeError(nameof(NativeMethods.set_fast_limit), NativeMethods.set_fast_limit(ryzenAdj, ConvertWattsToMilliwatts(scheme.FastWatts)));
            ThrowIfNativeError(nameof(NativeMethods.set_slow_limit), NativeMethods.set_slow_limit(ryzenAdj, ConvertWattsToMilliwatts(scheme.SlowWatts)));
        }
        finally
        {
            _mutex.Release();
        }
    }

    private IntPtr EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsAvailable)
        {
            throw new FileNotFoundException("libryzenadj.dll not found. Set Power:RyzenAdjPath or place libryzenadj.dll under tools/RyzenAdj.");
        }

        if (_handle != IntPtr.Zero)
        {
            return _handle;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastInitializationFailureAtUtc < _initializationCooldown)
        {
            throw new InvalidOperationException("Native RyzenAdj initialization is cooling down after a previous failure.");
        }

        using var scope = NativeLibraryDirectoryScope.Enter(_libraryDirectory);

        var handle = NativeMethods.init_ryzenadj();
        if (handle == IntPtr.Zero)
        {
            _lastInitializationFailureAtUtc = nowUtc;
            throw new InvalidOperationException("init_ryzenadj returned null. Run as Administrator and ensure WinRing0x64.sys/WinRing0x64.dll/inpoutx64.dll are in the RyzenAdj directory.");
        }

        _handle = handle;
        _logger?.LogInformation("[RyzenAdjNativeClient] Native RyzenAdj initialized");
        return _handle;
    }

    private static uint ConvertWattsToMilliwatts(int watts)
        => (uint)Math.Max(0, watts) * 1000U;

    private static void ThrowIfNativeError(string functionName, int result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{functionName} failed with code {result}");
        }
    }

    private static string? ResolveLibraryPath(string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim();
            if (Directory.Exists(trimmed))
            {
                var inDir = Path.Combine(trimmed, "libryzenadj.dll");
                if (File.Exists(inDir))
                {
                    return inDir;
                }
            }

            if (File.Exists(trimmed))
            {
                var fileName = Path.GetFileName(trimmed);
                if (string.Equals(fileName, "libryzenadj.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }

                var sibling = Path.Combine(Path.GetDirectoryName(trimmed) ?? string.Empty, "libryzenadj.dll");
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }
        }

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "tools", "RyzenAdj", "libryzenadj.dll"),
            Path.Combine(baseDirectory, "libryzenadj.dll")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.Wait();
        try
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.cleanup_ryzenadj(_handle);
                _handle = IntPtr.Zero;
            }
        }
        finally
        {
            _mutex.Release();
            _mutex.Dispose();
        }
    }

    private sealed class NativeLibraryDirectoryScope : IDisposable
    {
        private NativeLibraryDirectoryScope(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                SetDllDirectory(directory);
            }
        }

        public static NativeLibraryDirectoryScope Enter(string? directory)
            => new(directory);

        public void Dispose()
        {
            SetDllDirectory(null);
        }
    }

    private static partial class NativeMethods
    {
        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern IntPtr init_ryzenadj();

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern void cleanup_ryzenadj(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int refresh_table(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern float get_stapm_limit(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern float get_stapm_value(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern float get_fast_limit(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern float get_fast_value(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern float get_slow_limit(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern float get_slow_value(IntPtr ry);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int set_stapm_limit(IntPtr ry, uint value);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int set_fast_limit(IntPtr ry, uint value);

        [DllImport("libryzenadj.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int set_slow_limit(IntPtr ry, uint value);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string? lpPathName);
}
