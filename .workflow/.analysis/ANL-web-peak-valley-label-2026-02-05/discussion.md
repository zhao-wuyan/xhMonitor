# Analysis Discussion

**Session ID**: ANL-web-peak-valley-label-2026-02-05
**Topic**: web 端监控卡片曲线峰谷值标注重叠、截断，优化峰谷标注算法与布局
**Started**: 2026-02-05T22:45:04Z（UTC+8 时间写入 ISO 形式）
**Dimensions**: implementation, design

---

## User Context

**Focus Areas**: 峰谷点筛选（代表性、去噪、去重）、标注布局（避免重叠、避免被裁剪）、视觉一致性（卡片小图表）
**Analysis Depth**: deep

---

## Discussion Timeline

### Round 1 - Initial Understanding (2026-02-05 22:45 UTC+8)

#### Topic Analysis

现状问题（基于描述 + 截图）：

- 峰谷标注数量过多，尤其相邻且值相近的局部极值被全部标注，导致标注相互遮挡，视觉杂乱。
- 低谷值标注出现被裁剪（只显示一半），说明标注布局/画布边界/容器裁剪存在问题。
- 目前的“峰/谷”不够具有代表性，缺少“显著性（prominence）/最小间距（distance）/聚类合并（clustering）”等约束。

#### Key questions to explore

1. 当前前端使用的图表库是什么（ECharts / G2Plot / Chart.js / 自研 canvas）？峰谷是用 `markPoint`/`annotation` 还是自绘？
2. 峰谷标注的产品期望：只要全局 max/min？还是每张卡片保留 Top N 个峰和谷（例如各 1～2 个）？
3. 峰谷筛选应以“时间间隔”还是“像素间距”为约束（更适合解决重叠）？
4. 裁剪问题来自图表 grid/padding 不足，还是容器 `overflow: hidden`，或 label positioning 逻辑固定在底部？

#### Next steps

- 在代码库中定位峰谷标注相关实现与样式。
- 分析当前算法的“极值判定 + 去重/合并 + label layout”链路。
- 设计并实现更稳定的峰谷筛选（显著性 + 最小间距 + 限制数量）与自动避让/防裁剪布局。

---

## Current Understanding

目标是让小尺寸监控卡片的曲线标注“更少、更准、更不挡、不被裁剪”：

- **更少**：限制标注数量（例如峰/谷各 1～2 个），并合并相邻近似值。
- **更准**：用显著性（prominence）过滤掉噪声型局部极值，让峰谷更“代表性”。
- **更不挡**：按像素距离做最小间距，必要时做 label shift 或放弃次要标注。
- **不被裁剪**：给 label 预留上下边距，或动态翻转峰/谷标签位置，确保落在画布内。

#### Exploration Results (2026-02-05 23:25 UTC+8)

定位到的关键实现：

- 峰谷标注核心逻辑在 `xhmonitor-web/components/charts/MiniChart.js` 的 `updateMarkers()`。
- 标签样式在 `xhmonitor-web/components/core/stat-card.css` 的 `.xh-chart-peak-marker`；裁剪来自 `.xh-stat-card { overflow: hidden; }`。
- React 侧通过 `xhmonitor-web/src/components/ChartCanvas.tsx` 调用 MiniChart，并由 `useTimeSeries`（pushValue 左移窗口）驱动更新。

确认的根因：

1. **重叠**：旧逻辑无数量上限，也无基于像素/包围盒的避让，同类型仅做了很弱的去重（索引差 < 3），因此会堆叠很多相邻且值相近的标注。
2. **代表性不足**：旧逻辑使用固定阈值 + “新峰只删除更小峰”的累积策略，会保留一串下降/相近的峰（或谷），视觉上显得杂乱。
3. **裁剪**：谷值标签默认在点下方，而卡片容器 overflow hidden，谷值贴底时标签超出卡片被裁掉。

---

## Conclusions (2026-02-05 23:25 UTC+8)

### Summary

已完成峰谷标注算法与布局优化：

- 算法侧引入 **prominence + 噪声阈值**，只在右侧窗口插入“显著”的新峰谷，并在渲染前进行 **可视区裁剪 + 数量上限 + 最小像素间距** 控制。
- 布局侧对标签做 **水平夹取** 与 **贴边动态翻转**（谷值贴底翻到上方），并加入 **包围盒碰撞兜底 Drop**，保证不重叠且不被裁剪。

### Artifacts

- `xhmonitor-web/components/charts/peakValley.js`：纯函数峰谷检测/筛选/裁剪逻辑（可测）。
- `xhmonitor-web/components/charts/MiniChart.js`：更新峰谷插入策略与标签布局/防裁剪/防重叠。
- `xhmonitor-web/components/core/stat-card.css`：新增 `pos-top/pos-bottom` 位置类，支持动态翻转。
- `xhmonitor-web/components/charts/peakValley.test.js`：Node 内置 test runner 单元测试。
