# G4 Execute 报告

Run：`20260727-006-execute`
Goal：`G4`
Tasks：`TASK-012`、`TASK-013`、`TASK-014`

## 交付结果

### TASK-012：任务栏指标窗口

已实现独立 `TaskbarWindow` 与独立 SSE 投影路径。内部支持 `TextHorizontal`、`TextVertical`、`BarHorizontal`、`BarVertical` 四种呈现；生产规则固定为 Top/Bottom 横向、Left/Right 纵向，并由 `DockVisualStyle` 选择 Text/Bar。

指标覆盖上传、下载、CPU、RAM、GPU、VRAM、Power，包含 compact units 与温度 trailing text。布局 fingerprint 包含样式、方向、可见列、label、unit token 与 gap；数值变化通过 `set_row_data` 原地更新，只有布局输入变化才 reset model。

拖拽只转发 pointer intent，物理坐标、cursor anchor、80 px/半窗越界吸附使用既有 G2 `drag_anchor`、`origin_for_anchor`、`snap_taskbar_window` 与 `NativeWindowPositionOps`。未修改 G3 `floating_window.rs` 状态机或 drag clamp。

### TASK-013：Settings、About、更新检查

Settings 只序列化以下允许项：

- `Appearance.Opacity`（20..100）；
- `DataCollection.ProcessKeywords`（JSON array string）；
- 既有 Monitoring 指标开关、双显示模式开关、7 个 dock labels、`DockColumnGap`（0..24）和 `DockVisualStyle`。

Admin、startup、LAN、firewall、system 明确显示为 P3-only 且不进入 PUT body。GET/PUT 成功后更新共享 taskbar settings；错误只更新状态文案，不覆盖当前编辑值。

About 显示 Rust package version，并支持 `Idle`、`Checking`、`SourceUnavailable`、`UpToDate`、`UpdateAvailable`、`Error`。更新逻辑只向固定 Gitee latest-release URL 发出一次 GET，解析 tag/name/body/assets 并比较版本；不持久化 release 数据，也不启动外部程序。

Tray 的 Settings/About/notification-click 已接入真实 Slint 窗口；Web 打开既有 `http://127.0.0.1:35180`；Admin 保持 P3 deferred。

## 验证

- `cargo test -p xhm-desktop`：连续 3 次通过，每次 `125 passed, 0 failed`；
- `cargo clippy -p xhm-desktop --all-targets -- -D warnings`：通过；
- `cargo build -p xhm-desktop --release`：通过；
- Anti-pattern：update side-effect patterns 为 0；global-hotkey patterns 为 0；
- Release smoke：`winit-software` 启动成功；日志记录 distinct dual HWND、taskbar placement、双 SSE runtime 和 taskbar/settings/About 启动路径；Orca 观察到 live `xhm-desktop` UI；
- JSON artifacts：由 Run 输出目录写入，交给最终 `maestro run check` 校验。

## 阻断项

`TASK-014` 的 Private Bytes 门禁未通过。release、`winit-software`、main+taskbar、dual-SSE 条件下，60 个一秒间隔样本为：

- 最小：`34.504 MiB`；
- 最大：`34.719 MiB`；
- `<10 MiB`：`0/60`。

原始 60 行数据位于 `outputs/memory-samples.json`。本 Run 未压低阈值、未隐藏失败，也未伪造通过证据。因此 G4 代码与 package quality 已完成，但最终 P2 memory gate 仍是明确 blocker，Run 保持 unsealed。
