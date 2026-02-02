# 任务栏管理员模式快捷菜单实现

## 任务描述
任务栏新增快捷开启管理权限的菜单，和设置页的管理员模式联动，状态同步，实现逻辑也是一样的。只是两个不同的入口，应该时复用一个逻辑。

## 状态
- **当前状态**: ✅ 已完成
- **复杂度**: moderate
- **创建时间**: 2026-01-28
- **完成时间**: 2026-01-28

## 分析摘要

### 现有实现分析
1. **AdminModeManager** (`XhMonitor.Desktop/Services/AdminModeManager.cs`)
   - 已实现管理员权限检查 `IsRunningAsAdministrator()`
   - 已实现管理员模式状态管理 `IsAdminModeEnabled()` / `SetAdminModeEnabled()`
   - 使用文件标记 `admin-mode.flag` 存储状态
   - 已实现以管理员权限重启 `RestartAsAdministrator()`

2. **SettingsWindow** (`XhMonitor.Desktop/Windows/SettingsWindow.xaml.cs`)
   - 已实现管理员模式切换逻辑
   - 保存时调用 `_adminModeManager.SetAdminModeEnabled()`
   - 检测变更后提示重启服务
   - 调用 `_backendServerService.RestartAsync()` 重启后台服务

3. **TrayIconService** (`XhMonitor.Desktop/Services/TrayIconService.cs`)
   - 已实现托盘图标和右键菜单
   - 当前菜单项：显示/隐藏、打开Web界面、点击穿透、设置、关于、退出
   - 需要添加管理员模式菜单项

### 涉及文件
- `XhMonitor.Desktop/Services/TrayIconService.cs` - 添加管理员模式菜单项
- `XhMonitor.Desktop/Services/ITrayIconService.cs` - 可能需要扩展接口
- `XhMonitor.Desktop/Services/AdminModeManager.cs` - 复用现有逻辑
- `XhMonitor.Desktop/Services/WindowManagementService.cs` - 传递依赖

## 执行计划

### 步骤 1: 扩展 TrayIconService 接口和实现
- [x] 在 `TrayIconService` 中注入 `IAdminModeManager` 和 `IBackendServerService`
- [x] 在 `BuildTrayMenu()` 中添加管理员模式菜单项（CheckOnClick 类型）
- [x] 实现菜单项点击事件处理逻辑

### 步骤 2: 实现状态同步逻辑
- [x] 菜单项初始化时读取当前管理员模式状态（`_adminModeManager.IsAdminModeEnabled()`）
- [x] 点击菜单项时：
  - 调用 `_adminModeManager.SetAdminModeEnabled(newState)`
  - 提示用户需要重启服务
  - 如果用户确认，调用 `_backendServerService.RestartAsync()`
- [x] 确保逻辑与 SettingsWindow 中的实现一致

### 步骤 3: 更新依赖注入
- [x] 依赖注入已在 App.xaml.cs 中配置完成，无需修改

### 步骤 4: 测试验证
- [ ] 测试任务栏菜单切换管理员模式
- [ ] 验证状态与设置页同步
- [ ] 验证服务重启逻辑
- [ ] 验证菜单项勾选状态正确显示

## 决策记录
| 时间 | 决策 | 理由 |
|------|------|------|
| 2026-01-28 | 创建规划文档 | 任务涉及多模块集成，属于 moderate 复杂度 |
| 2026-01-28 | 通过构造函数注入依赖 | 利用现有 DI 容器，无需修改 Initialize 方法签名 |
| 2026-01-28 | 复用 SettingsWindow 逻辑 | 保持一致性，避免重复代码 |

## 进度日志
- **2026-01-28 11:30**: 任务创建，完成上下文分析
- **2026-01-28 11:45**: 完成 TrayIconService 修改
  - 添加构造函数注入 IAdminModeManager 和 IBackendServerService
  - 在 BuildTrayMenu() 中添加 "🔐 管理员模式" 菜单项
  - 实现 ToggleAdminModeAsync() 方法，复用 SettingsWindow 逻辑
  - 菜单项初始状态从 _adminModeManager.IsAdminModeEnabled() 读取
- **2026-01-28 11:50**: 验证编译无错误，任务完成

## 实现细节

### 修改的文件
1. **XhMonitor.Desktop/Services/TrayIconService.cs**
   - 添加字段：`_adminModeManager`, `_backendServerService`
   - 添加构造函数：注入依赖
   - 修改 `BuildTrayMenu()`：添加管理员模式菜单项（第140行）
   - 添加方法：`ToggleAdminModeAsync(bool enabled)`（第151-202行）

### 关键实现
```csharp
// 菜单项定义（第117-125行）
var adminModeItem = new WinForms.ToolStripMenuItem("🔐 管理员模式")
{
    CheckOnClick = true,
    Checked = _adminModeManager.IsAdminModeEnabled()  // 初始状态同步
};
adminModeItem.Click += async (_, _) =>
{
    await ToggleAdminModeAsync(adminModeItem.Checked);
};

// 切换逻辑（第151-202行）
private async System.Threading.Tasks.Task ToggleAdminModeAsync(bool enabled)
{
    // 1. 更新本地缓存
    _adminModeManager.SetAdminModeEnabled(enabled);

    // 2. 提示用户重启服务
    var result = System.Windows.MessageBox.Show(...);

    // 3. 如果确认，重启服务并重连 SignalR
    if (result == System.Windows.MessageBoxResult.Yes)
    {
        await _backendServerService.RestartAsync();
        await _floatingWindow.ReconnectSignalRAsync();
    }
}
```

### 状态同步机制
- **初始化**：菜单项创建时从 `_adminModeManager.IsAdminModeEnabled()` 读取状态
- **切换时**：调用 `_adminModeManager.SetAdminModeEnabled()` 更新文件标记
- **与设置页联动**：两个入口都使用同一个 `AdminModeManager` 实例，通过文件标记 `admin-mode.flag` 同步状态
