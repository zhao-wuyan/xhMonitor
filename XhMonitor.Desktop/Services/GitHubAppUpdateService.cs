using System.IO;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using XhMonitor.Desktop.Configuration;
using XhMonitor.Desktop.Models;

namespace XhMonitor.Desktop.Services;

public sealed class GitHubAppUpdateService : IAppUpdateService
{
    private static readonly Regex VersionRegex = new(@"(?<!\d)(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly TimeSpan DownloadProgressUpdateInterval = TimeSpan.FromMilliseconds(250);
    private const string InstallerSearchPattern = "XhMonitor-v*-Lite-Setup.exe";
    private const string NoNewVersionMessage = "未找到新版本";
    private const int DownloadBufferSize = 128 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppVersionService _appVersionService;
    private readonly ITrayIconService _trayIconService;
    private readonly IInstallerLauncher _installerLauncher;
    private readonly ILogger<GitHubAppUpdateService> _logger;
    private readonly AppUpdateOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private AppUpdateStatus _currentStatus;
    private ResolvedUpdateRelease? _latestRelease;

    public GitHubAppUpdateService(
        IHttpClientFactory httpClientFactory,
        IAppVersionService appVersionService,
        ITrayIconService trayIconService,
        IInstallerLauncher installerLauncher,
        IOptions<AppUpdateOptions> options,
        ILogger<GitHubAppUpdateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _appVersionService = appVersionService;
        _trayIconService = trayIconService;
        _installerLauncher = installerLauncher;
        _logger = logger;
        _options = options.Value;
        _currentStatus = CreateStatus(AppUpdateState.Idle);
    }

    public event EventHandler? StatusChanged;

    public AppUpdateStatus CurrentStatus => _currentStatus;

    public async Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(CreateStatus(AppUpdateState.Checking, "正在检查更新……"));
            var currentVersion = _appVersionService.CurrentVersion;
            CleanupInstallerCache(currentVersion);

            var release = await TryGetManagedLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release == null)
            {
                _latestRelease = null;
                SetStatus(CreateSourceUnavailableStatus(
                    $"未找到 tag 为 {_options.PreferredReleaseTag} 的 release。"));
                return _currentStatus;
            }

            if (!TryResolveRelease(release, out var resolvedRelease, out var resolveError))
            {
                _latestRelease = null;
                SetStatus(CreateSourceUnavailableStatus(
                    resolveError ?? "latest release 缺少可用安装包。"));
                return _currentStatus;
            }

            if (resolvedRelease.Version <= currentVersion)
            {
                _latestRelease = null;
                SetStatus(CreateStatus(
                    AppUpdateState.UpToDate,
                    $"当前已是最新版本：v{FormatVersion(currentVersion)}",
                    resolvedRelease.VersionText,
                    resolvedRelease.ReleaseTag,
                    resolvedRelease.AssetName));
                return _currentStatus;
            }

            _latestRelease = resolvedRelease;
            CleanupInstallerCache(currentVersion, resolvedRelease.InstallerPath);

            if (File.Exists(resolvedRelease.InstallerPath))
            {
                SetStatus(CreateStatus(
                    AppUpdateState.Downloaded,
                    $"已下载新版本：v{resolvedRelease.VersionText}，可直接安装",
                    resolvedRelease.VersionText,
                    resolvedRelease.ReleaseTag,
                    resolvedRelease.AssetName,
                    resolvedRelease.InstallerPath));
                return _currentStatus;
            }

            SetStatus(CreateStatus(
                AppUpdateState.UpdateAvailable,
                $"新版本：v{resolvedRelease.VersionText}",
                resolvedRelease.VersionText,
                resolvedRelease.ReleaseTag,
                resolvedRelease.AssetName));
            return _currentStatus;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for updates.");
            _latestRelease = null;
            SetStatus(CreateStatus(AppUpdateState.Error, $"检查更新失败：{ex.Message}"));
            return _currentStatus;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.CheckOnStartup)
        {
            return;
        }

        try
        {
            var status = await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            if (status.HasUpdate)
            {
                _trayIconService.ShowUpdateAvailableNotification(status);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Startup update check failed.");
        }
    }

    public async Task<AppUpdateStatus> DownloadUpdateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? tempFilePath = null;
        try
        {
            if (_latestRelease == null)
            {
                return _currentStatus;
            }

            var release = _latestRelease;
            SetStatus(CreateStatus(
                AppUpdateState.Downloading,
                $"正在下载新版本：v{release.VersionText}",
                release.VersionText,
                release.ReleaseTag,
                release.AssetName));

            var downloadDirectory = Path.GetDirectoryName(release.InstallerPath);
            if (string.IsNullOrWhiteSpace(downloadDirectory))
            {
                throw new InvalidOperationException("无法确定下载目录。");
            }

            Directory.CreateDirectory(downloadDirectory);
            CleanupInstallerCache(_appVersionService.CurrentVersion, release.InstallerPath);

            tempFilePath = $"{release.InstallerPath}.download";
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }

            var client = CreateHttpClient();
            using var response = await client.GetAsync(
                release.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await DownloadInstallerAsync(
                release,
                responseStream,
                tempFilePath,
                response.Content.Headers.ContentLength,
                cancellationToken).ConfigureAwait(false);

            File.Move(tempFilePath, release.InstallerPath, true);
            tempFilePath = null;

            SetStatus(CreateStatus(
                AppUpdateState.Downloaded,
                $"已下载新版本：v{release.VersionText}，正在启动安装程序",
                release.VersionText,
                release.ReleaseTag,
                release.AssetName,
                release.InstallerPath));

            await LaunchInstallerCoreAsync(release.InstallerPath, cancellationToken).ConfigureAwait(false);
            return _currentStatus;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download update.");
            if (!string.IsNullOrWhiteSpace(tempFilePath))
            {
                TryDeleteFile(tempFilePath);
            }

            var latestVersion = _latestRelease?.VersionText;
            var latestTag = _latestRelease?.ReleaseTag;
            var assetName = _latestRelease?.AssetName;

            SetStatus(CreateStatus(
                AppUpdateState.Error,
                $"下载更新失败：{ex.Message}",
                latestVersion,
                latestTag,
                assetName));
            return _currentStatus;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadInstallerAsync(
        ResolvedUpdateRelease release,
        Stream responseStream,
        string tempFilePath,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DownloadBufferSize];
        long downloadedBytes = 0;
        long lastReportedBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReportElapsed = TimeSpan.Zero;

        await using var fileStream = new FileStream(
            tempFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            useAsync: true);

        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            downloadedBytes += bytesRead;

            var elapsed = stopwatch.Elapsed;
            if (!ShouldReportDownloadProgress(downloadedBytes, totalBytes, elapsed, lastReportElapsed))
            {
                continue;
            }

            ReportDownloadProgress(release, downloadedBytes, totalBytes, elapsed, lastReportedBytes, lastReportElapsed);
            lastReportedBytes = downloadedBytes;
            lastReportElapsed = elapsed;
        }

        if (downloadedBytes > lastReportedBytes)
        {
            ReportDownloadProgress(
                release,
                downloadedBytes,
                totalBytes,
                stopwatch.Elapsed,
                lastReportedBytes,
                lastReportElapsed);
        }
    }

    public async Task<bool> LaunchInstallerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_latestRelease == null || !File.Exists(_latestRelease.InstallerPath))
            {
                return false;
            }

            return await LaunchInstallerCoreAsync(_latestRelease.InstallerPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> LaunchInstallerCoreAsync(string installerPath, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var launched = _installerLauncher.TryLaunch(installerPath, out var message);
        SetStatus(_currentStatus with
        {
            Message = message
        });
        return launched;
    }

    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds));
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"XhMonitor/{_appVersionService.CurrentVersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private async Task<GitHubReleaseDto?> TryGetManagedLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var owner = (_options.Owner ?? string.Empty).Trim();
        var repository = (_options.Repository ?? string.Empty).Trim();
        var releaseTag = (_options.PreferredReleaseTag ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repository) ||
            string.IsNullOrWhiteSpace(releaseTag))
        {
            return null;
        }

        var client = CreateHttpClient();
        var releaseUri = new Uri(
            $"https://api.github.com/repos/{owner}/{repository}/releases/tags/{Uri.EscapeDataString(releaseTag)}");

        using var response = await client.GetAsync(releaseUri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(
            contentStream,
            _jsonSerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private bool TryResolveRelease(
        GitHubReleaseDto release,
        out ResolvedUpdateRelease resolvedRelease,
        out string? errorMessage)
    {
        resolvedRelease = default!;
        errorMessage = null;

        var latestVersion = TryExtractVersion(release.Name)
            ?? TryExtractVersion(release.TagName);

        var liteAsset = default(GitHubAssetDto);
        foreach (var asset in release.Assets ?? Array.Empty<GitHubAssetDto>())
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.Name))
            {
                continue;
            }

            if (!asset.Name.EndsWith("-Lite-Setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            liteAsset = asset;
            latestVersion ??= TryExtractVersion(asset.Name);
            break;
        }

        if (latestVersion == null)
        {
            errorMessage = "latest release 中未解析到版本号。";
            return false;
        }

        var expectedAssetName = (_options.TargetAssetTemplate ?? string.Empty)
            .Replace("{version}", latestVersion, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(expectedAssetName))
        {
            errorMessage = "更新资产命名模板无效。";
            return false;
        }

        var installerAsset = (release.Assets ?? Array.Empty<GitHubAssetDto>())
            .FirstOrDefault(asset =>
                string.Equals(asset.Name, expectedAssetName, StringComparison.OrdinalIgnoreCase));

        if (installerAsset == null && liteAsset != null &&
            string.Equals(liteAsset.Name, expectedAssetName, StringComparison.OrdinalIgnoreCase))
        {
            installerAsset = liteAsset;
        }

        if (installerAsset == null || string.IsNullOrWhiteSpace(installerAsset.BrowserDownloadUrl))
        {
            errorMessage = $"latest release 中未找到安装包 {expectedAssetName}。";
            return false;
        }

        if (!Version.TryParse(latestVersion, out var parsedVersion))
        {
            errorMessage = $"latest release 版本号无效：{latestVersion}";
            return false;
        }

        var versionText = FormatVersion(parsedVersion);
        var downloadDirectory = ResolveDownloadDirectory();
        var installerPath = Path.Combine(downloadDirectory, installerAsset.Name);

        resolvedRelease = new ResolvedUpdateRelease(
            parsedVersion,
            versionText,
            release.TagName ?? _options.PreferredReleaseTag,
            installerAsset.Name,
            installerAsset.BrowserDownloadUrl,
            installerPath);
        return true;
    }

    private static string? TryExtractVersion(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var match = VersionRegex.Match(rawText);
        if (!match.Success)
        {
            return null;
        }

        if (!Version.TryParse(match.Groups[1].Value, out var version))
        {
            return null;
        }

        return FormatVersion(version);
    }

    private string ResolveDownloadDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.DownloadDirectory))
        {
            return Path.GetFullPath(_options.DownloadDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XhMonitor",
            "Updates");
    }

    private void CleanupInstallerCache(Version currentVersion, string? keepInstallerPath = null)
    {
        var downloadDirectory = ResolveDownloadDirectory();
        if (!Directory.Exists(downloadDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(downloadDirectory, InstallerSearchPattern))
        {
            if (ShouldKeepInstaller(file, currentVersion, keepInstallerPath))
            {
                continue;
            }

            TryDeleteFile(file);
        }

        foreach (var tempFile in Directory.GetFiles(downloadDirectory, "*.download"))
        {
            if (!string.IsNullOrWhiteSpace(keepInstallerPath) &&
                string.Equals(tempFile, $"{keepInstallerPath}.download", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(tempFile);
        }
    }

    private static bool ShouldKeepInstaller(string installerPath, Version currentVersion, string? keepInstallerPath)
    {
        if (!string.IsNullOrWhiteSpace(keepInstallerPath) &&
            string.Equals(installerPath, keepInstallerPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var installerVersionText = TryExtractVersion(Path.GetFileName(installerPath));
        if (installerVersionText == null || !Version.TryParse(installerVersionText, out var installerVersion))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(keepInstallerPath) && installerVersion > currentVersion;
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private AppUpdateStatus CreateStatus(
        AppUpdateState state,
        string? message = null,
        string? latestVersion = null,
        string? releaseTag = null,
        string? assetName = null,
        string? installerPath = null)
    {
        return new AppUpdateStatus
        {
            State = state,
            CurrentVersion = _appVersionService.CurrentVersionText,
            LatestVersion = latestVersion,
            ReleaseTag = releaseTag,
            InstallerAssetName = assetName,
            DownloadedInstallerPath = installerPath,
            Message = message
        };
    }

    private AppUpdateStatus CreateSourceUnavailableStatus(string diagnosticMessage)
    {
        _logger.LogInformation("Update source unavailable: {DiagnosticMessage}", diagnosticMessage);
        return CreateStatus(AppUpdateState.SourceUnavailable, NoNewVersionMessage);
    }

    private void SetStatus(AppUpdateStatus status)
    {
        _currentStatus = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReportDownloadProgress(
        ResolvedUpdateRelease release,
        long downloadedBytes,
        long? totalBytes,
        TimeSpan elapsed,
        long lastReportedBytes,
        TimeSpan lastReportElapsed)
    {
        SetStatus(CreateStatus(
            AppUpdateState.Downloading,
            BuildDownloadProgressMessage(
                downloadedBytes,
                totalBytes,
                CalculateDownloadSpeedBytesPerSecond(downloadedBytes, elapsed, lastReportedBytes, lastReportElapsed)),
            release.VersionText,
            release.ReleaseTag,
            release.AssetName));
    }

    private static bool ShouldReportDownloadProgress(
        long downloadedBytes,
        long? totalBytes,
        TimeSpan elapsed,
        TimeSpan lastReportElapsed)
    {
        if (downloadedBytes <= 0)
        {
            return false;
        }

        if (totalBytes is > 0 && downloadedBytes >= totalBytes.Value)
        {
            return true;
        }

        return elapsed - lastReportElapsed >= DownloadProgressUpdateInterval;
    }

    private static double CalculateDownloadSpeedBytesPerSecond(
        long downloadedBytes,
        TimeSpan elapsed,
        long lastReportedBytes,
        TimeSpan lastReportElapsed)
    {
        var deltaBytes = downloadedBytes - lastReportedBytes;
        var deltaSeconds = (elapsed - lastReportElapsed).TotalSeconds;
        if (deltaBytes > 0 && deltaSeconds > 0)
        {
            return deltaBytes / deltaSeconds;
        }

        if (downloadedBytes <= 0 || elapsed.TotalSeconds <= 0)
        {
            return 0;
        }

        return downloadedBytes / elapsed.TotalSeconds;
    }

    private static string BuildDownloadProgressMessage(long downloadedBytes, long? totalBytes, double speedBytesPerSecond)
    {
        var downloadedText = CompactUnitFormatter.FormatSizeFromBytes(downloadedBytes);
        var speedText = CompactUnitFormatter.FormatSpeedFromBytesPerSecond(speedBytesPerSecond);

        if (totalBytes is > 0)
        {
            var totalText = CompactUnitFormatter.FormatSizeFromBytes(totalBytes.Value);
            var progressText = CompactUnitFormatter.FormatPercent(downloadedBytes * 100d / totalBytes.Value);
            return $"已下载 {downloadedText} / {totalText}（{progressText}），速度 {speedText}";
        }

        return $"已下载 {downloadedText}，速度 {speedText}";
    }

    private static string FormatVersion(Version version)
    {
        var build = version.Build >= 0 ? version.Build : 0;
        return $"{version.Major}.{version.Minor}.{build}";
    }

    private sealed record ResolvedUpdateRelease(
        Version Version,
        string VersionText,
        string ReleaseTag,
        string AssetName,
        string DownloadUrl,
        string InstallerPath);

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        public string? Name { get; set; }

        public GitHubAssetDto[]? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }
}
