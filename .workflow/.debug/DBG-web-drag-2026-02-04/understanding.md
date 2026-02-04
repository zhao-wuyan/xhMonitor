# Understanding Document

**Session ID**: DBG-web-drag-2026-02-04  
**Bug Description**: web 端拖拽动画 bug：触发拖拽时会有残留卡片重叠；鼠标按下拖拽后卡片应被“抓住”跟随鼠标自由移动，不松手不应触发交换；交换只允许在上方规定区域内发生。  
**Started**: 2026-02-04T00:00:00+08:00

---

## Exploration Timeline

### Iteration 1 - Initial Exploration (2026-02-04)

#### Current Understanding

- 当前拖拽实现基于 SortableJS，入口组件为 `xhmonitor-web/src/components/DraggableGrid.tsx`，核心逻辑在 `xhmonitor-web/src/hooks/useSortable.ts`。
- 现状：`forceFallback: true` + `fallbackOnBody: true` 会产生“跟随鼠标的拖拽元素（drag）”，同时原列表元素会带 `sortable-chosen`，如果样式不隐藏原元素，就会出现“拖拽时有两张卡片”的重叠观感。
- 现状：默认 Sortable 行为会在拖拽过程中动态调整 DOM 顺序（sort），在 React 场景下可能导致拖拽中途出现视觉闪烁/残影（尤其当组件存在高频刷新）。
- 现状：`cleanup()` 只在 container 内移除 class/style，Sortable fallback 产生的拖拽元素可能挂在 `document.body` 上，容易造成“残留拖拽卡片”。

#### Evidence from Code Search

- `xhmonitor-web/src/hooks/useSortable.ts`
  - 使用 `forceFallback: true`、`fallbackOnBody: true`。
  - `cleanup()` 仅对 container 内 `.sortable-ghost/.sortable-chosen/.sortable-drag` 做清理。
- `xhmonitor-web/src/styles/responsive.css`
  - 存在 `.sortable-chosen { opacity: 0.85; }`，在 fallback 场景下更容易看到“原卡片 + 拖拽卡片”重叠。

#### Hypotheses Generated

1. **H1（视觉重叠）**：fallback 模式下 `.sortable-chosen` 对应原卡片仍可见，导致拖拽时出现“残留/重叠卡片”观感。  
   - 验证点：拖拽开始后同时出现 `.sortable-drag`（跟随鼠标）和 `.sortable-chosen`（原位置）两者都可见。
2. **H2（残影残留）**：Sortable fallback 产生的拖拽元素挂在 `document.body`，现有 `cleanup()` 只清理 container 内元素，导致拖拽结束偶发残留。  
   - 验证点：拖拽结束后 DOM 仍存在 `.sortable-drag` 且不在 container 内。
3. **H3（交换时机）**：拖拽过程中 Sortable 动态排序导致“没松手就交换”，与期望“松手后才交换”不一致。  
   - 验证点：拖拽移动过程中，非拖拽卡片立即发生位置变化。

#### Fix Attempt (待验证)

- 改为 **commit-on-drop**：`sort: false`，拖拽过程中不改变 DOM 顺序；在 `onEnd` 根据鼠标落点计算目标 index 并一次性更新 `cardOrder`。
- 增强清理：额外清理 `document` 上不在 container 内的 `.sortable-*` 元素。
- 调整样式：在 `body.is-sorting` 期间将 `.sortable-chosen` 强制隐藏，避免拖拽时看到“原卡片 + 拖拽卡片”。

### Iteration 2 - Bug Report & Correction (2026-02-04)

#### User-Observed Behavior

- 拖拽开始后，原位置元素消失（符合预期），但“跟随鼠标的拖拽卡片”也消失。
- 左键长按未松手时，偶发拖拽中途结束（脱离拖拽）。

#### Corrected Understanding

- ~~隐藏 `.sortable-chosen` 足够安全~~ → fallback 场景下，拖拽元素可能同时带有 `sortable-chosen` + `sortable-drag`，`opacity: 0 !important` 会把拖拽元素也隐藏。
- ~~清理 body 上 `.sortable-*` 只会发生在拖拽结束后~~ → `onUnchoose` 可能在拖拽进行中触发，如果 cleanup 删除了 body 上的拖拽节点，会造成“长按中途脱离拖拽”。

#### Fix Applied (Iteration 2)

- CSS：仅隐藏 `body.is-sorting .sortable-chosen:not(.sortable-drag)`，并将 `.sortable-drag` 的 `opacity` 提升为 `!important`。
- JS：`cleanup()` 增加 `removeOrphans` 开关；`onUnchoose` 只清理容器内 class，不删除 body 上拖拽节点；`onEnd/onCancel/unmount` 才删除 orphan 节点并移除 `body.is-sorting`。

### Iteration 3 - Evidence Analysis (2026-02-04)

#### Log Analysis Results

从 `DBG-web-drag-2026-02-04-debug.log`（已复制到 `.workflow/.debug/DBG-web-drag-2026-02-04/debug.log`）统计：

- `H1 Drag started`：出现 5 次（cpu/gpu/vram/net/pwr）。
- `H3 Drag ended (commit on drop)`：仅出现 1 次（net）。
- `H2 Cleanup sortable DOM artifacts`：大量出现，且绝大多数 `isDragging=false`、`removeOrphans=true`（说明在“非拖拽结束”的时段也在反复触发清理）。

#### Corrected Understanding

- ~~拖拽中途脱离主要是 DOM orphan 清理误删~~ → 更可能是 Sortable 实例在拖拽过程中被销毁/重建（effect cleanup 触发），导致拖拽被强制结束，因此出现“长按未松手但拖拽脱离”，并且没有对应的 `onEnd` 日志。

#### Root Cause Identified

`xhmonitor-web/src/hooks/useSortable.ts` 的初始化 `useEffect` 依赖 `onOrderChange`，而上层 `DraggableGrid` 每次 render 都会创建新的 `(order) => updateLayout(...)` 函数，导致 Sortable 被频繁 destroy/recreate；一旦在拖拽过程中发生，就会打断拖拽。

#### Fix Applied (Iteration 3)

- 通过 `useRef` 固定 `onOrderChange`，并将其移出 Sortable 初始化 effect 的依赖，避免重渲染时销毁 Sortable。

#### Repro & Debug Log（可选）

如仍能复现，可开启 Sortable 调试 NDJSON（默认关闭）：

1. 打开浏览器 DevTools，在 Console 执行：`localStorage.setItem('xh.debug.sortable', '1')`，然后刷新页面。
2. 复现拖拽问题后，在 Console 执行：`window.__xhExportSortableDebug?.()`，会下载 `DBG-web-drag-2026-02-04-debug.log`。
3. 将下载文件移动覆盖到：`.workflow/.debug/DBG-web-drag-2026-02-04/debug.log`，然后把该文件内容发我用于分析（Analyze mode）。

---

## Current Consolidated Understanding

### What We Know
- 该问题与 SortableJS fallback 行为（clone/drag + chosen）和清理范围（仅 container）强相关。
- 需求“松手才交换”与 Sortable 默认“拖拽中即时排序”冲突，需要改为 drop 时提交顺序。

### Current Investigation Focus
- 验证修复后：拖拽过程中不再即时交换；结束后无残留拖拽卡片；视觉不再重叠。

### Remaining Questions
- “上方规定区域”具体定义是否仅为 `.stats-grid` 区域；若需更严格的区域（如仅上方 N px），需要明确阈值。
