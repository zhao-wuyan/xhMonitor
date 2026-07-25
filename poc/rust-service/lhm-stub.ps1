# lhm-stub.ps1 — 模拟 LHM bridge subprocess 输出（仅用于 POC IPC 验证）
# 真实 LHM bridge 是 .NET self-contained exe，输出格式相同
# 用法：powershell -ExecutionPolicy Bypass -File lhm-stub.ps1

$rand = New-Object System.Random

while ($true) {
    $snapshot = [ordered]@{
        timestamp    = (Get-Date -Format "o")
        cpu_temp     = [math]::Round(45 + $rand.NextDouble() * 35, 1)
        gpu_temp     = [math]::Round(38 + $rand.NextDouble() * 30, 1)
        cpu_load     = [math]::Round($rand.NextDouble() * 80, 1)
        gpu_load     = [math]::Round($rand.NextDouble() * 60, 1)
        net_upload   = [math]::Round($rand.NextDouble() * 5, 2)
        net_download = [math]::Round($rand.NextDouble() * 20, 2)
        disk_read    = [math]::Round($rand.NextDouble() * 100, 1)
        disk_write   = [math]::Round($rand.NextDouble() * 50, 1)
    }
    Write-Output ($snapshot | ConvertTo-Json -Compress)
    [System.Console]::Out.Flush()
    Start-Sleep -Milliseconds 1000
}
