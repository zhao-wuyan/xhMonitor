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
        status.Message.Should().Contain("latest");
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
}
