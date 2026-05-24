using FluentAssertions;
using Microsoft.Extensions.Options;
using XhMonitor.Core.Configuration;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.Services;

public class DeviceVerifierTests
{
    [Fact]
    public async Task DoneWhen_SmbiosMatchesSixUnitedAxB35_VerifiesWithoutHttpEndpoint()
    {
        var httpClient = new HttpClient(new ThrowingHttpMessageHandler());
        var verifier = new DeviceVerifier(
            httpClient,
            Options.Create(CreateOptions()),
            new StubHardwarePlatformDetector(new HardwarePlatformInfo(
                SystemManufacturer: "Six United Intelligent Tech. CO.,Ltd.",
                SystemModel: "AXB35-02",
                SystemProductVendor: "Six United Intelligent Tech. CO.,Ltd.",
                SystemProductName: "AXB35-02",
                BaseBoardManufacturer: "Six United Intelligent Tech. CO.,Ltd.",
                BaseBoardProduct: "AXB35-02",
                BiosManufacturer: "American Megatrends International, LLC.",
                BiosVersion: "0.12.T90")));

        await verifier.GetDeviceInfoAsync();

        verifier.GetVerifiedDeviceName().Should().Be("SixUnitedAXB35-02");
        verifier.GetDisabledReason().Should().BeNull();
        verifier.IsPowerMonitoringEnabled().Should().BeTrue();
        verifier.GetSchemesForDevice("SixUnitedAXB35-02").Should().ContainSingle(s => s.StapmWatts == 120);
    }

    [Fact]
    public async Task DoneWhen_SmbiosDoesNotMatch_ButDeviceInfoIsAmd395_EnablesPowerMonitoringOnly()
    {
        var httpClient = new HttpClient(new JsonHttpMessageHandler(
            """{"platform":"amd_395","is_mac_authorized":false,"device_verified":true,"nova_id":null,"is_permanent":true,"usable_until":null,"use_cdk":false}"""));
        var verifier = new DeviceVerifier(
            httpClient,
            Options.Create(CreateOptions()),
            new StubHardwarePlatformDetector(new HardwarePlatformInfo(
                SystemManufacturer: "Other Vendor",
                SystemModel: "Other Model",
                SystemProductVendor: null,
                SystemProductName: null,
                BaseBoardManufacturer: null,
                BaseBoardProduct: null,
                BiosManufacturer: null,
                BiosVersion: null)));

        var info = await verifier.GetDeviceInfoAsync();

        info.Should().NotBeNull();
        verifier.GetVerifiedDeviceName().Should().BeNull();
        verifier.GetDisabledReason().Should().Be("当前设备不支持此功能");
        verifier.IsPowerMonitoringEnabled().Should().BeTrue();
        verifier.IsPowerSwitchEnabled().Should().BeFalse();
    }

    [Fact]
    public async Task DoneWhen_LegacyMappingHasNoHardwareCondition_FallsBackToDeviceInfoEndpoint()
    {
        var options = CreateOptions();
        options.Devices.Add(new DeviceSchemeMappingConfig
        {
            Name = "LegacyAmd395",
            SchemeKey = "LegacyAmd395",
            Platform = "amd_395",
            RequireMacAuthorized = false
        });
        options.SchemeProfiles["LegacyAmd395"] =
        [
            new PowerSchemeConfig { StapmWatts = 55, FastWatts = 100, SlowWatts = 55 }
        ];

        var httpClient = new HttpClient(new JsonHttpMessageHandler(
            """{"platform":"amd_395","is_mac_authorized":false,"device_verified":true,"nova_id":null,"is_permanent":true,"usable_until":null,"use_cdk":false}"""));
        var verifier = new DeviceVerifier(
            httpClient,
            Options.Create(options),
            new StubHardwarePlatformDetector(HardwarePlatformInfo.Empty));

        await verifier.GetDeviceInfoAsync();

        verifier.GetVerifiedDeviceName().Should().Be("LegacyAmd395");
        verifier.GetDisabledReason().Should().BeNull();
        verifier.IsPowerMonitoringEnabled().Should().BeTrue();
    }

    [Fact]
    public async Task DoneWhen_DeviceReferencesMissingSchemeProfile_VerifiesDeviceButDisablesPowerSwitch()
    {
        var options = CreateOptions();
        options.Devices[0].SchemeKey = "MissingProfile";

        var verifier = new DeviceVerifier(
            new HttpClient(new ThrowingHttpMessageHandler()),
            Options.Create(options),
            new StubHardwarePlatformDetector(new HardwarePlatformInfo(
                SystemManufacturer: "Six United Intelligent Tech. CO.,Ltd.",
                SystemModel: "AXB35-02",
                SystemProductVendor: "Six United Intelligent Tech. CO.,Ltd.",
                SystemProductName: "AXB35-02",
                BaseBoardManufacturer: "Six United Intelligent Tech. CO.,Ltd.",
                BaseBoardProduct: "AXB35-02",
                BiosManufacturer: null,
                BiosVersion: null)));

        await verifier.GetDeviceInfoAsync();

        verifier.GetVerifiedDeviceName().Should().Be("SixUnitedAXB35-02");
        verifier.GetDisabledReason().Should().Be("功耗切换方案未配置");
        verifier.IsPowerMonitoringEnabled().Should().BeTrue();
        verifier.GetSchemesForDevice("SixUnitedAXB35-02").Should().BeEmpty();
        verifier.IsPowerSwitchEnabled().Should().BeFalse();
    }

    [Fact]
    public async Task DoneWhen_NeitherSmbiosNorDeviceInfoMatch_DisablesPowerSwitch()
    {
        var httpClient = new HttpClient(new JsonHttpMessageHandler(
            """{"platform":"unknown","is_mac_authorized":false,"device_verified":false,"nova_id":null,"is_permanent":false,"usable_until":null,"use_cdk":false}"""));
        var verifier = new DeviceVerifier(
            httpClient,
            Options.Create(CreateOptions()),
            new StubHardwarePlatformDetector(HardwarePlatformInfo.Empty));

        await verifier.GetDeviceInfoAsync();

        verifier.GetVerifiedDeviceName().Should().BeNull();
        verifier.GetDisabledReason().Should().Be("当前设备不支持此功能");
        verifier.IsPowerMonitoringEnabled().Should().BeFalse();
        verifier.IsPowerSwitchEnabled().Should().BeFalse();
    }

    private static DeviceVerificationOptions CreateOptions() => new()
    {
        TimeoutSeconds = 1,
        Devices =
        [
            new DeviceSchemeMappingConfig
            {
                Name = "SixUnitedAXB35-02",
                SchemeKey = "AXB35-02",
                Platform = "amd_395",
                RequireMacAuthorized = false,
                HardwareManufacturerContains = ["Six United", "Sixunited"],
                HardwareModelContains = ["AXB35-02"]
            }
        ],
        SchemeProfiles = new Dictionary<string, List<PowerSchemeConfig>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AXB35-02"] =
            [
                new PowerSchemeConfig { StapmWatts = 55, FastWatts = 100, SlowWatts = 55 },
                new PowerSchemeConfig { StapmWatts = 120, FastWatts = 140, SlowWatts = 120 }
            ]
        }
    };

    private sealed class StubHardwarePlatformDetector(HardwarePlatformInfo hardware) : IHardwarePlatformDetector
    {
        public HardwarePlatformInfo Detect() => hardware;
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Endpoint unavailable");
    }

    private sealed class JsonHttpMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }
}
