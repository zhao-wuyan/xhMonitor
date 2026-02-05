# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.6] - 2026-02-05

### Added
- **硬盘指标监控**
  - 新增 `DiskMetricProvider` 支持硬盘读写速度和使用率监控
  - 集成 LibreHardwareMonitor 硬盘传感器
  - Web 端新增 `DiskWidget` 组件显示硬盘指标
- **访问密钥认证**
  - 新增 `System.EnableAccessKey` 配置项启用访问密钥认证
  - 新增 `System.AccessKey` 配置项设置访问密钥
  - 新增 `System.IpWhitelist` 配置项设置 IP 白名单（支持 CIDR）
  - Web 端新增 `AccessKeyScreen` 访问密钥输入页面
  - 新增 `AuthContext` 管理认证状态
  - 新增 `apiFetch` 统一 API 请求处理（自动附加访问密钥）
- **局域网访问控制**
  - 新增 `System.EnableLanAccess` 配置项启用局域网访问
  - 新增 `FirewallManager` 管理 Windows 防火墙规则
  - 新增 `IpWhitelistMatcher` 支持 IP 白名单匹配（CIDR 格式）
  - Desktop 应用新增局域网访问设置界面
- **API 端点集中化配置**
  - 新增 `endpoints.ts` 集中管理 API 端点配置
  - 统一 SignalR Hub URL 和 REST API Base URL
  - 支持环境变量配置端点地址

### Changed
- **Web 体验优化**
  - 调整指标卡片顺序（CPU → RAM → GPU → VRAM → NET → PWR → DISK）
  - 优化指标标签图标和描述文本
  - 优化设置面板布局和交互体验
  - 调整面板透明度和毛玻璃效果
- **关于页面完善**
  - 完善技术栈说明（Desktop、Service、Web）
  - 添加版本信息显示
- **配置管理优化**
  - 统一配置项命名规范（`System.*`、`DataCollection.*`）
  - 完善配置项默认值和验证逻辑

### Fixed
- **设置页面问题修复**
  - 修复设置保存失败问题
  - 修复配置项加载异常
  - 修复 UI 交互响应问题
- **Web 端问题修复**
  - 修复版本号显示不一致
  - 修复 TypeScript 编译错误
  - 修复拖拽排序动画问题

### Technical Details
- **架构改进**
  - 引入 `AuthProvider` 统一认证状态管理
  - 引入 `apiFetch` 统一 API 请求处理
  - 引入 `endpoints.ts` 集中化端点配置
  - 引入 `IpWhitelistMatcher` 支持 CIDR 格式 IP 白名单
- **安全增强**
  - SignalR 连接支持访问密钥认证
  - REST API 请求支持访问密钥认证
  - 支持 IP 白名单限制访问来源
  - 支持 Windows 防火墙规则管理
- **测试覆盖**
  - 新增 `IpWhitelistMatcherTests` 单元测试
  - 新增 `SettingsViewModelTests` 单元测试

## [0.2.0] - 2026-01-27

### Added
- **进程排序优化**
  - 优化进程列表排序逻辑
- **单实例模式与设备验证**
  - 启动脚本只启动 Desktop 应用
  - 添加设备验证功能，非星核设备无法切换功耗模式
- **点击动画**
  - 悬浮窗指标点击添加视觉反馈动画
- **管理员状态指示器**
  - 显示当前是否以管理员权限运行
- **设置页改版**
  - 新增监控开关
  - 新增开机自启选项
  - 新增管理员模式选项
- **功耗监控**
  - 集成 RyzenAdj 功耗监控
  - 添加 RyzenAdj 打包逻辑
- **网络监控**
  - 添加网络速度监控
  - 悬浮窗显示网络/内存使用情况
  - Health 探测自动解析后端端口
- **LibreHardwareMonitor 混合架构集成**
  - 新增 `ILibreHardwareManager` 接口和 `LibreHardwareManager` 实现类
  - 新增 `LibreHardwareMonitorCpuProvider` - 系统级 CPU 指标使用 LibreHardwareMonitor
  - 新增 `LibreHardwareMonitorMemoryProvider` - 系统级内存指标使用 LibreHardwareMonitor
  - 新增 `LibreHardwareMonitorGpuProvider` - 系统级 GPU 指标使用 LibreHardwareMonitor
  - 新增 `LibreHardwareMonitorVramProvider` - 系统级 VRAM 指标使用 LibreHardwareMonitor
  - 新增配置项 `MetricProviders:PreferLibreHardwareMonitor` 控制是否启用混合架构
  - 新增自动回退机制：无管理员权限时自动回退到 PerformanceCounter
  - 新增 Computer 实例单例管理，避免重复初始化
  - 新增传感器缓存机制（1秒生命周期），减少硬件轮询频率
  - 新增线程安全保护（SemaphoreSlim），支持多线程并发访问
  - 新增完整的集成测试套件（10+ 测试用例）
  - 新增单元测试覆盖（LibreHardwareManager、各混合架构提供者）
  - 更新 README.md 添加系统要求、混合架构说明和配置文档
  - 更新 appsettings.json 添加配置项注释

### Changed
- **混合架构设计**
  - 系统级指标（`GetSystemTotalAsync()`）使用 LibreHardwareMonitor（需管理员权限）
  - 进程级指标（`CollectAsync(processId)`）保持 PerformanceCounter 实现（无需管理员权限）
  - 提供者注册逻辑更新，支持动态选择 LibreHardwareMonitor 或 PerformanceCounter 提供者
- **Pinned 卡片宽度优化**
  - 调整悬浮窗置顶卡片宽度
- **设置优化**
  - 优化设置页面交互体验
- **依赖更新**
  - 添加 `LibreHardwareMonitorLib` 0.9.4 依赖
  - XhMonitor.Core 项目启用 `AllowUnsafeBlocks`（LibreHardwareMonitor 要求）

### Fixed
- **处理警告**
  - 修复编译警告
- **悬浮窗置顶卡片宽度**
  - 调整悬浮窗置顶卡片宽度显示问题
- **启动脚本**
  - 修复启动脚本问题
- **Web 端显存和内存占用**
  - 修复 Web 端显存和内存占用显示问题
- **进程卡片随悬浮窗移动**
  - 修复进程详情卡片未随悬浮窗拖动而移动的问题

### Technical Details
- **架构优势**
  - 系统级指标精度更高（直接读取硬件传感器）
  - 进程级指标功能完整（委托给现有 PerformanceCounter 实现）
  - 无缝回退机制（无管理员权限时自动降级）
  - 单例 Computer 实例（避免资源浪费）
  - 传感器缓存（减少硬件轮询，提升性能）
  - 线程安全（支持多线程并发访问）
- **测试覆盖**
  - 集成测试：提供者注册、启动检测、回退机制、多线程安全、进程级监控回归
  - 单元测试：LibreHardwareManager 初始化、传感器读取、缓存机制、线程安全、资源释放
  - 提供者测试：IsSupported()、GetSystemTotalAsync()、CollectAsync()、错误处理

## [0.1.0] - 2025-12-20

### Added
- **核心监控功能**
  - 实现核心监控架构
  - 支持 CPU、内存、GPU、显存监控
  - 基于关键词的进程过滤
- **数据持久化**
  - 实现 Repository 模式
  - 集成 EF Core 和 SQLite
  - 实现数据聚合功能（分钟/小时/天）
  - 新增 AggregationWorker 后台服务
- **Web API 与实时通信**
  - 新增 Web API 和 SignalR 支持
  - 实现 REST API 查询接口
  - 实现实时数据推送
- **Web 前端**
  - 完成 Web 前端开发（React 19 + TypeScript）
  - 实现实时数据展示和 SignalR 连接
  - 添加进程列表、搜索和排序功能
  - 集成 ECharts 动态图表
  - 实现国际化支持（中英文切换）
  - 采用 Glassmorphism 毛玻璃 UI 设计
  - 支持动态指标扩展（零前端代码修改）
  - 添加前端国际化文档（I18N.md）

### Fixed
- 修复 CpuMetricProvider 线程安全问题
- 修复嵌套并行导致的死锁

### Changed
- 优化 GetInstanceName 为 O(1) 查找

### Documentation
- 记录已知限制文档
