# xhMonitor Web — src

## Purpose

本目录是 xhMonitor Web 前端应用的根源码目录，基于 React 18 + TypeScript + Vite 构建。主要目标是通过 SignalR 实时连接后端服务，可视化展示系统资源（CPU、内存、GPU、磁盘、网络、电量）与进程列表，并提供高度可定制化的布局与主题配置。

## Structure

```
src/
├── App.tsx                   # 根组件与路由分发（主页 / 浮动小窗 / 任务栏小窗）
├── main.tsx                  # 应用入口，根据 URL 路径渲染对应组件
├── types.ts                  # 全局 TypeScript 类型定义
├── utils.ts                  # 通用数据格式化工具函数
├── i18n.ts                   # 中英文双语国际化模块
├── version.ts                # 构建时注入的版本号
├── App.css / index.css       # 全局样式与 Tailwind 主题变量
├── global.d.ts               # 构建注入变量类型声明（__APP_VERSION__）
├── components/               # UI 组件（有独立 CLAUDE.md）
├── styles/                   # 响应式 CSS（有独立 CLAUDE.md）
├── hooks/                    # 自定义 React Hooks
├── contexts/                 # React Context 状态提供者
├── pages/                    # 页面级组件（访问密钥页、桌面小窗）
├── config/                   # 端点配置与访问密钥管理
├── utils/                    # 专项工具模块（背景图存储等）
└── assets/                   # 静态资源
```

## Key Files

### 核心入口

- **`main.tsx`**: 应用挂载入口。根据 `window.location.pathname` 决定渲染 `App`（`/`）、`FloatingWidget`（`/widget/floating`）或 `TaskbarWidget`（`/widget/taskbar`）。

- **`App.tsx`**: 主应用组件。包含 `AppShell`（监控主界面）和 `AppContent`（鉴权分发）。组合了所有 Provider 与核心组件，实现自适应滚动模式（`useAdaptiveScroll`）。

### 类型系统

- **`types.ts`**: 全局数据类型定义，包含：
  - `SystemUsage` — CPU / GPU / 内存 / VRAM / 磁盘 / 网络 / 电量数据
  - `MetricsData` / `ProcessInfo` — 进程指标数据
  - `MetricConfig` / `MetricMetadata` — 指标元信息与颜色映射
  - `DiskUsage`、`AlertConfig`、`HealthStatus`、`ChartDataPoint`

### 工具与国际化

- **`utils.ts`**: 格式化函数库：
  - `formatMegabytesParts` / `formatMegabytesLabel` — MB 转人类可读单位（带分段 value/unit）
  - `formatNetworkRateParts` / `formatNetworkRateLabel` — 网速格式化（KB/s、MB/s、GB/s）
  - `formatBytes`、`formatPercent`、`formatTimestamp`
  - `calculateSystemSummary` — 聚合进程与系统指标为汇总数据

- **`i18n.ts`**: 静态中英文文本映射。`t(key)` 为翻译函数，`setLocale` / `getLocale` 切换当前语言，默认 `zh`。

- **`version.ts`**: 从 Vite 构建时注入的 `__APP_VERSION__` 变量导出版本标签 `APP_VERSION_TAG`。

## Hooks

| 文件 | 用途 |
|------|------|
| `useMetricsHub.ts` | 通过 SignalR 连接后端 `/hubs/metrics`，接收 `ReceiveProcessMetrics`、`ReceiveSystemUsage`、`ReceiveProcessMetadata` 推送，管理连接状态与重连逻辑，处理 401 鉴权事件 |
| `useLayoutState.ts` | 布局状态的读写与持久化（localStorage），包括网格列数、卡片顺序、可见性、背景图、主题色。背景图大文件存 IndexedDB，避免 localStorage 超限 |
| `useMetricConfig.ts` | 从 API 加载指标元数据与颜色映射（`MetricConfig`） |
| `useAdaptiveScroll.ts` | 自适应滚动模式：根据页面高度动态决定整页滚动或进程面板内部滚动 |
| `useTimeSeries.ts` | 时间序列数据环形缓冲区管理，用于图表数据窗口控制 |
| `useTheme.ts` | 主题相关逻辑 |
| `useSortable.ts` | 拖拽排序实现（用于 `DraggableGrid`） |
| `useWidgetConfig.ts` | 小窗组件配置管理 |

## Contexts

| 文件 | 提供值 |
|------|--------|
| `AuthContext.tsx` | `requiresAccessKey`（是否需要鉴权）、`authEpoch`（用于重挂载 App）、监听 `xh-auth-required` 全局事件 |
| `LayoutContext.tsx` | 封装 `useLayoutState`，提供 `layoutState`、`updateLayout`、`resetLayout` |
| `TimeSeriesContext.tsx` | 时间序列数据的全局分发，支持多指标（cpu/ram/gpu/vram/net/pwr） |

## Config

- **`config/endpoints.ts`**: API 端点配置。开发环境默认连接 `localhost:35179`，生产构建使用同源相对路径（通过桌面端 35180 反代转发），支持 `VITE_API_BASE_URL` / `VITE_METRICS_HUB_URL` 环境变量覆盖。
- **`config/accessKey.ts`**: 访问密钥的读写与变更订阅（`getAccessKey` / `setAccessKey` / `onAccessKeyChanged`）。

## Pages

- **`pages/AccessKeyScreen.tsx`**: 访问密钥输入页。`AuthContext` 检测到 401 时展示，输入密钥后触发重连（通过 `authEpoch` 重挂载 `AppShell`）。
- **`pages/DesktopWidget/`**: 浮动小窗（`FloatingWidget`）与任务栏小窗（`TaskbarWidget`）入口组件，供 `main.tsx` 按路径分发。

## Utils

- **`utils/backgroundImageStore.ts`**: 使用 IndexedDB 存储大背景图 Blob，避免 localStorage 配额问题。提供 `loadBackgroundImageBlob` / `clearBackgroundImage`。

## Dependencies

- **`@microsoft/signalr`**: WebSocket 实时通信，连接后端监控数据推送中枢
- **`react` / `react-dom`**: UI 渲染框架
- **`lucide-react`**: 图标库（`Activity`, `Settings` 等）
- 构建工具: Vite，支持 `import.meta.env` 环境变量

## Architecture

应用采用标准 React Provider 层叠模式：

```
AuthProvider
  └── LayoutProvider
        └── TimeSeriesProvider
              └── AppContent
                    ├── AccessKeyScreen（需鉴权时）
                    └── AppShell（监控主界面）
                          ├── MobileNav
                          ├── Header（品牌、DiskWidget、状态）
                          ├── DraggableGrid（卡片排列）
                          │     └── StatCard + ChartCanvas（每个指标）
                          ├── DiskWidget（移动端堆叠位置）
                          ├── ProcessList
                          └── SettingsDrawer
```

## Integration Guidelines

- **所有用户可见文本**必须通过 `t(key)` 进行国际化包装
- **SignalR 鉴权**：连接时通过 `accessTokenFactory` 注入访问密钥；401 响应触发 `xh-auth-required` 事件，由 `AuthContext` 统一处理
- **端点引用**：不得硬编码端口号，统一从 `config/endpoints.ts` 导入 `API_V1_BASE` / `METRICS_HUB_URL`
- **布局状态变更**：通过 `updateLayout(patch)` 或 `updateLayout(prev => newState)` 修改，勿直接操作 localStorage
- **子模块说明**：`components/` 和 `styles/` 目录有各自的 CLAUDE.md，详见对应文件
