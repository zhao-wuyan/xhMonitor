param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class NativeActions {
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string className, string title);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, UIntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
}
"@

function Get-Windows([int]$TargetProcessId) {
    $items = [Collections.Generic.List[object]]::new()
    $cb = [NativeActions+EnumWindowsProc]{
        param([IntPtr]$hwnd, [IntPtr]$unused)
        [uint32]$owner = 0
        [void][NativeActions]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -eq $TargetProcessId) {
            $title = [Text.StringBuilder]::new(512)
            $class = [Text.StringBuilder]::new(512)
            [void][NativeActions]::GetWindowTextW($hwnd, $title, $title.Capacity)
            [void][NativeActions]::GetClassNameW($hwnd, $class, $class.Capacity)
            $items.Add([pscustomobject]@{
                hwnd = [Int64]$hwnd
                title = $title.ToString()
                class = $class.ToString()
                visible = [NativeActions]::IsWindowVisible($hwnd)
            })
        }
        return $true
    }
    [void][NativeActions]::EnumWindows($cb, [IntPtr]::Zero)
    return @($items)
}

function Get-Stage([Diagnostics.Process]$Process, [string]$Name) {
    $values = [Collections.Generic.List[object]]::new()
    for ($i = 1; $i -le 20; $i++) {
        $Process.Refresh()
        $values.Add([pscustomobject]@{
            private_bytes = [Int64]$Process.PrivateMemorySize64
            working_set_bytes = [Int64]$Process.WorkingSet64
            threads = $Process.Threads.Count
            handles = $Process.HandleCount
            gdi = [NativeActions]::GetGuiResources($Process.Handle, 0)
            user = [NativeActions]::GetGuiResources($Process.Handle, 1)
        })
        if ($i -lt 20) { Start-Sleep -Milliseconds 250 }
    }
    return [pscustomobject]@{
        name = $Name
        captured_at = (Get-Date).ToString('o')
        private_min_bytes = [Int64](($values.private_bytes | Measure-Object -Minimum).Minimum)
        private_max_bytes = [Int64](($values.private_bytes | Measure-Object -Maximum).Maximum)
        private_mean_bytes = [Int64](($values.private_bytes | Measure-Object -Average).Average)
        private_mean_mib = [Math]::Round((($values.private_bytes | Measure-Object -Average).Average) / 1MB, 6)
        working_set_mean_mib = [Math]::Round((($values.working_set_bytes | Measure-Object -Average).Average) / 1MB, 6)
        thread_range = @((($values.threads | Measure-Object -Minimum).Minimum), (($values.threads | Measure-Object -Maximum).Maximum))
        handle_range = @((($values.handles | Measure-Object -Minimum).Minimum), (($values.handles | Measure-Object -Maximum).Maximum))
        gdi_range = @((($values.gdi | Measure-Object -Minimum).Minimum), (($values.gdi | Measure-Object -Maximum).Maximum))
        user_range = @((($values.user | Measure-Object -Minimum).Minimum), (($values.user | Measure-Object -Maximum).Maximum))
        windows = @(Get-Windows $Process.Id)
    }
}

function Send-TrayCommand([int]$TargetProcessId, [uint64]$Command) {
    $tray = [NativeActions]::FindWindowW("xhmonitor_shell_notify_$TargetProcessId", $null)
    if ($tray -eq [IntPtr]::Zero) { throw "tray window not found" }
    if (-not [NativeActions]::PostMessageW($tray, 0x0111, [UIntPtr]::new($Command), [IntPtr]::Zero)) {
        throw "PostMessage tray command $Command failed"
    }
}

function Close-WindowByTitle([string]$Title) {
    $window = [NativeActions]::FindWindowW($null, $Title)
    if ($window -ne [IntPtr]::Zero) {
        [void][NativeActions]::PostMessageW($window, 0x0010, [UIntPtr]::Zero, [IntPtr]::Zero)
    }
}

$resolvedExe = (Resolve-Path $ExePath).Path
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory((Split-Path -Parent $resolvedOutput)) | Out-Null
$stdout = [IO.Path]::ChangeExtension($resolvedOutput, ".stdout.log")
$stderr = [IO.Path]::ChangeExtension($resolvedOutput, ".stderr.log")
$oldBackend = $env:SLINT_BACKEND
$oldLog = $env:RUST_LOG
try {
    $env:SLINT_BACKEND = "winit-software"
    $env:RUST_LOG = "info"
    Remove-Item Env:XHM_DESKTOP_UI_SMOKE -ErrorAction SilentlyContinue
    Remove-Item Env:XHM_DESKTOP_G4_SMOKE -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $resolvedExe -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
} finally {
    if ($null -eq $oldBackend) { Remove-Item Env:SLINT_BACKEND -ErrorAction SilentlyContinue } else { $env:SLINT_BACKEND = $oldBackend }
    if ($null -eq $oldLog) { Remove-Item Env:RUST_LOG -ErrorAction SilentlyContinue } else { $env:RUST_LOG = $oldLog }
}

try {
    Start-Sleep -Seconds 10
    $stages = [Collections.Generic.List[object]]::new()
    $stages.Add((Get-Stage $process "normal_main_taskbar_hidden_aux_tray_dual_sse"))

    Send-TrayCommand $process.Id 1005
    Start-Sleep -Seconds 3
    $stages.Add((Get-Stage $process "settings_visible"))

    Send-TrayCommand $process.Id 1006
    Start-Sleep -Seconds 3
    $stages.Add((Get-Stage $process "settings_about_visible"))

    Close-WindowByTitle "xhMonitor Settings"
    Close-WindowByTitle "About XhMonitor"
    Start-Sleep -Seconds 3
    $stages.Add((Get-Stage $process "aux_close_requested"))

    Send-TrayCommand $process.Id 1001
    Start-Sleep -Seconds 3
    $stages.Add((Get-Stage $process "floating_hidden"))

    Close-WindowByTitle "xhm-desktop-taskbar"
    Start-Sleep -Seconds 3
    $stages.Add((Get-Stage $process "taskbar_close_requested"))

    $tray = [NativeActions]::FindWindowW("xhmonitor_shell_notify_$($process.Id)", $null)
    if ($tray -ne [IntPtr]::Zero) {
        [void][NativeActions]::PostMessageW($tray, 0x0010, [UIntPtr]::Zero, [IntPtr]::Zero)
    }
    Start-Sleep -Seconds 3
    $stages.Add((Get-Stage $process "tray_close_requested"))

    [ordered]@{
        schema_version = "desktop-memory-action-profile/1.0"
        executable = $resolvedExe
        process_id = $process.Id
        stages = @($stages)
        stdout_log = $stdout
        stderr_log = $stderr
    } | ConvertTo-Json -Depth 8 | Set-Content -Path $resolvedOutput -Encoding utf8
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}
