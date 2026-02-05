# README 更新计划

## 分析结果

基于 Git 历史分析，README.md 自 2026-01-27 最后修改后，有以下重要变更需要同步到文档：

### 1. 新增功能（需要更新 Features 章节）

#### 硬盘监控功能 (commit: 83fd63a)
- **变更**: 接入硬盘指标监控
- **影响**: Features 章节的"多维度监控"需要添加硬盘监控
- **当前**: `CPU、内存、GPU、显存、功耗、网络速度实时监控`
- **建议**: `CPU、内存、GPU、显存、硬盘、功耗、网络速度实时监控`

#### 访问密钥认证功能 (commit: efdca1b)
- **变更**: 添加访问密钥认证和统一 API 请求处理
- **影响**: Features 章节需要新增安全认证特性
- **建议新增**: `✅ **安全认证** - 访问密钥认证、IP 白名单、局域网访问控制`

### 2. 配置项更新（需要更新 Configuration 章节）

#### 新增配置项
基于 ConfigurationDefaults.cs 和相关提交，需要添加以下配置项：

**appsettings.json 新增配置**：
- 无新增（硬盘监控使用现有 LibreHardwareMonitor 配置）

**数据库设置（ApplicationSettings）新增配置**：
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `System.EnableLanAccess` | bool | false | 是否启用局域网访问 |
| `System.EnableAccessKey` | bool | false | 是否启用访问密钥认证 |
| `System.AccessKey` | string | "" | 访问密钥（空表示未设置） |
| `System.IpWhitelist` | string | "" | IP 白名单（逗号分隔的 IP 地址或 CIDR） |

### 3. 架构图更新（需要更新 Architecture 章节）

#### MetricProviders 列表
- **当前**: CpuMetricProvider, MemoryMetricProvider, GpuMetricProvider, VramMetricProvider, PowerMetricProvider, NetworkMetricProvider
- **建议新增**: `DiskMetricProvider` (或 SystemMetricProvider 包含硬盘监控)

### 4. Changelog 更新

#### 最新版本信息
- **当前**: `v0.2.0 (2026-01-27)`
- **需要更新为**: `v0.2.6 (2026-02-05)`

#### v0.2.6 变更内容
基于提交历史，建议添加：
- ✨ 新增硬盘指标监控（读写速度、使用率）
- ✨ 新增访问密钥认证功能
- ✨ 新增局域网访问控制和 IP 白名单
- ✨ 新增 API 端点集中化配置管理
- ✨ 完善关于页面技术栈说明
- 🐛 修复设置页面相关问题

## 更新策略

### 保持原有结构
README 当前结构完整且符合开源项目标准，建议：
- ✅ 保持现有章节顺序
- ✅ 保持现有格式风格
- ✅ 仅更新过时内容，不重组结构

### 更新优先级
1. **P0 - 必须更新**:
   - Features 章节（新增硬盘监控、安全认证）
   - Configuration 章节（新增配置项）
   - Changelog 章节（更新版本信息）

2. **P1 - 建议更新**:
   - Architecture 章节（架构图添加 DiskMetricProvider）

3. **P2 - 可选更新**:
   - 无

## 实施计划

1. 更新 Features 章节第 11 行
2. 在 Features 章节添加新的安全认证特性（第 21 行后）
3. 更新 Configuration 章节的数据库设置表格（第 182-191 行）
4. 更新 Architecture 章节的 MetricProviders 列表（第 263-270 行）
5. 更新 Changelog 章节的最新版本信息（第 475-486 行）
