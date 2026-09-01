# XhMonitor - Windows 资源监视器

XhMonitor 是一套 Windows 进程资源监控工具，支持 CPU、内存、GPU、显存、功耗、网络指标的实时采集、分层聚合和可视化展示。

[![Rust](https://img.shields.io/badge/Rust-1.82-000000)](https://www.rust-lang.org/)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![React](https://img.shields.io/badge/React-19-61DAFB)](https://react.dev/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

> 国内用户可通过 [AtomGit 国内镜像](https://atomgit.com/zhao-wuyan/xhMonitor) 加速访问代码与 Release 下载。

## 当前架构

当前过渡版本使用 Rust backend 和 C# WPF Desktop：

| 模块 | 技术 | 职责 |
|------|------|------|
| `xhm-core` | Rust | 共享模型、trait、错误和 wire contract |
| `xhm-service` | Rust、Tokio、Axum、SQLite | 指标采集、REST API、SignalR 兼容端点、分层聚合与数据保留 |
| `lhm-bridge` | .NET 8、LibreHardwareMonitor | 向 Rust Service 提供硬件传感器快照 |
| `XhMonitor.Desktop` | .NET 8 WPF | 桌面悬浮窗、任务栏窗口、托盘和内嵌 Web 界面 |
| `XhMonitor.Core` | .NET 8 | C# Desktop 仍使用的共享配置和模型 |
| `xhmonitor-web` | React、TypeScript、Vite | 实时 Web 可视化 |

旧 `XhMonitor.Service` 已由 `xhm-service` 替代。Rust Desktop 仍在独立迁移分支开发，不属于当前生产 workspace。

## 目录结构

```text
xhMonitor/
├── Cargo.toml
├── xhm-core/
├── xhm-service/
├── lhm-bridge/
├── XhMonitor.Core/
├── XhMonitor.Desktop/
├── XhMonitor.Desktop.Tests/
├── xhmonitor-web/
├── tools/RyzenAdj/
├── scripts/
├── publish.ps1
├── build-installer.ps1
└── installer/XhMonitor.iss
```

## 环境要求

- Windows 10/11 x64。
- Rust 1.82 或更高版本，MSVC toolchain。
- .NET SDK 8。
- Node.js 18 或更高版本。
- Visual Studio 2022 Build Tools。
- 构建安装器时需要 Inno Setup 6.x。

## 开发运行

启动 Rust Service：

```powershell
cargo run -p xhm-service
```

Service 默认监听 `http://127.0.0.1:35179`。

启动 C# Desktop：

```powershell
dotnet run --project XhMonitor.Desktop/XhMonitor.Desktop.csproj
```

如果 `../Service/xhm-service.exe` 不存在，Desktop 会从 workspace 根目录执行 `cargo run -p xhm-service`。

一键启动 Desktop 和 Web 开发服务器：

```powershell
.\start-all.ps1
```

## 配置

- Service 配置模板：`xhm-service/appsettings.json`
- Desktop 配置：`XhMonitor.Desktop/appsettings.json`
- Desktop 服务端点：`XhMonitor.Desktop/service-endpoints.json`
- Web 开发配置：`xhmonitor-web/vite.config.ts`

完整配置字段见 [docs/appsettings-reference.md](docs/appsettings-reference.md)。

## 日志

Rust Service 默认向控制台输出 `info` 及以上级别日志，可通过 `RUST_LOG` 覆盖过滤规则。同时以非阻塞方式写入每日文件：

- 发布环境：`Service/logs/xhmonitor.YYYY-MM-DD.log`；
- 开发环境：`target/debug/logs/xhmonitor.YYYY-MM-DD.log`；
- 文件记录 `debug` 及以上级别，服务退出时会刷完缓冲日志。

## 数据生命周期

Rust Service 使用 SQLite 存储指标，并按以下层级聚合和保留：

- raw：短期原始采样；
- minute：分钟聚合；
- hour：小时聚合；
- day：日聚合。

生命周期 worker 只有在目标层 coverage 验证完成后才删除源数据。`MetricLifecycleCheckpoints` 保存 Minute、Hour、Day 各层的连续覆盖起点和完成边界，避免重复聚合，并确保源数据仅在下一级聚合完整后删除。

旧版大数据库首次升级时会创建当前 schema 的新数据库，只复制应用配置和告警配置，不复制历史指标。`20260810000000_AddMetricLifecycleStorage` 是该一次性重建完成的唯一 marker，不根据 `MetricLifecycleCheckpoints` 表是否存在判断。

若旧数据库被 DBX 等外部程序占用，Service 对 `SQLITE_BUSY` / `SQLITE_LOCKED` 最多等待 1 秒，随后保留原数据库继续启动且不写 marker；释放占用后，下次启动会重新尝试重建。数据库损坏、字段缺失或磁盘错误不会降级。未来 schema 变更必须使用新的 `MigrationId`，不得复用现有 marker。

## API

默认地址：

- REST API：`http://127.0.0.1:35179/api/v1`
- 健康检查：`http://127.0.0.1:35179/api/v1/config/health`
- SignalR Hub：`http://127.0.0.1:35179/hubs/metrics`
- Web 界面：`http://127.0.0.1:35180`

常用请求：

```text
GET /api/v1/metrics/latest
GET /api/v1/metrics/history
GET /api/v1/metrics/processes
GET /api/v1/config
GET /api/v1/config/health
```

## 测试

Rust workspace：

```powershell
cargo fmt --check
cargo test --workspace -- --test-threads=1
cargo clippy --workspace --all-targets -- -D warnings
```

C# Desktop：

```powershell
dotnet test XhMonitor.Desktop.Tests/XhMonitor.Desktop.Tests.csproj
```

LHM bridge：

```powershell
dotnet build lhm-bridge/lhm-bridge.csproj -c Release
```

## 发布

构建绿色版：

```powershell
.\publish.ps1 -Version "1.0.0"
.\publish.ps1 -Version "1.0.0" -Lite
```

构建安装器：

```powershell
.\build-installer.ps1 -Version "1.0.0" -BuildType LiteNet8
.\build-installer.ps1 -Version "1.0.0" -BuildType Full
```

发布目录包含：

```text
XhMonitor-v1.0.0/
├── Service/
│   ├── xhm-service.exe
│   ├── lhm-bridge.exe
│   ├── appsettings.json
│   └── tools/RyzenAdj/
├── Desktop/
│   └── XhMonitor.Desktop.exe
├── 启动服务.bat
├── 停止服务.bat
└── README.txt
```

## License

[MIT License](LICENSE)
