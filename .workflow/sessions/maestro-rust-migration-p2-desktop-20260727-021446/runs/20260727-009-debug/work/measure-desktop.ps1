param(
    [Parameter(Mandatory = $true)][string]$Label,
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [switch]$UiSmoke,
    [switch]$G4Smoke,
    [int]$WarmupSeconds = 10,
    [int]$SampleCount = 60,
    [int]$IntervalMs = 1000
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class NativeProfile {
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string className, string windowName);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr VirtualQueryEx(
        IntPtr process,
        IntPtr address,
        out MEMORY_BASIC_INFORMATION buffer,
        UIntPtr length);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetMappedFileNameW(
        IntPtr process,
        IntPtr address,
        StringBuilder fileName,
        uint size);
}
"@

function Get-TargetWindows([int]$TargetProcessId) {
    $items = [System.Collections.Generic.List[object]]::new()
    $callback = [NativeProfile+EnumWindowsProc]{
        param([IntPtr]$hwnd, [IntPtr]$unused)
        [uint32]$owner = 0
        [void][NativeProfile]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -eq $TargetProcessId) {
            $titleBuffer = [Text.StringBuilder]::new(512)
            $classBuffer = [Text.StringBuilder]::new(512)
            [void][NativeProfile]::GetWindowTextW($hwnd, $titleBuffer, $titleBuffer.Capacity)
            [void][NativeProfile]::GetClassNameW($hwnd, $classBuffer, $classBuffer.Capacity)
            $items.Add([pscustomobject]@{
                hwnd = [Int64]$hwnd
                hwnd_hex = ('0x{0:X}' -f [Int64]$hwnd)
                title = $titleBuffer.ToString()
                class = $classBuffer.ToString()
                visible = [NativeProfile]::IsWindowVisible($hwnd)
            })
        }
        return $true
    }
    [void][NativeProfile]::EnumWindows($callback, [IntPtr]::Zero)
    return @($items)
}

function Get-MappedFile([System.Diagnostics.Process]$Process, [IntPtr]$Address) {
    $buffer = [Text.StringBuilder]::new(2048)
    $length = [NativeProfile]::GetMappedFileNameW($Process.Handle, $Address, $buffer, $buffer.Capacity)
    if ($length -eq 0) { return $null }
    return $buffer.ToString()
}

function Get-MemoryMap([System.Diagnostics.Process]$Process) {
    $commit = 0x1000
    $memPrivate = 0x20000
    $memMapped = 0x40000
    $memImage = 0x1000000
    $regions = [System.Collections.Generic.List[object]]::new()
    [UInt64]$address = 0
    $mbiSize = [Runtime.InteropServices.Marshal]::SizeOf([type][NativeProfile+MEMORY_BASIC_INFORMATION])

    while ($true) {
        $mbi = [NativeProfile+MEMORY_BASIC_INFORMATION]::new()
        $queried = [NativeProfile]::VirtualQueryEx(
            $Process.Handle,
            [IntPtr]([Int64]$address),
            [ref]$mbi,
            [UIntPtr]::new([UInt64]$mbiSize))
        if ($queried -eq [UIntPtr]::Zero) { break }

        [UInt64]$base = [UInt64]$mbi.BaseAddress.ToInt64()
        [UInt64]$allocationBase = [UInt64]$mbi.AllocationBase.ToInt64()
        [UInt64]$size = $mbi.RegionSize.ToUInt64()
        if ($size -eq 0) { break }

        if ($mbi.State -eq $commit) {
            $typeName = switch ($mbi.Type) {
                $memPrivate { "MEM_PRIVATE" }
                $memMapped { "MEM_MAPPED" }
                $memImage { "MEM_IMAGE" }
                default { ('0x{0:X}' -f $mbi.Type) }
            }
            $mappedFile = if ($mbi.Type -eq $memMapped -or $mbi.Type -eq $memImage) {
                Get-MappedFile $Process $mbi.BaseAddress
            } else { $null }
            $regions.Add([pscustomobject]@{
                base = $base
                base_hex = ('0x{0:X}' -f $base)
                allocation_base = $allocationBase
                allocation_base_hex = ('0x{0:X}' -f $allocationBase)
                region_size = $size
                state = ('0x{0:X}' -f $mbi.State)
                protect = ('0x{0:X}' -f $mbi.Protect)
                allocation_protect = ('0x{0:X}' -f $mbi.AllocationProtect)
                type = $typeName
                mapped_file = $mappedFile
            })
        }

        [UInt64]$next = $base + $size
        if ($next -le $address) { break }
        $address = $next
        if ($address -gt 0x00007FFFFFFFFFFF) { break }
    }

    $byType = @($regions | Group-Object type | ForEach-Object {
        [pscustomobject]@{
            type = $_.Name
            committed_bytes = [UInt64](($_.Group | Measure-Object region_size -Sum).Sum)
            region_count = $_.Count
        }
    } | Sort-Object committed_bytes -Descending)

    $allocations = @($regions | Group-Object allocation_base | ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{
            allocation_base = [UInt64]$first.allocation_base
            allocation_base_hex = $first.allocation_base_hex
            committed_bytes = [UInt64](($_.Group | Measure-Object region_size -Sum).Sum)
            region_count = $_.Count
            types = @($_.Group.type | Sort-Object -Unique)
            mapped_file = @($_.Group.mapped_file | Where-Object { $_ } | Sort-Object -Unique)
            protections = @($_.Group.protect | Sort-Object -Unique)
        }
    } | Sort-Object committed_bytes -Descending | Select-Object -First 40)

    return [pscustomobject]@{
        captured_at = (Get-Date).ToString('o')
        committed_by_type = $byType
        largest_allocation_bases = $allocations
        committed_region_count = $regions.Count
    }
}

function Get-ProcessSample([System.Diagnostics.Process]$Process, [int]$Index) {
    $Process.Refresh()
    return [pscustomobject]@{
        sample = $Index
        timestamp = (Get-Date).ToString('o')
        private_bytes = [Int64]$Process.PrivateMemorySize64
        private_mib = [Math]::Round($Process.PrivateMemorySize64 / 1MB, 6)
        working_set_bytes = [Int64]$Process.WorkingSet64
        working_set_mib = [Math]::Round($Process.WorkingSet64 / 1MB, 6)
        virtual_bytes = [Int64]$Process.VirtualMemorySize64
        paged_bytes = [Int64]$Process.PagedMemorySize64
        thread_count = $Process.Threads.Count
        handle_count = $Process.HandleCount
        gdi_objects = [NativeProfile]::GetGuiResources($Process.Handle, 0)
        user_objects = [NativeProfile]::GetGuiResources($Process.Handle, 1)
    }
}

function Get-Summary($Samples) {
    $private = @($Samples.private_bytes | Sort-Object)
    $working = @($Samples.working_set_bytes | Sort-Object)
    $middle = [int][Math]::Floor($private.Count / 2)
    $median = if (($private.Count % 2) -eq 0) {
        ($private[$middle - 1] + $private[$middle]) / 2
    } else { $private[$middle] }
    return [pscustomobject]@{
        count = $private.Count
        private_min_bytes = [Int64]$private[0]
        private_max_bytes = [Int64]$private[-1]
        private_mean_bytes = [Int64](($private | Measure-Object -Average).Average)
        private_median_bytes = [Int64]$median
        private_min_mib = [Math]::Round($private[0] / 1MB, 6)
        private_max_mib = [Math]::Round($private[-1] / 1MB, 6)
        private_mean_mib = [Math]::Round((($private | Measure-Object -Average).Average) / 1MB, 6)
        private_median_mib = [Math]::Round($median / 1MB, 6)
        working_set_min_mib = [Math]::Round($working[0] / 1MB, 6)
        working_set_max_mib = [Math]::Round($working[-1] / 1MB, 6)
        below_10_count = @($private | Where-Object { $_ -lt 10MB }).Count
    }
}

$resolvedExe = (Resolve-Path $ExePath).Path
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$stdoutPath = Join-Path $outputDirectory ("{0}.stdout.log" -f $Label)
$stderrPath = Join-Path $outputDirectory ("{0}.stderr.log" -f $Label)

$oldBackend = $env:SLINT_BACKEND
$oldLog = $env:RUST_LOG
$oldUiSmoke = $env:XHM_DESKTOP_UI_SMOKE
$oldG4Smoke = $env:XHM_DESKTOP_G4_SMOKE
try {
    $env:SLINT_BACKEND = "winit-software"
    $env:RUST_LOG = "info"
    if ($UiSmoke) { $env:XHM_DESKTOP_UI_SMOKE = "1" } else { Remove-Item Env:XHM_DESKTOP_UI_SMOKE -ErrorAction SilentlyContinue }
    if ($G4Smoke) { $env:XHM_DESKTOP_G4_SMOKE = "1" } else { Remove-Item Env:XHM_DESKTOP_G4_SMOKE -ErrorAction SilentlyContinue }

    $process = Start-Process -FilePath $resolvedExe -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
} finally {
    if ($null -eq $oldBackend) { Remove-Item Env:SLINT_BACKEND -ErrorAction SilentlyContinue } else { $env:SLINT_BACKEND = $oldBackend }
    if ($null -eq $oldLog) { Remove-Item Env:RUST_LOG -ErrorAction SilentlyContinue } else { $env:RUST_LOG = $oldLog }
    if ($null -eq $oldUiSmoke) { Remove-Item Env:XHM_DESKTOP_UI_SMOKE -ErrorAction SilentlyContinue } else { $env:XHM_DESKTOP_UI_SMOKE = $oldUiSmoke }
    if ($null -eq $oldG4Smoke) { Remove-Item Env:XHM_DESKTOP_G4_SMOKE -ErrorAction SilentlyContinue } else { $env:XHM_DESKTOP_G4_SMOKE = $oldG4Smoke }
}

$targetProcessId = $process.Id
$startedAt = Get-Date
try {
    $windowDeadline = (Get-Date).AddSeconds(20)
    do {
        if ($process.HasExited) { throw "xhm-desktop exited before profiling (code=$($process.ExitCode))" }
        $windows = @(Get-TargetWindows $targetProcessId)
        if (@($windows | Where-Object { $_.title -in @('xhm-desktop', 'xhm-desktop-taskbar') }).Count -ge 2) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $windowDeadline)

    if (@($windows | Where-Object { $_.title -in @('xhm-desktop', 'xhm-desktop-taskbar') }).Count -lt 2) {
        throw "timed out waiting for dual xhm-desktop windows"
    }

    Start-Sleep -Seconds $WarmupSeconds
    $process.Refresh()
    $windowsBefore = @(Get-TargetWindows $targetProcessId)
    $modules = @($process.Modules | ForEach-Object {
        [pscustomobject]@{
            module_name = $_.ModuleName
            file_name = $_.FileName
            base_address = ('0x{0:X}' -f [Int64]$_.BaseAddress)
            module_memory_size = $_.ModuleMemorySize
        }
    })
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$targetProcessId" -ErrorAction SilentlyContinue | ForEach-Object {
        [pscustomobject]@{ process_id = $_.ProcessId; name = $_.Name; command_line = $_.CommandLine }
    })
    $mapBefore = Get-MemoryMap $process

    $samples = [System.Collections.Generic.List[object]]::new()
    for ($index = 1; $index -le $SampleCount; $index++) {
        if ($process.HasExited) { throw "xhm-desktop exited during profiling (code=$($process.ExitCode))" }
        $samples.Add((Get-ProcessSample $process $index))
        if ($index -lt $SampleCount) { Start-Sleep -Milliseconds $IntervalMs }
    }

    $mapAfter = Get-MemoryMap $process
    $windowsAfter = @(Get-TargetWindows $targetProcessId)
    $summary = Get-Summary @($samples)

    $result = [ordered]@{
        schema_version = "desktop-memory-profile/1.0"
        label = $Label
        executable = $resolvedExe
        process_id = $targetProcessId
        started_at = $startedAt.ToString('o')
        conditions = [ordered]@{
            release = $true
            slint_backend = "winit-software"
            ui_smoke = [bool]$UiSmoke
            g4_smoke = [bool]$G4Smoke
            warmup_seconds = $WarmupSeconds
            sample_interval_ms = $IntervalMs
        }
        summary = $summary
        windows_before = $windowsBefore
        windows_after = $windowsAfter
        child_processes_excluded = $children
        modules = $modules
        memory_map_before = $mapBefore
        memory_map_after = $mapAfter
        samples = @($samples)
        stdout_log = $stdoutPath
        stderr_log = $stderrPath
    }
    $result | ConvertTo-Json -Depth 10 | Set-Content -Path $resolvedOutput -Encoding utf8
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $trayClass = "xhmonitor_shell_notify_$targetProcessId"
        $trayWindow = [NativeProfile]::FindWindowW($trayClass, $null)
        if ($trayWindow -ne [IntPtr]::Zero) {
            [void][NativeProfile]::PostMessageW($trayWindow, 0x0111, [UIntPtr]::new([UInt64]1007), [IntPtr]::Zero)
            if (-not $process.WaitForExit(5000)) {
                Stop-Process -Id $targetProcessId -Force -ErrorAction SilentlyContinue
            }
        } else {
            Stop-Process -Id $targetProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}
