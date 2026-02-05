# 文档变更预览

## README.md 变更详情

### 变更统计
- **文件**: README.md
- **变更类型**: 内容更新
- **新增行数**: +16
- **修改行数**: 4
- **删除行数**: 0

---

## 详细变更内容

### 1. Features 章节 - 多维度监控 (第 11 行)

```diff
- ✅ **多维度监控** - CPU、内存、GPU、显存、功耗、网络速度实时监控
+ ✅ **多维度监控** - CPU、内存、GPU、显存、硬盘、功耗、网络速度实时监控
```

**变更原因**: 新增硬盘指标监控功能 (commit: 83fd63a)

---

### 2. Features 章节 - 新增安全认证 (第 22 行)

```diff
  - ✅ **功耗管理** - RyzenAdj 集成，支持 AMD 平台功耗监控与调节
  - ✅ **设备验证** - 设备白名单机制，保护功耗调节功能
+ - ✅ **安全认证** - 访问密钥认证、IP 白名单、局域网访问控制
```

**变更原因**: 新增访问密钥认证功能 (commit: efdca1b)

---

### 3. Configuration 章节 - 数据库设置 (第 189-193 行)

```diff
  | `DataCollection.ProcessKeywords` | string[] | [] | 用户自定义进程关键词 |
  | `DataCollection.TopProcessCount` | int | 10 | 显示的 Top 进程数量 |
  | `System.StartWithWindows` | bool | false | 是否开机自启 |
+ | `System.EnableLanAccess` | bool | false | 是否启用局域网访问 |
+ | `System.EnableAccessKey` | bool | false | 是否启用访问密钥认证 |
+ | `System.AccessKey` | string | "" | 访问密钥（空表示未设置） |
+ | `System.IpWhitelist` | string | "" | IP 白名单（逗号分隔的 IP 地址或 CIDR） |
```

**变更原因**: 新增局域网访问控制和安全认证配置 (commits: efdca1b, b4df440)

---

### 4. Architecture 章节 - MetricProviders (第 274 行)

```diff
  │  └─ MetricProviders (指标采集器)                             │
  │     ├─ CpuMetricProvider                                     │
  │     ├─ MemoryMetricProvider                                  │
  │     ├─ GpuMetricProvider                                     │
  │     ├─ VramMetricProvider                                    │
+ │     ├─ DiskMetricProvider                                    │
  │     ├─ PowerMetricProvider (RyzenAdj)                        │
  │     └─ NetworkMetricProvider                                 │
```

**变更原因**: 新增硬盘指标监控功能 (commit: 83fd63a)

---

### 5. Changelog 章节 - 版本更新 (第 477-489 行)

```diff
- ### 最新版本 v0.2.0 (2026-01-27)
+ ### 最新版本 v0.2.6 (2026-02-05)
+ 
+ - ✨ 新增硬盘指标监控（读写速度、使用率）
+ - ✨ 新增访问密钥认证功能
+ - ✨ 新增局域网访问控制和 IP 白名单
+ - ✨ 新增 API 端点集中化配置管理
+ - ✨ 完善关于页面技术栈说明
+ - ✨ Web 体验优化（指标顺序调整、标签图标和描述）
+ - ✨ 设置布局优化和面板透明度调整
+ - 🐛 修复设置页面相关问题
+ 
+ ### v0.2.0 (2026-01-27)
```

**变更原因**: 发布 0.2.6 版本 (commit: 0a7c2e5)

---

## 影响分析

### 用户可见变更
1. **功能特性**: 用户可以了解到新增的硬盘监控和安全认证功能
2. **配置选项**: 用户可以查看新增的安全配置项说明
3. **版本信息**: 用户可以看到最新版本的变更内容

### 文档质量
- ✅ 所有变更都有明确的来源（Git 提交）
- ✅ 变更内容与代码实现一致
- ✅ 保持了原有文档的格式和风格
- ✅ 没有破坏性的结构调整

### 向后兼容性
- ✅ 保留了所有原有内容
- ✅ 仅新增和更新，没有删除
- ✅ 配置项说明完整，包含默认值

---

## 下一步操作

### 选项 1: 应用变更
确认变更无误后，可以应用这些更新到 README.md 文件。

### 选项 2: 取消变更
如果发现问题，可以取消本次更新，恢复原始内容。

### 选项 3: 继续编辑
如果需要进一步调整，可以继续编辑文档内容。

---

**提示**: 变更已准备就绪，等待用户确认。
