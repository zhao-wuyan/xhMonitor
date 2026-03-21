using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XhMonitor.Desktop.Configuration;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.Tests;

public sealed class GitHubAppUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ShouldStopWhenLatestReleaseMissing()
    {
        using var tempDir = new TemporaryDirectory();
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var service = CreateService(handler, tempDir.Path);

        var status = await service.CheckForUpdatesAsync();

        status.State.Should().Be(AppUpdateState.SourceUnavailable);
        status.Message.Should().Be("未找到新版本");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldHideTechnicalReason_WhenLatestReleaseIsInvalid()
    {
        using var tempDir = new TemporaryDirectory();
        using var handler = new FakeHttpMessageHandler(_ => CreateJsonResponse("""
        {
          "tag_name": "latest",
          "name": "release-without-version",
          "assets": [
            {
              "name": "README.txt",
              "browser_download_url": "https://example.com/download/README.txt"
            }
          ]
        }
        """));

        var service = CreateService(handler, tempDir.Path);

        var status = await service.CheckForUpdatesAsync();

        status.State.Should().Be(AppUpdateState.SourceUnavailable);
        status.Message.Should().Be("未找到新版本");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldReturnUpdateAvailable_WhenLatestReleaseHasNewerLiteInstaller()
    {
        using var tempDir = new TemporaryDirectory();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Contain("/releases/tags/latest");
            return CreateJsonResponse("""
            {
              "tag_name": "latest",
              "name": "v0.2.13",
              "assets": [
                {
                  "name": "XhMonitor-v0.2.13-Lite-Setup.exe",
                  "browser_download_url": "https://example.com/download/XhMonitor-v0.2.13-Lite-Setup.exe"
                }
              ]
            }
            """);
        });

        var service = CreateService(handler, tempDir.Path);

        var status = await service.CheckForUpdatesAsync();

        status.State.Should().Be(AppUpdateState.UpdateAvailable);
        status.LatestVersion.Should().Be("0.2.13");
        status.InstallerAssetName.Should().Be("XhMonitor-v0.2.13-Lite-Setup.exe");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldReturnUpToDate_AndDeleteInstallerMatchingCurrentVersion()
    {
        using var tempDir = new TemporaryDirectory();
        var cachedInstallerPath = Path.Combine(tempDir.Path, "XhMonitor-v0.2.9-Lite-Setup.exe");
        await File.WriteAllBytesAsync(cachedInstallerPath, [1, 2, 3]);

        using var handler = new FakeHttpMessageHandler(_ => CreateJsonResponse("""
        {
          "tag_name": "latest",
          "name": "v0.2.9",
          "assets": [
            {
              "name": "XhMonitor-v0.2.9-Lite-Setup.exe",
              "browser_download_url": "https://example.com/download/XhMonitor-v0.2.9-Lite-Setup.exe"
            }
          ]
        }
        """));

        var service = CreateService(handler, tempDir.Path);

        var status = await service.CheckForUpdatesAsync();

        status.State.Should().Be(AppUpdateState.UpToDate);
        status.Message.Should().Contain("当前已是最新版本");
        File.Exists(cachedInstallerPath).Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldDeleteOlderInstallers_AndKeepLatestDownloadedInstaller()
    {
        using var tempDir = new TemporaryDirectory();
        var staleInstallerPath = Path.Combine(tempDir.Path, "XhMonitor-v0.2.8-Lite-Setup.exe");
        var currentInstallerPath = Path.Combine(tempDir.Path, "XhMonitor-v0.2.9-Lite-Setup.exe");
        var latestInstallerPath = Path.Combine(tempDir.Path, "XhMonitor-v0.2.13-Lite-Setup.exe");
        await File.WriteAllBytesAsync(staleInstallerPath, [1]);
        await File.WriteAllBytesAsync(currentInstallerPath, [2]);
        await File.WriteAllBytesAsync(latestInstallerPath, [3]);

        using var handler = new FakeHttpMessageHandler(_ => CreateJsonResponse("""
        {
          "tag_name": "latest",
          "name": "v0.2.13",
          "assets": [
            {
              "name": "XhMonitor-v0.2.13-Lite-Setup.exe",
              "browser_download_url": "https://example.com/download/XhMonitor-v0.2.13-Lite-Setup.exe"
            }
          ]
        }
        """));

        var service = CreateService(handler, tempDir.Path);

        var status = await service.CheckForUpdatesAsync();

        status.State.Should().Be(AppUpdateState.Downloaded);
        status.DownloadedInstallerPath.Should().Be(latestInstallerPath);
        File.Exists(staleInstallerPath).Should().BeFalse();
        File.Exists(currentInstallerPath).Should().BeFalse();
        File.Exists(latestInstallerPath).Should().BeTrue();
    }

    [Fact]
    public async Task CheckForUpdatesOnStartupAsync_ShouldShowTrayNotification_WhenUpdateExists()
    {
        using var tempDir = new TemporaryDirectory();
        using var handler = new FakeHttpMessageHandler(_ => CreateJsonResponse("""
        {
          "tag_name": "latest",
          "name": "v0.2.13",
          "assets": [
            {
              "name": "XhMonitor-v0.2.13-Lite-Setup.exe",
              "browser_download_url": "https://example.com/download/XhMonitor-v0.2.13-Lite-Setup.exe"
            }
          ]
        }
        """));

        var trayIconService = new FakeTrayIconService();
        var service = CreateService(handler, tempDir.Path, trayIconService: trayIconService);

        await service.CheckForUpdatesOnStartupAsync();

        trayIconService.LastNotificationStatus.Should().NotBeNull();
        trayIconService.LastNotificationStatus!.LatestVersion.Should().Be("0.2.13");
    }

    [Fact]
    public async Task DownloadUpdateAsync_ShouldPersistInstallerAndAutoLaunch()
    {
        using var tempDir = new TemporaryDirectory();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/releases/tags/latest", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""
                {
                  "tag_name": "latest",
                  "name": "v0.2.13",
                  "assets": [
                    {
                      "name": "XhMonitor-v0.2.13-Lite-Setup.exe",
                      "browser_download_url": "https://example.com/download/XhMonitor-v0.2.13-Lite-Setup.exe"
                    }
                  ]
                }
                """);
            }

            if (request.RequestUri.AbsoluteUri.Contains("/download/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4, 5])
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var launcher = new FakeInstallerLauncher();
        var service = CreateService(handler, tempDir.Path, launcher: launcher);

        await service.CheckForUpdatesAsync();
        var status = await service.DownloadUpdateAsync();

        status.State.Should().Be(AppUpdateState.Downloaded);
        launcher.CallCount.Should().Be(1);
        launcher.LastInstallerPath.Should().NotBeNull();
        File.Exists(launcher.LastInstallerPath!).Should().BeTrue();
        status.Message.Should().Contain("已弹出安装程序");
    }

    [Fact]
    public async Task DownloadUpdateAsync_ShouldReportProgressBytesAndSpeedDuringDownload()
    {
        using var tempDir = new TemporaryDirectory();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/releases/tags/latest", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""
                {
                  "tag_name": "latest",
                  "name": "v0.2.13",
                  "assets": [
                    {
                      "name": "XhMonitor-v0.2.13-Lite-Setup.exe",
                      "browser_download_url": "https://example.com/download/XhMonitor-v0.2.13-Lite-Setup.exe"
                    }
                  ]
                }
                """);
            }

            if (request.RequestUri.AbsoluteUri.Contains("/download/", StringComparison.Ordinal))
            {
                var bytes = new byte[384 * 1024];
                var content = new StreamContent(new SlowReadStream(bytes, 128 * 1024, TimeSpan.FromMilliseconds(300)));
                content.Headers.ContentLength = bytes.Length;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var service = CreateService(handler, tempDir.Path, launcher: new FakeInstallerLauncher());
        var statuses = new List<AppUpdateStatus>();
        service.StatusChanged += (_, _) => statuses.Add(service.CurrentStatus);

        await service.CheckForUpdatesAsync();
        statuses.Clear();

        var status = await service.DownloadUpdateAsync();

        status.State.Should().Be(AppUpdateState.Downloaded);
        statuses.Should().Contain(status =>
            status.State == AppUpdateState.Downloading &&
            status.Message != null &&
            status.Message.Contains("已下载", StringComparison.Ordinal) &&
            status.Message.Contains("速度", StringComparison.Ordinal));
        statuses.Should().Contain(status =>
            status.State == AppUpdateState.Downloading &&
            status.Message != null &&
            status.Message.Contains("%", StringComparison.Ordinal) &&
            status.Message.Contains("/ 384K", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchInstallerAsync_ShouldAllowReinstallAfterDownload()
    {
        using var tempDir = new TemporaryDirectory();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/releases/tags/latest", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""
                {
                  "tag_name": "latest",
                  "name": "v0.2.13",
                  "assets": [
                    {
                      "name": "XhMonitor-v0.2.13-Lite-Setup.exe",
                      "browser_download_url": "https://example.com/download/XhMonitor-v0.2.13-Lite-Setup.exe"
                    }
                  ]
                }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([9, 8, 7, 6])
            };
        });

        var launcher = new FakeInstallerLauncher();
        var service = CreateService(handler, tempDir.Path, launcher: launcher);

        await service.CheckForUpdatesAsync();
        await service.DownloadUpdateAsync();
        var result = await service.LaunchInstallerAsync();

        result.Should().BeTrue();
        launcher.CallCount.Should().Be(2);
    }

    private static GitHubAppUpdateService CreateService(
        HttpMessageHandler handler,
        string downloadDirectory,
        FakeTrayIconService? trayIconService = null,
        FakeInstallerLauncher? launcher = null)
    {
        var httpClientFactory = new FakeHttpClientFactory(handler);
        var appVersionService = new FakeAppVersionService();
        trayIconService ??= new FakeTrayIconService();
        launcher ??= new FakeInstallerLauncher();
        var options = Options.Create(new AppUpdateOptions
        {
            Owner = "zhao-wuyan",
            Repository = "xhMonitor",
            PreferredReleaseTag = "latest",
            TargetAssetTemplate = "XhMonitor-v{version}-Lite-Setup.exe",
            DownloadDirectory = downloadDirectory
        });

        return new GitHubAppUpdateService(
            httpClientFactory,
            appVersionService,
            trayIconService,
            launcher,
            options,
            NullLogger<GitHubAppUpdateService>.Instance);
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name = "")
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class FakeAppVersionService : IAppVersionService
    {
        public Version CurrentVersion => new(0, 2, 9);

        public string CurrentVersionText => "0.2.9";
    }

    private sealed class FakeTrayIconService : ITrayIconService
    {
        public AppUpdateStatus? LastNotificationStatus { get; private set; }

        public void Initialize(
            FloatingWindow floatingWindow,
            Action toggleFloatingWindow,
            Action openWebInterface,
            Action<SettingsWindowSection> openSettingsWindow,
            Action openAboutWindow,
            Action exitApplication)
        {
        }

        public void Show()
        {
        }

        public void Hide()
        {
        }

        public void ShowUpdateAvailableNotification(AppUpdateStatus status)
        {
            LastNotificationStatus = status;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeInstallerLauncher : IInstallerLauncher
    {
        public int CallCount { get; private set; }

        public string? LastInstallerPath { get; private set; }

        public bool TryLaunch(string installerPath, out string message)
        {
            CallCount++;
            LastInstallerPath = installerPath;
            message = "已弹出安装程序。若取消安装，可再次点击“安装”。";
            return true;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xhmonitor-update-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class SlowReadStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly TimeSpan _delay;
        private int _position;

        public SlowReadStream(byte[] data, int chunkSize, TimeSpan delay)
        {
            _data = data;
            _chunkSize = chunkSize;
            _delay = delay;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadChunk(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            await Task.Delay(_delay, cancellationToken);
            return ReadChunk(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private int ReadChunk(Span<byte> buffer)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            var bytesToCopy = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
            _data.AsSpan(_position, bytesToCopy).CopyTo(buffer);
            _position += bytesToCopy;
            return bytesToCopy;
        }
    }
}
