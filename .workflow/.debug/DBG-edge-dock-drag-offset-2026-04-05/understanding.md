# Understanding Document

**Session ID**: DBG-edge-dock-drag-offset-2026-04-05
**Bug Description**: 贴边模式下拖拽有偏移，鼠标没有抓住贴边框，贴边框会跑到鼠标右侧一段距离
**Started**: 2026-04-05T00:00:00+08:00

---

## Exploration Timeline

### Iteration 1 - Initial Exploration (2026-04-05T00:00:00+08:00)

#### Current Understanding

基于 `TaskbarMetricsWindow` 与 `FloatingWindow` 的拖拽/贴边实现对比，问题集中在贴边窗自己的拖拽坐标换算：

- `TaskbarMetricsWindow` 在拖拽开始和拖拽过程中直接使用 `PointToScreen` / `Screen.WorkingArea`。
- `Window.Left` / `Window.Top`、`ActualWidth` / `ActualHeight` 使用的是 WPF 逻辑坐标（DIP）。
- `FloatingWindow` 已经实现了 `TransformFromDevice` 的设备像素到逻辑坐标转换，但贴边窗没有复用这层处理。

#### Evidence From Code Search

- `XhMonitor.Desktop/Windows/TaskbarMetricsWindow.xaml.cs`
  - `Window_MouseLeftButtonDown` 使用 `PointToScreen(cursorLocal)` 作为拖拽起点。
  - `Window_MouseMove` 使用 `PointToScreen(e.GetPosition(this))` 计算拖拽增量。
  - `TrySnapByHalfOut`、`GetNearestDockSide`、`ApplyDockSide`、`ResetToNearestNonTaskbarOverlap` 直接拿 `Screen.Bounds` / `Screen.WorkingArea` 与 `Left` / `Top` 比较。
- `XhMonitor.Desktop/FloatingWindow.xaml.cs`
  - 已有 `TransformDevicePointToLogical`、`TransformDeviceRectangleToLogical`，并用来处理屏幕坐标与窗口逻辑坐标之间的转换。

#### Hypotheses Generated

- `H1`: 贴边窗拖拽把设备像素增量直接叠加到逻辑坐标，导致在非 100% DPI 下窗口相对鼠标产生固定比例偏移。
- `H2`: 贴边状态切换为浮动态时只修正了首帧位置，但释放吸附和边界钳制仍使用设备像素，导致拖拽过程中或释放后继续出现位置漂移。

### Iteration 2 - Resolution (2026-04-05T00:00:00+08:00)

#### Fix Applied

- 在 `TaskbarMetricsWindow` 中新增设备像素 -> 逻辑坐标转换辅助方法，与 `FloatingWindow` 保持一致。
- 拖拽开始、拖拽移动改为统一使用逻辑屏幕坐标。
- 吸附判断、最近边识别、工作区钳制改为先把 `Screen.Bounds` / `Screen.WorkingArea` 转成逻辑坐标再参与计算。
- 为贴边窗坐标转换新增单测，覆盖点和矩形两种缩放场景。

#### Root Cause Identified

**H1 已确认**：`TaskbarMetricsWindow` 混用了 Win32/WinForms 屏幕设备像素与 WPF 窗口逻辑坐标，DPI 缩放下拖拽增量被放大，表现为鼠标没有“抓住”贴边框，窗体跑到鼠标右侧。

#### Lessons Learned

1. 在 WPF 桌面窗体中，只要同时使用 `Screen` / `Cursor` / `PointToScreen` 和 `Window.Left` / `Top`，就必须明确设备像素与逻辑坐标的转换边界。
2. 修复拖拽起点不足以彻底解决问题，吸附判断和工作区钳制也必须使用同一坐标系。

---

## Current Consolidated Understanding

### What We Know
- 贴边窗偏移是 DPI 坐标系混用导致，不是拖拽锚点比例算法本身的问题。
- `FloatingWindow` 已有成熟的坐标转换模式，贴边窗缺失了同等处理。
- 统一到逻辑坐标后，拖拽、吸附和边界修正会落在同一套坐标系上。

### What Was Disproven
- ~~仅是 `CalculateWindowTopLeftByDragAnchor` 算法错误~~（该算法只负责从贴边态切回浮动态时保持鼠标落点，问题出在进入拖拽后的坐标增量和后续吸附判断）

### Current Investigation Focus
验证坐标统一后的编译与单测结果，确认没有引入贴边释放和边界钳制回归。
