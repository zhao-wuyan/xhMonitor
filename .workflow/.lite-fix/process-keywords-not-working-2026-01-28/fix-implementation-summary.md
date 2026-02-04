# 进程关键字动态重载功能实现总结

## 问题描述

用户在设置页面添加进程关键字后,新添加的关键字不生效。原因是 ProcessScanner 从 `appsettings.json` 读取关键字,而设置页面将关键字保存到数据库,两个数据源完全不同步。

## 解决方案

采用**方案1: 动态重载机制**,实现关键字立即生效,无需重启服务。

## 实现细节

### 1. ProcessScanner 修改 (XhMonitor.Service/Core/ProcessScanner.cs)

#### 1.1 添加依赖注入
```csharp
// 添加数据库访问能力
private readonly IDbContextFactory<MonitorDbContext> _contextFactory;

// 构造函数注入
public ProcessScanner(
    ILogger<ProcessScanner> logger,
    IConfiguration config,
    IProcessNameResolver nameResolver,
    IDbContextFactory<MonitorDbContext> contextFactory)  // 新增参数
```

#### 1.2 关键字字段改为可变
```csharp
// 从 readonly 改为可变,支持动态更新
private List<string> _includeKeywords;
private List<string> _excludeKeywords;

// 添加线程安全锁
private readonly object _keywordsLock = new();
```

#### 1.3 添加公共重载方法
```csharp
/// <summary>
/// 从数据库重新加载进程关键字
/// </summary>
public async Task ReloadKeywordsAsync()
{
    // 1. 从数据库读取 ProcessKeywords 配置
    // 2. 使用锁保护更新关键字列表
    // 3. 记录日志
}
```

#### 1.4 初始化时异步加载数据库关键字
```csharp
// 构造函数中启动异步加载
_ = Task.Run(async () => await TryLoadKeywordsFromDatabaseAsync());

// 私有方法:尝试从数据库加载
private async Task TryLoadKeywordsFromDatabaseAsync()
{
    // 如果数据库中有配置,覆盖 appsettings.json 的默认值
    // 如果失败,保持使用 appsettings.json 的默认值
}
```

#### 1.5 线程安全保护
```csharp
// GetMatchedKeywords() 方法中添加锁
private List<string> GetMatchedKeywords(string commandLine)
{
    lock (_keywordsLock)
    {
        // 读取关键字列表
    }
}

// ProcessSingleProcess() 方法中添加锁
lock (_keywordsLock)
{
    shouldFilter = (_includeKeywords.Count != 0 || _excludeKeywords.Count != 0) && matchedKeywords.Count <= 0;
}
```

### 2. ConfigController 修改 (XhMonitor.Service/Controllers/ConfigController.cs)

#### 2.1 注入 ProcessScanner
```csharp
private readonly ProcessScanner _processScanner;

public ConfigController(
    IDbContextFactory<MonitorDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<ConfigController> logger,
    MetricProviderRegistry providerRegistry,
    ProcessScanner processScanner)  // 新增参数
```

#### 2.2 UpdateSettings() 方法中触发重载
```csharp
[HttpPut("settings")]
public async Task<IActionResult> UpdateSettings([FromBody] Dictionary<string, Dictionary<string, string>> settings)
{
    // ... 保存配置到数据库 ...

    // 检测是否更新了进程关键字
    if (processKeywordsUpdated)
    {
        _logger.LogInformation("检测到进程关键字更新,触发 ProcessScanner 重新加载");
        await _processScanner.ReloadKeywordsAsync();
    }

    return Ok(...);
}
```

## 工作流程

### 启动流程
```
1. ProcessScanner 构造函数执行
   ↓
2. 从 appsettings.json 加载默认关键字
   ↓
3. 启动后台任务 TryLoadKeywordsFromDatabaseAsync()
   ↓
4. 如果数据库中有配置,覆盖默认关键字
   ↓
5. ProcessScanner 准备就绪
```

### 更新流程
```
1. 用户在设置页面修改关键字
   ↓
2. SettingsViewModel.SaveSettingsAsync() 保存到数据库
   ↓
3. 调用 ConfigController.UpdateSettings() API
   ↓
4. 检测到 ProcessKeywords 更新
   ↓
5. 调用 ProcessScanner.ReloadKeywordsAsync()
   ↓
6. 从数据库读取最新关键字
   ↓
7. 使用锁更新关键字列表
   ↓
8. 下次进程扫描立即使用新关键字 ✓
```

## 技术特性

### 1. 向后兼容
- appsettings.json 中的 Monitor:Keywords 仍作为默认值
- 如果数据库为空或读取失败,使用 appsettings.json 的配置
- 不影响现有部署

### 2. 线程安全
- 使用 `lock (_keywordsLock)` 保护关键字列表的读写
- 支持并发进程扫描 (Parallel.ForEach)
- 避免竞态条件

### 3. 立即生效
- 保存设置后立即触发重载
- 无需重启服务
- 下次采集周期即可使用新关键字

### 4. 错误处理
- 数据库读取失败时保持当前关键字不变
- 记录详细日志便于排查问题
- 不影响进程扫描的正常运行

## 测试步骤

### 1. 启动服务
```bash
cd XhMonitor.Service
dotnet run
```

### 2. 检查初始关键字
查看日志输出:
```
ProcessScanner initialized with X include, Y exclude keywords
已从数据库加载进程关键字: X 个包含关键字, Y 个排除关键字
```

### 3. 修改关键字
1. 打开设置窗口
2. 在进程关键字列表中添加新关键字 (例如: "chrome")
3. 点击保存按钮

### 4. 验证重载
查看日志输出:
```
批量更新 X 个配置项
检测到进程关键字更新,触发 ProcessScanner 重新加载
进程关键字已重新加载: X 个包含关键字, Y 个排除关键字
```

### 5. 验证生效
1. 启动包含新关键字的进程 (例如: chrome.exe)
2. 检查进程是否被监控
3. 在悬浮窗或 Web 界面中查看进程列表

## 构建结果

```
✓ XhMonitor.Core -> bin/Debug/net8.0/XhMonitor.Core.dll
✓ XhMonitor.Service -> bin/Debug/net8.0/XhMonitor.Service.dll

已成功生成。
    0 个警告
    0 个错误
```

## 文件修改清单

1. **XhMonitor.Service/Core/ProcessScanner.cs**
   - 添加 IDbContextFactory 依赖注入
   - 添加 ReloadKeywordsAsync() 公共方法
   - 添加 TryLoadKeywordsFromDatabaseAsync() 私有方法
   - 关键字字段改为可变
   - 添加线程安全锁保护

2. **XhMonitor.Service/Controllers/ConfigController.cs**
   - 注入 ProcessScanner
   - UpdateSettings() 方法中检测 ProcessKeywords 更新
   - 触发 ProcessScanner.ReloadKeywordsAsync()

## 优点

1. **用户体验好**: 立即生效,无需重启
2. **实现简洁**: 代码改动最小,逻辑清晰
3. **向后兼容**: 保留 appsettings.json 作为默认值
4. **线程安全**: 正确处理并发访问
5. **错误容错**: 失败时保持当前状态

## 注意事项

1. ProcessScanner 是单例服务,所有请求共享同一个实例
2. 关键字更新会影响所有后续的进程扫描
3. 如果数据库连接失败,会保持使用当前关键字
4. 日志级别建议设置为 Information 以便观察重载过程

---

**实现者**: 哈雷酱 (傲娇大小姐工程师)
**实现日期**: 2026-01-28
**实现方案**: 方案1 - 动态重载机制
**状态**: ✓ 实现完成,构建成功
