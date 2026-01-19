# 临时测试脚本：验证提供者注册
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project XhMonitor.Service/XhMonitor.Service.csproj 2>&1 | Select-String -Pattern "LibreHardwareMonitor|已注册|使用|提供者|初始化|Computer|MetricProviderRegistry" -Context 0,2 | Select-Object -First 20
