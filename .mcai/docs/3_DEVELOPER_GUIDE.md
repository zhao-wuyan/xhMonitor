# 开发者指南

## 项目目的

XhMonitor 是一个高性能的 Windows 进程资源监控系统，提供实时采集、聚合分析和可视化展示。它在更大系统中担任监控和分析的角色，帮助系统管理员和开发人员深入了解 Windows 系统中各个进程的资源消耗模式。

**核心职责**:
- 实时采集进程的 CPU、内存、GPU、显存等资源使用情况
- 支持配置驱动的指标扩展
- 提供分层聚合数据存储（原始数据、分钟级、小时级、天级）
- 通过 Web 界面实时展示指标数据
- 提供历史数据查询和分析能力

**相关系统**:
- Windows Performance Counter API - 获取系统性能数据
- System.Diagnostics.Process API - 获取进程信息
- SQLite 数据库 - 持久化存储监控数据
- SignalR - 实时数据推送

---

## 环境搭建

### 前置条件

- .NET 8 SDK
- Node.js 18+ 和 npm
- Windows 操作系统（监控功能依赖 Windows API）
- （可选）Visual Studio 2022 或 Visual Studio Code

### 后端开发

#### 安装

```bash
# 克隆仓库
git clone <repository-url>
cd XhMonitor

# 还原依赖
dotnet restore

# 构建项目
dotnet build
```

#### 运行

```bash
# 运行后端服务（开发模式）
cd XhMonitor.Service
dotnet run

# 服务将在 http://localhost:35179 启动
```

#### 数据库迁移

```bash
# 创建初始迁移（仅第一次）
dotnet ef migrations add InitialCreate --project XhMonitor.Service --startup-project XhMonitor.Service

# 应用迁移
dotnet ef database update --project XhMonitor.Service --startup-project XhMonitor.Service
```

### 前端开发

#### 安装

```bash
# 进入前端目录
cd xhmonitor-web

# 安装依赖
npm install
```

#### 运行

```bash
# 启动开发服务器
npm run dev

# 前端将在 http://localhost:5173 启动
```

#### 构建

```bash
# 构建生产版本
npm run build

# 预览生产构建
npm run preview
```

---

## 环境变量

| 变量 | 必需 | 描述 | 示例 |
|------|------|------|------|
| `ConnectionStrings__DatabaseConnection` | 是 | SQLite 数据库连接字符串 | `Data Source=monitor.db` |
| `Monitor__IntervalSeconds` | 否 | 监控采集间隔（秒） | `5` |
| `Monitor__Keywords` | 否 | 进程过滤关键词数组 | `chrome,python,node` |
| `MetricProviders__PluginDirectory` | 否 | 指标插件目录 | `plugins` |

⚠️ **绝不提交密钥**。使用 `appsettings.Development.json` 或用户密钥管理器。

### 配置示例 (appsettings.Development.json)

```json
{
  "ConnectionStrings": {
    "DatabaseConnection": "Data Source=monitor.db"
  },
  "Monitor": {
    "IntervalSeconds": 5,
    "Keywords": ["chrome", "python", "node"]
  },
  "MetricProviders": {
    "PluginDirectory": "plugins"
  },
  "Serilog": {
    "MinimumLevel": "Debug",
    "WriteTo": [
      {
        "Name": "File",
        "Args": {
          "path": "logs/monitor-.log",
          "rollingInterval": "Day"
        }
      }
    ]
  }
}
```

---

## 开发工作流

### 代码质量工具

| 工具 | 命令 | 目的 |
|------|------|------|
| .NET Build | `dotnet build` | 编译检查 |
| .NET Test | `dotnet test` | 单元/集成测试 |
| ESLint | `npm run lint` | 前端代码检查 |
| TypeScript | `npm run build` | 类型检查 |

### 提交前检查

这些会在提交前手动运行：
1. 后端编译：`dotnet build`
2. 前端编译：`npm run build`
3. 后端测试：`dotnet test`
4. 前端检查：`npm run lint`

### 分支策略

- `main` - 生产就绪代码
- `staging` - 预生产测试
- `feature/*` - 新功能
- `fix/*` - Bug 修复

### Pull Request 流程

1. 从 `main` 创建功能分支
2. 编写代码和测试
3. 运行所有检查
4. 创建 PR 并填写描述
5. 处理审查反馈
6. Squash 合并

---

## 修改建议区域

### 安全起步点 🟢

这些区域适合熟悉代码库：

| 区域 | 位置 | 安全原因 |
|------|------|---------|
| 工具函数 | `xhmonitor-web/src/utils.ts` | 隔离性好，测试充分 |
| React 组件 | `xhmonitor-web/src/components/` | 自包含 UI |
| 单元测试 | `SqliteTest/` | 不会破坏生产环境 |

### 中等风险 🟡

修改前需理解依赖关系：

| 区域 | 位置 | 注意事项 |
|------|------|---------|
| REST API 控制器 | `XhMonitor.Service/Controllers/` | 外部契约，需保持兼容 |
| 指标提供者 | `XhMonitor.Core/Providers/` | 可能影响监控功能 |
| React Hooks | `xhmonitor-web/src/hooks/` | 全局状态管理 |

### 高风险 🔴

修改前需与团队讨论：

| 区域 | 位置 | 风险原因 |
|------|------|---------|
| PerformanceMonitor | `XhMonitor.Service/Core/PerformanceMonitor.cs` | 所有监控功能依赖它 |
| 数据库模型 | `XhMonitor.Core/Entities/` | 数据迁移风险 |
| MetricProviderRegistry | `XhMonitor.Service/Core/MetricProviderRegistry.cs` | 插件系统核心 |

---

## 常见任务

### 添加新的指标采集器

**需修改的文件**:
1. `XhMonitor.Core/Providers/[YourMetric]Provider.cs` - 实现指标采集器
2. `XhMonitor.Service/Core/MetricProviderRegistry.cs` - 注册新指标（可选）
3. `xhmonitor-web/src/i18n.ts` - 添加国际化文本（可选）

**步骤**:
1. 创建新类实现 `IMetricProvider` 接口
2. 实现 `CollectAsync` 方法采集指标数据
3. 如果需要，在 `MetricProviderRegistry` 中手动注册
4. 添加单元测试
5. 前端会自动发现并显示新指标

**示例**:
```csharp
// XhMonitor.Core/Providers/DiskMetricProvider.cs
public class DiskMetricProvider : IMetricProvider
{
    public string MetricId => "disk";
    public string DisplayName => "Disk Usage";
    public string Unit => "MB";
    public MetricType Type => MetricType.Gauge;

    public bool IsSupported() => true;

    public async Task<MetricValue> CollectAsync(int processId)
    {
        // 实现磁盘使用率采集逻辑
        var diskUsage = await GetDiskUsageAsync(processId);
        return new MetricValue { Value = diskUsage, Unit = Unit };
    }

    public void Dispose() { }
}
```

**示例提交**: `feat: add disk usage metric provider`

---

### 添加新的 REST API 端点

**需修改的文件**:
1. `XhMonitor.Service/Controllers/[YourController].cs` - 添加 API 端点
2. `xhmonitor-web/src/hooks/` - 添加前端 Hook（可选）

**步骤**:
1. 在控制器中定义新的 Action 方法
2. 实现业务逻辑
3. 添加输入验证
4. 编写测试
5. 更新接口文档

**示例**:
```csharp
[HttpGet("summary")]
public async Task<IActionResult> GetSummary()
{
    await using var context = await _contextFactory.CreateDbContextAsync();
    var summary = await context.ProcessMetricRecords
        .GroupBy(r => r.MetricId)
        .Select(g => new
        {
            MetricId = g.Key,
            Count = g.Count(),
            AvgValue = g.Average(r => r.MetricValue)
        })
        .ToListAsync();
    return Ok(summary);
}
```

**示例提交**: `feat(api): add GET /metrics/summary endpoint`

---

### 添加数据库 migration

**需创建的文件**:
1. `XhMonitor.Service/Migrations/[Timestamp]_[Name].cs`

**步骤**:
1. 生成迁移文件：`dotnet ef migrations add AddYourFeature`
2. 审查生成的 Up 和 Down 方法
3. 本地测试：`dotnet ef database update`
4. 测试回滚：`dotnet ef database update <previous-migration>`
5. 提交迁移文件

⚠️ **绝不修改已部署的 migration**

---

### 添加新的前端组件

**需修改的文件**:
1. `xhmonitor-web/src/components/[YourComponent].tsx` - 创建组件
2. `xhmonitor-web/src/App.tsx` - 集成组件

**步骤**:
1. 创建组件文件
2. 使用 TailwindCSS 样式
3. 使用 `useMetricsHub` Hook 获取实时数据
4. 使用 ECharts 渲染图表（如需要）
5. 添加国际化支持

**示例**:
```typescript
import { useMetricsHub } from '../hooks/useMetricsHub';
import { t } from '../i18n';

export const MyComponent = () => {
  const { metricsData } = useMetricsHub();

  return (
    <div className="glass rounded-xl p-6">
      <h2 className="text-2xl font-bold">{t('My Component')}</h2>
      {/* 组件内容 */}
    </div>
  );
};
```

**示例提交**: `feat(ui): add custom metrics dashboard component`

---

### 修复 Bug

**流程**:
1. 编写复现 bug 的失败测试
2. 在代码中定位根因
3. 用最小改动修复
4. 验证测试通过
5. 检查其他地方是否有类似问题

**示例提交**: `fix(monitor): handle missing PerformanceCounter gracefully`

---

## 编码规范

### 文件组织
- 每个文件一个类/组件
- 文件以其默认导出命名
- 相关文件放在同一目录

### 命名

| 类型 | 约定 | 示例 |
|------|------|------|
| C# 文件 | PascalCase | `CpuMetricProvider.cs` |
| C# 类 | PascalCase | `CpuMetricProvider` |
| C# 方法 | PascalCase | `CollectAsync` |
| C# 私有字段 | _camelCase | `_logger` |
| TypeScript 文件 | PascalCase (组件) / camelCase (工具) | `MetricChart.tsx`, `utils.ts` |
| TypeScript 组件 | PascalCase | `MetricChart` |
| TypeScript 函数 | camelCase | `useMetricsHub` |

### 错误处理

```csharp
// 推荐：特定错误类型
throw new InvalidOperationException("Process not found");

// 避免：通用错误
throw new Exception("Something went wrong");
```

### 日志

```csharp
// 包含上下文
_logger.LogInformation("Metrics collected for process {ProcessId}", processId);

// 使用适当级别
_logger.LogDebug();   // 开发详情
_logger.LogInformation(); // 正常操作
_logger.LogWarning();  // 可恢复问题
_logger.LogError();   // 需要关注的故障
```

### 测试
- 测试文件: `[Name].Test.cs` 与源码同目录（后端）
- 测试文件: `[Name].test.ts` 与源码同目录（前端）
- describe 块: 匹配类/函数名
- 测试名: "should [预期行为] when [条件]"

---

## 已知改进机会

### 测试
- [ ] 提高 `XhMonitor.Service/` 覆盖率（当前较低）
- [ ] 为关键流程添加集成测试
- [ ] 为前端组件添加更多单元测试

### 文档
- [ ] 为内部 API 添加 XML 注释
- [ ] 添加架构决策记录（ADR）

### 技术债务
- [ ] 修复 `MaxDegreeOfParallelism=1` 的串行化问题
- [ ] 从 PerformanceCounter 迁移到 WMI 异步 API
- [ ] 添加数据重试机制
- [ ] 配置化硬编码参数（如超时时间）

### 性能
- [ ] 优化大量进程时的性能
- [ ] 为常用查询添加数据库索引
- [ ] 实现数据压缩存储

### 功能
- [ ] 添加告警通知功能（邮件、Webhook）
- [ ] 支持进程分组
- [ ] 添加数据导出功能
- [ ] Electron 桌面端开发

---

## 故障排查

### 常见问题

**问题**: SignalR 连接失败
- 检查后端服务是否运行在 `http://localhost:35179`
- 检查 CORS 配置是否正确
- 查看浏览器控制台错误信息

**问题**: 指标采集超时
- 检查进程是否仍然存在
- 查看后端日志中的警告信息
- 调整 `MaxDegreeOfParallelism` 或超时时间

**问题**: 数据库连接失败
- 检查连接字符串是否正确
- 确保数据库文件有写入权限
- 检查是否已应用数据库迁移

**问题**: 前端构建失败
- 删除 `node_modules` 和 `package-lock.json`，重新运行 `npm install`
- 检查 Node.js 版本是否符合要求
- 查看编译错误信息

### 调试技巧

**后端**:
- 使用 `appsettings.Development.json` 启用详细日志
- 在 Visual Studio 中使用断点调试
- 使用 Serilog 输出到文件

**前端**:
- 使用浏览器开发者工具查看网络请求
- 使用 React DevTools 查看组件状态
- 在 VS Code 中使用断点调试

