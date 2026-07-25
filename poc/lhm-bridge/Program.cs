// lhm-bridge POC
// 目标：验证 LHM 0.9.* 传感器读取可行性 + JSON Lines 输出 + self-contained 发布
//
// 采集范围与 XhMonitor.Core/Services/LibreHardwareManager.cs 对齐：
//   - CPU Temperature（对应 SystemMetricProvider.GetTemperatures）
//   - GPU Temperature + Load（对应 LibreHardwareMonitorGpuProvider）
//   - Network Throughput（对应 SystemMetricProvider.GetNetworkSpeed via LHM）
//   - Storage Throughput + Data（对应 SystemMetricProvider.GetDiskUsages via LHM）
//
// 输出格式：JSON Lines，每行一个快照，stdout flush 后 1s 间隔
// Rust Service 端通过 stdin/stdout IPC 消费此输出

using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;

// 管理员权限检查（LHM 需要）
if (!System.Security.Principal.WindowsIdentity.GetCurrent()
        .Owner?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) ?? true)
{
    Console.Error.WriteLine("[lhm-bridge] WARNING: not running as administrator; some sensors may be unavailable");
}

var computer = new Computer
{
    IsCpuEnabled    = true,
    IsGpuEnabled    = true,
    IsMemoryEnabled = true,
    IsNetworkEnabled = true,
    IsStorageEnabled = true,
    IsMotherboardEnabled = false,  // 与 Service 默认配置对齐
    IsControllerEnabled  = false,
};

try
{
    computer.Open();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[lhm-bridge] Computer.Open() failed: {ex.Message}");
    return 1;
}

Console.Error.WriteLine("[lhm-bridge] started, outputting JSON Lines to stdout");

var visitor = new UpdateVisitor();
var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    try
    {
        computer.Accept(visitor);
        var snapshot = BuildSnapshot(computer);
        Console.WriteLine(JsonSerializer.Serialize(snapshot, options));
        Console.Out.Flush();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[lhm-bridge] collection error: {ex.Message}");
    }

    try { await Task.Delay(1000, cts.Token); }
    catch (TaskCanceledException) { break; }
}

computer.Close();
Console.Error.WriteLine("[lhm-bridge] stopped");
return 0;

// ── snapshot builder ────────────────────────────────────────────────────────

static LhmSnapshot BuildSnapshot(Computer computer)
{
    double? cpuTemp = null, gpuTemp = null, gpuLoad = null;
    string? cpuTempLabel = null;
    double  netUp = 0, netDown = 0, diskRead = 0, diskWrite = 0;

    foreach (var hw in computer.Hardware)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.Cpu:
                (cpuTemp, cpuTempLabel) = SelectTemperatureWithLabel(hw);
                break;

            case HardwareType.GpuAmd:
            case HardwareType.GpuNvidia:
            case HardwareType.GpuIntel:
                if (gpuTemp == null) (gpuTemp, _) = SelectTemperatureWithLabel(hw);
                gpuLoad ??= SelectGpuLoad(hw);
                break;

            case HardwareType.Network:
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput || !s.Value.HasValue) continue;
                    var n = s.Name.ToLowerInvariant();
                    if (n.Contains("upload") || n.Contains("send") || n.Contains("tx"))
                        netUp += s.Value.Value;
                    else if (n.Contains("download") || n.Contains("receive") || n.Contains("rx"))
                        netDown += s.Value.Value;
                }
                break;

            case HardwareType.Storage:
                foreach (var s in hw.Sensors)
                {
                    if (!s.Value.HasValue) continue;
                    var n = s.Name.ToLowerInvariant();
                    if (s.SensorType == SensorType.Throughput)
                    {
                        if (n.Contains("read"))  diskRead  += s.Value.Value;
                        if (n.Contains("write")) diskWrite += s.Value.Value;
                    }
                }
                break;
        }
    }

    return new LhmSnapshot(
        Timestamp:       DateTime.UtcNow,
        CpuTemp:         cpuTemp.HasValue ? Math.Round(cpuTemp.Value, 1) : null,
        CpuTempLabel:    cpuTempLabel,
        GpuTemp:         gpuTemp.HasValue ? Math.Round(gpuTemp.Value, 1) : null,
        GpuLoad:         gpuLoad.HasValue ? Math.Round(gpuLoad.Value,  1) : null,
        NetUploadMbps:   Math.Round(netUp   / 1_048_576.0, 3),
        NetDownloadMbps: Math.Round(netDown / 1_048_576.0, 3),
        DiskReadMbps:    Math.Round(diskRead  / 1_048_576.0, 3),
        DiskWriteMbps:   Math.Round(diskWrite / 1_048_576.0, 3)
    );
}

static (double? Value, string? Label) SelectTemperatureWithLabel(IHardware hw)
{
    // 与 SystemMetricProvider.SelectPreferredTemperatureSensor 逻辑对齐
    ISensor? best = null;
    foreach (var s in hw.Sensors)
    {
        if (s.SensorType != SensorType.Temperature) continue;
        if (!s.Value.HasValue || float.IsNaN(s.Value.Value) || s.Value.Value <= 0) continue;
        if (s.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)) continue;

        if (best == null) { best = s; continue; }
        var n = s.Name;
        if (n.Equals("Core Max",  StringComparison.OrdinalIgnoreCase)) { best = s; break; }
        if (n.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Tctl",    StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Tdie",    StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Hot Spot",StringComparison.OrdinalIgnoreCase))
            best = s;
    }
    return best?.Value.HasValue == true
        ? ((double?)best.Value!.Value, best.Name)
        : (null, null);
}

static double? SelectGpuLoad(IHardware hw)
{
    ISensor? core = null, engine = null;
    foreach (var s in hw.Sensors)
    {
        if (s.SensorType != SensorType.Load || !s.Value.HasValue) continue;
        var n = s.Name;
        if (n.Contains("GPU Core") || n.Contains("GPU Usage") || n.Contains("GPU Load"))
            if (core == null || s.Value > core.Value) core = s;
        if (n.Contains("D3D") || n.Contains("Graphics") || n.Contains("Compute"))
            if (engine == null || s.Value > engine.Value) engine = s;
    }
    var chosen = core ?? engine;
    return chosen?.Value.HasValue == true ? (double?)chosen.Value!.Value : null;
}

// ── types ───────────────────────────────────────────────────────────────────

record LhmSnapshot(
    [property: JsonPropertyName("ts")]               DateTime  Timestamp,
    [property: JsonPropertyName("cpu_temp")]          double?   CpuTemp,
    [property: JsonPropertyName("cpu_temp_label")]    string?   CpuTempLabel,
    [property: JsonPropertyName("gpu_temp")]          double?   GpuTemp,
    [property: JsonPropertyName("gpu_load")]          double?   GpuLoad,
    [property: JsonPropertyName("net_up_mbps")]       double    NetUploadMbps,
    [property: JsonPropertyName("net_down_mbps")]     double    NetDownloadMbps,
    [property: JsonPropertyName("disk_read_mbps")]    double    DiskReadMbps,
    [property: JsonPropertyName("disk_write_mbps")]   double    DiskWriteMbps
);

class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }
    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
