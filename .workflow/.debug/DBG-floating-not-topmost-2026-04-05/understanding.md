# Understanding Document

**Session ID**: DBG-floating-not-topmost-2026-04-05  
**Bug Description**: 用户反馈桌面悬浮窗偶发出现“非置顶”现象，会被其他窗口挡住；先判断可能原因，并评估是否需要在右键菜单补一个置顶相关入口。  
**Started**: 2026-04-05T11:00:08+08:00

---

## Exploration Timeline

### Iteration 1 - Initial Exploration (2026-04-05 11:00 +08:00)

#### Current Understanding

- 悬浮窗 `FloatingWindow` 在 XAML 上已经声明了 `Topmost="True"`，所以“缺少置顶开关”不是当前现象的直接解释。
- 贴边窗口 `TaskbarMetricsWindow` 除了 `Topmost="True"`，还在多个生命周期节点调用 Win32 `SetWindowPos(HWND_TOPMOST)` 反复回顶。
- 悬浮窗当前没有看到与贴边窗口等价的“失焦后回顶”或“拖拽后回顶”逻辑，更多依赖 WPF 的声明式 `Topmost`。
- 托盘右键菜单目前只有“显示/隐藏”“点击穿透”等动作，没有“重新置顶”或“置顶开关”入口。

#### Evidence from Code Search

- `XhMonitor.Desktop/FloatingWindow.xaml`
  - 第 13 行声明 `Topmost="True"`。
- `XhMonitor.Desktop/FloatingWindow.xaml.cs`
  - `OnSourceInitialized()` 只做句柄获取、位置恢复、位置纠偏和 `WndProc` hook，没有 Win32 级回顶。
  - `HandleWindowDragReleased()` 在拖拽结束后只尝试切换贴边模式，失败时只做位置纠偏，不做回顶。
  - `SetClickThrough()` 只切换 `WS_EX_TRANSPARENT`，不涉及 `HWND_TOPMOST`。
- `XhMonitor.Desktop/Services/WindowManagementService.cs`
  - `ShowMainWindow()` 仅执行 `_floatingWindow.Show(); _floatingWindow.Activate();`，没有额外回顶。
- `XhMonitor.Desktop/Windows/TaskbarMetricsWindow.xaml.cs`
  - `OnLoaded()`、`OnSourceInitialized()`、`OnDeactivated()`、拖拽结束后都会调用 `ReassertTopMost()`。
  - `ReassertTopMost()` 通过 `SetWindowPos(hwnd, HWND_TOPMOST, ..., SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOOWNERZORDER)` 强制回顶。
- `XhMonitor.Desktop/Services/TrayIconService.cs`
  - 托盘菜单没有“置顶”相关项，只有“点击穿透”。

#### Hypotheses Generated

- H1：悬浮窗只依赖 WPF 的 `Topmost="True"`，缺少 Win32 级回顶；在失焦、拖拽、显示切换后，窗口可能仍在 topmost band 内，但顺序落到别的 topmost 窗口后面。
- H2：悬浮窗在 `Show()/Hide()`、设置窗/关于窗打开关闭、托盘交互等场景后，只做 `Activate()` 不做 `SetWindowPos(HWND_TOPMOST)`，导致“看起来像失去置顶”。
- H3：部分用户口中的“被其他窗口挡住”其实是被同样 topmost 的窗口，甚至全屏/独占窗口覆盖；这种情况下新增“置顶开关”不会修复根因，最多提供一次手动回顶。
- H4：悬浮窗使用 `AllowsTransparency="True"` 的分层透明窗口，叠加多屏、DPI、拖拽等路径时，更容易暴露 Z 序维护缺口。

---

## Current Consolidated Understanding

### What We Know

- 当前实现里，悬浮窗默认就是置顶窗口，不是“用户忘了开启置顶”。
- 贴边窗口已经为置顶稳定性做了额外 Win32 兜底，而悬浮窗没有同等级别的兜底。
- 从静态代码看，最可疑的不是“缺少一个置顶菜单项”，而是“缺少在关键生命周期点重新声明 topmost”的实现。

### What Was Disproven

- ~~悬浮窗完全没有置顶能力~~
  - `FloatingWindow.xaml` 已明确声明 `Topmost="True"`。
- ~~右键菜单缺少置顶选项就是根因~~
  - 菜单缺项只会影响手动补救能力，不解释为什么默认置顶会失效或看起来失效。

### Iteration 2 - Why Only Some Users See It (2026-04-05 11:15 +08:00)

#### New Evidence

- 悬浮窗没有订阅 `Deactivated`，也没有 `ReassertTopMost()`；贴边窗口则明确在 `Deactivated` 中回顶。
- 悬浮窗拖拽结束后只做“切贴边”判断和位置纠偏，不做回顶；贴边窗口拖拽结束后会回顶。
- 悬浮窗通过托盘 `Show()/Hide()` 控制显示，显示时只调用 `Show()` + `Activate()`。
- 设置窗和关于窗通过 `ShowDialog()` 打开，并尽量把悬浮窗设为 `Owner`；关闭对话窗后，没有显式对悬浮窗做重新回顶。
- 悬浮窗是 `AllowsTransparency="True"` 的分层透明窗口；贴边窗口也是透明窗口，但额外补了 Win32 回顶，所以对环境波动更不敏感。

#### Analysis

这解释了“为什么不是所有用户都遇到”：

- **交互路径不同**
  - 只要用户不常拖拽、不常从托盘显隐、不常打开设置/关于，问题就可能长期不暴露。
  - 经常走这些路径的用户，更容易把悬浮窗带到一个没有被重新回顶的层级状态。
- **竞争窗口不同**
  - 如果用户桌面上还有别的 topmost/overlay 类窗口，悬浮窗仅靠初始化时那次 `Topmost=True` 更容易被压到后面。
  - 没有这类软件的用户，即使实现有缺口，也可能感知不到。
- **显示环境不同**
  - 多屏、不同 DPI、跨屏拖拽、全屏/无边框应用，会让“Z 序维护缺口”更容易被触发或放大。
  - 单屏、固定缩放、普通办公窗口环境下，现有实现可能看起来一直正常。
- **使用习惯不同**
  - 有些用户可能会进入点击穿透、频繁切换锁定/展开、通过托盘恢复窗口；这些都会增加窗口层级被系统或其他窗口重排的机会。

#### Updated Understanding

- 问题更像“代码里存在稳定性缺口，但只有在某些交互和环境条件下才暴露”，不是“只有部分机器代码路径不同”。
- 因此“部分用户不复现”并不能反证代码没问题，只说明他们还没踩中触发条件。

---

## Current Consolidated Understanding (Updated)

### What We Know

- 当前实现里，悬浮窗默认就是置顶窗口，不是“用户忘了开启置顶”。
- 贴边窗口已经为置顶稳定性做了额外 Win32 兜底，而悬浮窗没有同等级别的兜底。
- 悬浮窗的风险主要集中在这些交互点：
  - 拖拽结束
  - 托盘显示/隐藏
  - 打开/关闭设置或关于窗口
  - 与其他 topmost/overlay/fullscreen 窗口竞争 Z 序
- “有些用户有问题、有些用户没有”更符合“触发条件不同”而不是“代码行为随机”。

### What Was Disproven

- ~~悬浮窗完全没有置顶能力~~
  - `FloatingWindow.xaml` 已明确声明 `Topmost="True"`。
- ~~右键菜单缺少置顶选项就是根因~~
  - 菜单缺项只会影响手动补救能力，不解释为什么默认置顶会失效或看起来失效。
- ~~只有用户机器差异，代码本身没问题~~
  - 代码中确实存在与贴边窗口不一致的置顶维护缺口。

### Current Investigation Focus

- 优先验证悬浮窗是否在以下路径后掉出预期层级：
  - 拖拽结束后
  - 托盘显示/隐藏后
  - 打开/关闭设置或关于窗口后
  - 与其他 topmost 窗口同时存在时
- 如果需要补交互入口，更准确的产品语义应偏向“重新置顶”或“恢复前台层级”，而不是单纯再加一个“置顶开关”。

### Iteration 3 - Minimal Fix Applied (2026-04-05 11:25 +08:00)

#### Fix Applied

- 修改 `XhMonitor.Desktop/FloatingWindow.xaml.cs`
  - 为悬浮窗补充 `SetWindowPos(HWND_TOPMOST)` 级别的 `ReassertTopMost()`。
  - 在窗口 `Activated` / `Deactivated` 时通过 `Dispatcher.BeginInvoke` 重新声明 topmost。
  - 在 `OnSourceInitialized()` 和 `OnLoaded()` 后补一次回顶，覆盖启动和首次显示路径。
  - 在拖拽结束且未切换到贴边模式时，再补一次回顶，覆盖用户最容易触发的问题路径。

#### Verification Results

- `dotnet build XhMonitor.Desktop/XhMonitor.Desktop.csproj`：通过。
- `dotnet test XhMonitor.Desktop.Tests/XhMonitor.Desktop.Tests.csproj --no-build`：通过（73 / 73）。

#### Updated Understanding

- 现在悬浮窗在关键生命周期点已经与贴边窗口具备同类的 topmost 重申机制。
- 这属于“最小根修”，优先补实现缺口，而不是先增加一个手动菜单补救项。

### Iteration 4 - Right Edge Detection Gap Confirmed (2026-04-05 11:45 +08:00)

#### New Evidence

- `FloatingWindow` 本身是 `SizeToContent="WidthAndHeight"`。
- 右侧边缘检测和释放后的边界纠偏都直接使用：
  - `Math.Max(Width, ActualWidth)`
  - `Math.Max(Height, ActualHeight)`
- 对当前窗口来说，`Width` / `Height` 可能是 `NaN`；而 `Math.Max(double.NaN, 60)` 仍会得到 `NaN`，不能自动回退到 `ActualWidth`。
- 这会导致右侧/下侧相关计算拿到无效尺寸，进而让：
  - 拖拽释放后的边缘检测失真；
  - 工作区边界纠偏失效。

#### Fix Applied

- 修改 `XhMonitor.Desktop/FloatingWindow.xaml.cs`
  - 为悬浮窗新增 `ResolveWindowDimension(width, actualWidth, fallback)`。
  - 在 `TryActivateEdgeDockModeOnDragRelease()` 中改用安全尺寸解析，确保右/下边缘检测可用。
  - 在 `ResetToNearestNonTaskbarOverlap()` 中改用安全尺寸解析，确保释放后会被拉回工作区。
- 新增 `XhMonitor.Desktop.Tests/FloatingWindowDimensionTests.cs`
  - 覆盖 `Width/Height = NaN`、`ActualWidth/ActualHeight` 有值时的回退逻辑。

#### Verification Results

- 常规 `dotnet build` 会因为正在运行的 `XhMonitor.Desktop.exe` 锁住 `bin/Debug` 输出而失败，这不是源码错误。
- 使用独立输出目录验证通过：
  - `dotnet build XhMonitor.Desktop.Tests/XhMonitor.Desktop.Tests.csproj -p:OutDir=C:\ProjectDev\project\xinghe\xhMonitor\.codex-temp\verify2\ -p:UseAppHost=false`
  - `dotnet test .codex-temp/verify2/XhMonitor.Desktop.Tests.dll`
- 结果：77 / 77 通过。

#### Updated Understanding

- 用户描述的“左侧不会越出边界，但右侧可以拖出边界”是合理的：
  - 左侧更像是系统拖拽行为在兜底；
  - 右侧则暴露了我们自己的宽度解析缺口。
- 这不是“没有右侧边缘检测”，而是“右侧边缘检测在 `NaN` 尺寸下失效”。

---

## Current Consolidated Understanding (Latest)

### What We Know

- 当前实现里，悬浮窗默认就是置顶窗口，不是“用户忘了开启置顶”。
- 贴边窗口已经为置顶稳定性做了额外 Win32 兜底，而悬浮窗原先没有同等级别的兜底。
- 悬浮窗的风险主要集中在这些交互点：
  - 拖拽结束
  - 托盘显示/隐藏
  - 打开/关闭设置或关于窗口
  - 与其他 topmost/overlay/fullscreen 窗口竞争 Z 序
- “有些用户有问题、有些用户没有”更符合“触发条件不同”而不是“代码行为随机”。
- 已对悬浮窗补上最小回顶修复，优先覆盖启动、激活、失焦、拖拽结束四类关键路径。
- 已确认右侧越界问题来自 `SizeToContent` 窗口的 `NaN` 宽高参与边缘检测/边界纠偏计算。
- 已补安全宽高解析，覆盖右侧边缘检测和释放后的工作区回弹。

### What Was Disproven

- ~~悬浮窗完全没有置顶能力~~
  - `FloatingWindow.xaml` 已明确声明 `Topmost="True"`。
- ~~右键菜单缺少置顶选项就是根因~~
  - 菜单缺项只会影响手动补救能力，不解释为什么默认置顶会失效或看起来失效。
- ~~只有用户机器差异，代码本身没问题~~
  - 代码中确实存在与贴边窗口不一致的置顶维护缺口。

### Current Fix Status

- 已为悬浮窗补上 Win32 级 `ReassertTopMost()` 兜底。
- 已为悬浮窗补上 `ResolveWindowDimension()`，修复 `NaN` 宽高导致的右侧越界问题。
- 自动化验证已通过独立输出目录下的编译和桌面测试。
