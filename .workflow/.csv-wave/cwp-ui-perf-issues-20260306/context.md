# CSV Batch Execution Report

**Session**: cwp-ui-perf-issues-20260306
**Updated**: 2026-03-06 14:39:34
**Total Tasks**: 9 | **Completed**: 8 | **Failed**: 0 | **Skipped**: 1

## Wave Summary
### Wave 1
- [1] [P0] 收起态跳过全量进程集合刷新: completed (tests=True)
- [2] [P0] 悬浮窗进程列表虚拟化（避免一次性创建所有行）: completed (tests=True)

### Wave 2
- [3] [P1] 降低 llama 实时指标导致的额外全量快照推送: completed (tests=True)
- [4] [P1] Web FloatingWidget：减少全量聚合/排序并使用稳定 key: completed (tests=True)
- [5] [P1] TaskbarMetricsViewModel：减少 RebuildColumns 与测量开销: completed (tests=True)

### Wave 3
- [6] [P2] 滚动条显隐：缓存 ScrollBar 并复用单个 DispatcherTimer: completed (tests=True)
- [7] [P2] 降低透明窗体 + 阴影效果的过绘成本: completed (tests=True)

### Wave 4
- [8] [P3] Web 主页面：去重 SignalR 连接（回收项）: skipped
- [9] [P3] metrics.latest：定位发送端或移除无效订阅（排查项）: completed (tests=True)

## Key Findings
- Desktop：Collapsed/Clickthrough 状态跳过 All/Top 全量刷新，仅更新 pinned。
- Desktop：详情进程列表改为 ListBox + VirtualizingStackPanel（Recycling）。
- Service：llama realtime 触发的全量快照推送增加节流窗口（按 Monitor:IntervalSeconds）。
- Web：FloatingWidget TopN 线性选择 + stable key + 摘要聚合限流（build 通过；lint 全局存在既有错误）。
- Desktop：TaskbarMetricsViewModel 增加布局输入快照与文本测量缓存，重复更新不再重建列。
- Desktop：滚动条显隐逻辑缓存 ScrollBar + 复用单 Timer。
- Desktop：阴影层拆分并下调参数，降低动态内容导致的离屏渲染。
- Desktop：移除 metrics.latest 死订阅与文档描述，统一 Receive* 数据通路。
