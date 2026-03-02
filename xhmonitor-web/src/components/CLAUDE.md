# xhMonitor Web Components

## Purpose
该目录包含 xhMonitor Web 前端的核心 UI 组件。主要目标是提供可重用、响应式且高性能的监控数据可视化组件，支持实时系统指标展示、进程管理、拖拽排序以及自适应布局。

## Structure
目录组件按功能可以分为以下几类：
1. **数据展示组件**: `StatCard`, `SystemSummary`, `ProcessList`, `DiskWidget`
2. **图表组件**: `MetricChart`, `ChartCanvas`
3. **布局与交互组件**: `DraggableGrid`, `MobileNav`, `SettingsDrawer`

## Components
- **`StatCard.tsx`**: 基础统计卡片组件，用于展示单一核心指标（如 CPU、内存使用率等）。支持自定义强调色、趋势显示和拖拽手柄。通过 `memo` 进行了防抖重绘优化。
- **`SystemSummary.tsx`**: 顶部系统概览组件，结合 `lucide-react` 图标展示系统核心的聚合指标。
- **`MetricChart.tsx`**: 基于 ECharts 封装的折线图组件，用于渲染时间序列数据。针对实时监控禁用了入场动画，优化了数据增量更新的平滑过渡效果。
- **`ProcessList.tsx`**: 进程列表数据表格。支持按任意指标（CPU、内存、GPU、VRAM 等）排序、模糊搜索过滤以及资源使用率进度条可视化。包含对大量数据的滚动渲染支持。
- **`DraggableGrid.tsx`**: 响应式拖拽网格容器。结合 `useSortable` 钩子与 `LayoutContext` 实现各监控卡片的自定义拖拽排序及用户布局状态持久化。
- **`MobileNav.tsx`**: 移动端专属的底部导航栏组件。
- **`SettingsDrawer.tsx`**: 侧边栏设置抽屉，用于管理应用偏好配置。
- **`DiskWidget.tsx`**: 磁盘空间与读写指标的可视化组件。
- **`ChartCanvas.tsx`**: 图表画布包装器，管理图表的基础生命周期和尺寸自适应。

## Dependencies
- 第三方库:
  - `react`: UI 渲染核心。
  - `echarts`: 复杂数据图表可视化 (`MetricChart`)。
  - `lucide-react`: 界面标准图标 (`SystemSummary`)。
- 内部依赖: 
  - `../types`: 全局 TypeScript 类型定义 (`ProcessInfo`, `MetricMetadata` 等)。
  - `../utils`: 数据格式化工具 (如 `formatPercent`, `formatBytes`)。
  - `../i18n`: 全局国际化多语言支持。
  - `../contexts/LayoutContext`: 全局布局状态管理。
  - `../hooks/useSortable`: 拖拽排序逻辑。

## Integration
组件集成需遵循项目的架构规范：
- **国际化 (i18n)**: 所有对用户可见的文本必须使用 `t()` 函数进行包裹。
- **响应式上下文**: 布局和排序状态完全依赖 `LayoutContext` 下发，组件保持视图层的纯粹性。
- **主题与样式**: 通过 CSS 变量 (如 `--xh-card-accent`) 处理主题化，动态颜色通过内联 `style` 传递给底层 DOM，并辅以 `.xh-glass-panel` 等全局样式类保持视觉统一。

## Implementation Guidelines
- **性能优先**: 监控数据具有极高频的更新特征，因此严格要求使用 `useMemo`, `memo` 拦截无效渲染。图表实现中避免全量重绘，依靠 ECharts 的增量数据特性。
- **类型安全**: 必须声明完整的 Props 接口并复用 `../types` 中的全局类型定义。
- **错误降级**: 组件需具备在部分数据缺失（如 GPU 相关指标在无独显机器上为空）时，平滑缺省的兼容展现能力。