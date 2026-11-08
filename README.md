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

桌面“关于”和“设置”使用独立普通窗口，不继承监控悬浮窗的置顶层级，避免遮挡它们启动的安装器；监控悬浮窗本身仍保持置顶。设置页在后端重启后直接查找已加载的悬浮窗重连 SignalR，不依赖窗口 Owner 关系。

显存（VRAM）已用量和总容量均采用专用显存口径，排除 Shared 传感器。同一 GPU 的厂商接口与 D3D Dedicated 读数不重复累计，多 GPU 累计各自的专用值。桌面显存容量未知时显示 `-`，任务栏不生成使用百分比，不使用共享内存或假定的 `8G` 上限补足。

Web 磁盘卡片保持每块物理盘一个位置，悬浮向下展开有卷标且已挂载的卷；展开列表复用磁盘卡片的紧凑容量条和玻璃面板样式，不展示说明段落。点击图钉可原位显示其中一个卷，固定后展开列表顶部保留物理磁盘汇总，点击其取消固定按钮恢复磁盘卡片。固定设置保存在当前浏览器的布局配置中，按设备标识和卷 GUID 区分，不按盘符或型号匹配；固定卷暂时不可用时回到磁盘视图，恢复连接后重新显示。触屏可点击展开，键盘可聚焦、展开并通过 Escape 收起。

磁盘汇总容量只统计可读取的有卷标挂载卷，不包含未挂载、无卷标或无法读取的文件系统；无法读取的 Btrfs 等文件系统不以分区长度推算已用量。没有可读卷时，仅显示物理总容量并标明已用未知。`R:/W:` 始终为物理盘读写速率，固定卷后也不变；悬浮提示保留简短口径标注，不增加常驻卡片行。

磁盘卡片标题右侧显示剩余空间和物理盘温度，字体、灰色及透明度与已用／总容量数字一致。剩余空间随固定卷切换；温度始终取所属物理盘，不随卷切换。温度通过 Windows 原生存储温度属性只读查询，不重新启用 LHM Storage；查询失败、不支持或未报告温度时直接隐藏，不保留旧值，也不显示占位符。

Rust Service 的 CPU 功耗限制读写面向 Ryzen AI Max 395（Strix Halo），由 Rust 直接访问已安装的 PawnIO 驱动，并加载编译时嵌入的官方签名 RyzenSMU 模块。使用该功能需要安装 PawnIO 并以管理员权限运行 Service；不依赖 WinRing0，也不会每秒启动 RyzenAdj CLI。PawnIO 同时用于支持该驱动的 CPU 传感器访问。

功耗采样复用设备句柄，监控线程与 API 共享 3 秒缓存（失败也节流）；写入后失效缓存以便验证实际限制。权限、模块初始化失败会停用至服务重启；运行期连续 3 次操作失败后停止访问。PCI 互斥量等待上限为 100 ms，不进行用户态忙等或无限重试。这些措施减少访问开销，但不构成硬件稳定性保证。

嵌入模块来自 [PawnIO.Modules 0.2.11](https://github.com/namazso/PawnIO.Modules/tree/0.2.11)，按 LGPL-2.1-or-later 许可分发；发布包在 `Service/tools/PawnIO/Modules/` 中同时提供 `COPYING`、`VERSION.txt`（版本、源码地址及校验值）及对应完整源码包 `source-0.2.11.zip`，无需另附模块二进制。

从旧版覆盖安装时，安装器仅依据 HKLM 中本程序的既有安装记录迁移 WinRing0：只删除名称、安装内已知映像路径和内核类型均匹配、且已经停止的 `R0lhm-bridge` / `R0XhMonitor_Service` 服务，不主动停止或卸载运行中的驱动。旧文件仅限 `Service/tools/RyzenAdj/WinRing0x64.sys`、`WinRing0x64.dll` 及 `Service/` 下的 `lhm-bridge.sys`、`XhMonitor.Service.sys`、`LibreHardwareMonitorLib.sys`；仍被服务引用、被占用、存在链接或无法核实归属时保留，并在安装日志中记录原因。通用共享 WinRing0 服务、系统目录和 PawnIO 不会被清理；交互安装仅在完成页显示简短保留说明，不单独弹窗，具体驱动、路径及失败原因只写安装日志；静默安装只记录日志，不自动重启。

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
├── tools/PawnIO/        # 安装器与嵌入的官方签名 RyzenSMU 模块
├── tools/RyzenAdj/      # 保留供旧 C# 代码使用，不随新版本分发
├── scripts/
├── publish.ps1
├── build-installer.ps1
└── installer/XhMonitor.iss
```

## 环境要求

- Windows 10/11 x64。
- Rust 1.82 或更高版本，MSVC toolchain。
- .NET SDK 8。
- Node.js 20.19+ 或 22.12+（Vite 7 要求）；`mise.toml` 固定使用 Node.js 24。
- Visual Studio 2022 Build Tools。
- 构建安装器时需要 Inno Setup 6.x。

## 开发运行

推荐使用 [mise](https://mise.jdx.dev/) 管理开发环境。根目录 `mise.toml` 固定 Rust `1.94.0`、.NET SDK `8.0.427`、Node.js `24.22.1`；.NET SDK 使用独立安装目录，避免被其他已安装的 SDK 版本覆盖。

首次使用（已安装 mise，并准备好 Visual Studio 2022 Build Tools 的 C++ 工具链和 Windows SDK）：

```powershell
mise trust
mise install
mise run
```

之后执行 `mise run`、`mise run dev` 或 `mise run start` 即可一键启动整个开发环境。任务会先安装 Web 依赖、构建硬件传感器 bridge 和 Rust Service，再调用 `start-all.ps1`：先启动 Vite 并等待其监听 `35180`，再在独立窗口运行 Desktop，由 Desktop 自动拉起 Rust 后端，避免重复启动 Service。任务返回后，各组件继续运行；停止时通过托盘退出 Desktop，并在 Vite 窗口按 `Ctrl+C`。

| 命令 | 用途 |
|------|------|
| `mise run` / `mise run dev` / `mise run start` | 一键运行 Desktop、Rust 后端和 Vite 开发服务器 |
| `mise run run` | 仅运行 Desktop 及其后端和内嵌 Web 页面，不启动 Vite |
| `mise run run:service` | 单独运行后端，先构建硬件传感器 bridge |
| `mise run run:web` | 单独运行 Vite 热更新服务器，先安装 Web 依赖 |
| `mise run deps:web` | 按 `package-lock.json` 安装前端依赖，依赖未变时跳过 |
| `mise run build:bridge` | 构建 Debug 硬件传感器 bridge |
| `mise run build:service` | 按 `Cargo.lock` 构建 Rust 后端 |
| `mise tasks ls` | 查看任务列表 |

后端健康检查地址为 `http://127.0.0.1:35179/api/v1/config/health`，Vite 地址为 `http://localhost:35180`。一键任务要求 `35180` 空闲，不会终止已有服务；Desktop 的内嵌 Web 会选择其他空闲端口，后端 Web gateway 遇到端口占用会继续提供 API。仅开发前端时，也可先执行 `mise run run:web`，再在另一终端执行 `mise run run:service`。

硬件传感器和 PawnIO 功耗功能需要管理员权限及相应驱动；mise 不会自动提权或安装系统驱动。

也可以继续使用以下手动命令，或保留的 `start-all.ps1` 脚本：

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

安装器迁移行为测试（需要 Inno Setup 6，测试只操作隔离临时文件，不修改真实驱动服务）：

```powershell
$testOutput = Join-Path $env:TEMP 'XhMonitorInstallerTests'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
ISCC.exe "/O$testOutput" installer/tests/LegacyWinRingMigration.Tests.iss
& "$testOutput/LegacyWinRingMigration.Tests.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART "/LOG=$testOutput/migration-tests.log"
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
│   └── tools/PawnIO/Modules/
│       ├── COPYING
│       ├── VERSION.txt
│       └── source-0.2.11.zip
├── Desktop/
│   └── XhMonitor.Desktop.exe
├── 启动服务.bat
├── 停止服务.bat
└── README.txt
```

## License

[MIT License](LICENSE)
