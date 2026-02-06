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

---

### Round 2 - User Feedback & Refinement (2026-02-06 10:04 UTC+8)

#### User Input

- 有时曲线会出现“没有任何标注”的情况，需要保证 **有曲线时，可视区内至少 1 个标注**。
- 设置中需要新增一个 **滑块开关**，允许用户关闭该标注。

#### Changes Applied

1. **最少 1 个标注兜底**
   - 当筛选/裁剪后可视区内没有任何标注时，使用兜底策略选取一个点进行标注：
     - 优先：可视区内 prominence 最大的候选极值；
     - 否则：可视区内的 max/min（择其更偏离均值的一侧）。
2. **设置开关**
   - `LayoutState` 新增 `showPeakValleyMarkers`（默认开启，localStorage 持久化）。
   - 设置面板新增滑块开关，关闭后会清空并停止渲染峰谷标注。

#### Updated Artifacts

- `xhmonitor-web/components/charts/peakValley.js`：新增 `pickFallbackMarker()` 兜底逻辑。
- `xhmonitor-web/components/charts/MiniChart.js`：支持 `markersEnabled` 开关 + 兜底插入。
- `xhmonitor-web/src/hooks/useLayoutState.ts`：新增并持久化 `showPeakValleyMarkers`。
- `xhmonitor-web/src/components/SettingsDrawer.tsx`：新增滑块开关 UI。
- `xhmonitor-web/src/styles/responsive.css`：新增 `.settings-switch` 样式。
- `xhmonitor-web/src/App.tsx`、`xhmonitor-web/src/components/ChartCanvas.tsx`：将开关接入 MiniChart。

---

### Round 3 - User Feedback & Refinement (2026-02-06 10:16 UTC+8)

#### User Input

- “至少 1 个标注”需要更精确：当没有获取到有效值时不显示（例如 `null`/无效值，或 `0` 值）。

#### Changes Applied

1. **有效值定义**
   - 标注的有效值定义为：`Number.isFinite(value) && value > 0`。
2. **兜底条件收紧**
   - 当显示区（keepAfterXRatio 右侧）没有任何有效值时：清空并不显示任何标注。
   - 仅当显示区存在有效值且筛选后无标注时，才启用兜底 `pickFallbackMarker()` 选取 1 个标注。
3. **筛选逻辑同步**
   - `filterSignificantExtrema()` 会过滤掉 `0`/无效值，避免出现“谷值为 0”的无意义标注。

#### Updated Artifacts

- `xhmonitor-web/components/charts/peakValley.js`：显著性筛选与兜底均忽略 `0`/无效值。
- `xhmonitor-web/components/charts/MiniChart.js`：显示区无有效值时直接清空标注；兜底仅在存在有效值时触发。
- `xhmonitor-web/components/charts/peakValley.test.js`：新增“全 0 时兜底返回 null”的用例。

---

### Round 4 - UI Refinement (2026-02-06 10:41 UTC+8)

#### User Input

- 峰谷标注在设置中不是“标题”，应按普通设置项展示。
- 设置抽屉的背景透明度不应该随“面板透明度（glassOpacity）”变化，否则容易被背景干扰阅读。

#### Changes Applied

1. **设置项样式调整**
   - 将“峰谷标注”从标题样式改为普通 label，避免出现圆点/加粗的标题感。
2. **设置抽屉背景与面板透明度解耦**
   - 为设置抽屉增加独立背景变量 `--xh-color-settings-bg`，固定更不透明的背景，保证可读性。
   - `.settings-drawer` 不再使用 `--xh-color-glass-bg`（会受 `--xh-glass-opacity` 影响）。

#### Updated Artifacts

- `xhmonitor-web/src/components/SettingsDrawer.tsx`：峰谷标注设置项改为普通 label。
- `xhmonitor-web/components/core/design-tokens.css`：新增 `--xh-color-settings-bg`。
- `xhmonitor-web/src/styles/responsive.css`：`.settings-drawer` 使用独立背景变量。
