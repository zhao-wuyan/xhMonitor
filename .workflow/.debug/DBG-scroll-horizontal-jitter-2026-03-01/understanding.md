# Understanding Document

**Session ID**: DBG-scroll-horizontal-jitter-2026-03-01  
**Bug Description**: 滚动条策略导致的横向抖动问题（web 端）  
**Started**: 2026-03-01T08:00:00+08:00

---

## Exploration Timeline

### Iteration 1 - Initial Exploration (2026-03-01 08:00)

#### 代码结构梳理

**自适应滚动策略核心文件：**
- `xhmonitor-web/src/hooks/useAdaptiveScroll.ts` — 滚动模式逻辑
- `xhmonitor-web/src/components/ProcessList.tsx` — 表格渲染，接受 `scrollMode` / `processTableMaxHeight`
- `xhmonitor-web/src/App.tsx` — 组装 hook + 组件，传递 `adaptiveScroll.mode`
- `xhmonitor-web/src/styles/responsive.css` — 所有滚动相关 CSS

**模式切换机制：**
```
page 模式：
  .xh-app-shell { overflow-y: auto }     → 页面整体可滚动
  .table-scroll { overflow-x: auto }     → 横向滚动
  .table-body-scroll { overflow-y: 默认 } → 不固定高度

process 模式：
  .xh-app-shell--process-scroll { overflow-y: hidden } → 锁住外层滚动
  .process-panel--scroll .table-scroll { overflow-x: auto; overflow-y: hidden }
  .process-panel--scroll .table-body-scroll {
    max-height: var(--xh-process-scroll-max-height);
    overflow-y: auto;
    overflow-x: hidden;
  }
```

**触发逻辑（useAdaptiveScroll.ts）：**
- `canEnterProcessMode` = isAtTop && tableShare >= 0.5
- `shouldExitProcessMode` = tableShare < 0.45 || availableHeight < 160px
- `processTableMaxHeight` = `viewportHeight - tableRect.top - 12`（针对 .table-scroll 的 getBoundingClientRect）

---

#### 横向抖动三大候选根因

---

##### 根因 H1（最可能）：垂直滚动条出现/消失导致布局宽度跳变

**机制：**
1. page 模式下，`.xh-app-shell` 有 `overflow-y: auto`
2. 若内容足够长（触发滚动条），Windows 原生滚动条宽度 ≈ 17px；自定义滚动条 6px（CSS 定义）
3. 切换到 process 模式 → `.xh-app-shell--process-scroll` 覆盖为 `overflow-y: hidden` → 滚动条消失
4. 内容区宽度增加 6~17px（取决于浏览器/OS）
5. `.app-container`（`max-width: 1400px; margin: 0 auto`）居中，左右 padding 各增加约一半
6. 表格整体向左偏移（因为容器居中展宽）→ **横向位移**
7. `ResizeObserver` 触发 → 重新计算 → 可能反复切换

**关键 CSS 问题点（responsive.css）：**
```css
.xh-app-shell {
  overflow-x: hidden;
  overflow-y: auto;          /* ← 可能出现/消失垂直滚动条 */
}
.xh-app-shell--process-scroll {
  overflow-y: hidden;        /* ← 切换时滚动条宽度变化 → 布局抖动 */
}
```

**严重程度：高。** `scrollbar-gutter` 未设置，是教科书级的 layout shift 问题。

---

##### 根因 H2（中可能）：processTableMaxHeight 1px 振荡导致 table-body-scroll 滚动条闪烁

**机制：**
```javascript
const processTableMaxHeight = Math.max(
  minProcessTableHeightPx,
  Math.floor(viewportHeight - tableRect.top - bottomPadding - 12)
);
```

- `tableRect.top` 来自 `getBoundingClientRect()`，可能因子像素渲染（subpixel）而在相邻帧中差 1px
- 若 `processTableMaxHeight` 在 N 帧到 N+1 帧之间差 1px，而此时 `.table-body-scroll` 的内容高度恰好在 max-height 边界附近
- 滚动条 6px 反复出现/消失 → `overflow-y: auto` → 6px 横向布局抖动
- 同时 `ResizeObserver` 不断触发新的 recompute

**关键代码：**
```javascript
// useAdaptiveScroll.ts L57-59
const processTableMaxHeight = tableRect
  ? Math.max(minProcessTableHeightPx, Math.floor(viewportHeight - tableRect.top - bottomPadding - 12))
  : 0;
```

**严重程度：中。** `Math.floor` 已有保护，但 `tableRect.top` 在模式切换时本身不稳定（H1 会使其变化）。

---

##### 根因 H3（补充）：thead/tbody 双表格列宽错位加剧视觉抖动

**机制：**
Process 模式下，`.table-body-scroll` 有垂直滚动条（6px）：
```
header table → 全宽（在 .table-scroll 内，无滚动条占位）
body table   → 全宽 - 6px（在 .table-body-scroll 内，有滚动条）
```

由于都是 `width: 100%; table-layout: fixed`，两个表格的实际宽度不同，首列宽度计算基准不同，导致 **列头与列体在水平方向错位**。当模式切换时，这个错位来回变化也被感知为抖动。

**关键 CSS：**
```css
.process-panel--scroll .table-body-scroll {
  overflow-x: hidden;   /* body 表格无法横向滚动 */
  overflow-y: auto;     /* 有滚动条时挤压宽度 */
}
```

---

#### 根因优先级总结

| 优先级 | 根因 | 触发条件 | 表现 |
|--------|------|----------|------|
| 🔴 H1 | 外层 overflow-y 切换 → scrollbar gutter 跳变 | 每次模式切换 | 整个页面内容横向位移 |
| 🟡 H2 | processTableMaxHeight 1px 振荡 | 进入 process 模式后 | table-body 滚动条闪烁 |
| 🟠 H3 | thead/tbody 列宽错位 | process 模式内容超长 | 表头列与数据列不对齐 |

---

## Current Consolidated Understanding

### What We Know

- 横向抖动的直接原因是**垂直滚动条宽度的突然变化引起布局重排**
- H1 是根本触发点（外层 shell 的 overflow-y 切换），H2/H3 是放大器
- 当前策略本身逻辑正确（滞后进入/退出），无需修改策略逻辑
- 修复应聚焦在 **阻止滚动条宽度变化引起的布局偏移**

### 修复方向（保留当前策略）

1. **主修复（CSS）**：在 `.xh-app-shell` 上添加 `scrollbar-gutter: stable`
   - 始终为滚动条预留空间，即使未显示，彻底解决 H1
   - 无需修改任何 JS 逻辑
   
2. **辅助修复（CSS）**：为 `.table-body-scroll` 的滚动条预留空间
   - 解决 H3 的列宽错位问题：header table 减去滚动条宽度，或 body 容器设 `scrollbar-gutter: stable`

3. **可选修复（JS）**：`processTableMaxHeight` 增加 debounce 或 stability check
   - 避免 H2 的 1px 振荡，防止频繁 `ResizeObserver` 触发

### Remaining Questions

- `scrollbar-gutter: stable` 在目标浏览器（特别是移动端 WebView）支持情况？
- `body { overflow: hidden }` 在 index.css 设置后，滚动条是否实际出现在 `.xh-app-shell` 上？需确认实际滚动容器。
