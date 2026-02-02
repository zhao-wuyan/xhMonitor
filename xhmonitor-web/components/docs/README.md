# XhMonitor 组件库文档

## 概述

XhMonitor 组件库是从 `ui-preview-v2.html` 提取的设计系统，提供可复用的 UI 组件和图表引擎。

**版本**: 1.0.0
**设计语言**: 玻璃拟态 (Glassmorphism)
**主题**: 暗色模式

---

## 快速开始

### 1. 引入样式

```html
<!-- 设计 Tokens -->
<link rel="stylesheet" href="components/core/design-tokens.css">

<!-- 核心组件 -->
<link rel="stylesheet" href="components/core/glass-panel.css">
<link rel="stylesheet" href="components/core/stat-card.css">
```

### 2. 引入脚本

```html
<!-- 图表引擎 -->
<script src="components/charts/MiniChart.js"></script>
<script src="components/charts/DynamicScaler.js"></script>
```

### 3. 使用组件

```html
<!-- 玻璃拟态面板 -->
<div class="xh-glass-panel xh-glass-panel--padded">
  <h2>内容标题</h2>
  <p>面板内容...</p>
</div>

<!-- 资源监控卡片 -->
<div class="xh-stat-card xh-glass-panel">
  <div class="xh-stat-card__glow" style="background: var(--xh-color-cpu)"></div>
  <div class="xh-stat-card__info">
    <div class="xh-stat-card__label">
      <span style="color: var(--xh-color-cpu)">●</span> CPU
    </div>
    <div class="xh-stat-card__value" id="cpu-value">0%</div>
    <div class="xh-stat-card__subtitle">i9-13900K</div>
  </div>
  <div class="xh-stat-card__chart" id="chart-area-cpu">
    <canvas id="chart-cpu" class="xh-stat-card__canvas"></canvas>
  </div>
</div>
```

---

## 设计 Tokens

### 颜色系统

#### 基础色

| Token | 值 | 用途 |
|-------|-----|------|
| `--xh-color-bg` | #0f172a | 背景色 |
| `--xh-color-text-primary` | #f8fafc | 主要文本 |
| `--xh-color-text-secondary` | #94a3b8 | 次要文本 |

#### 玻璃拟态

| Token | 值 | 用途 |
|-------|-----|------|
| `--xh-color-glass-bg` | rgba(30, 41, 59, 0.6) | 玻璃背景 |
| `--xh-color-glass-border` | rgba(255, 255, 255, 0.08) | 玻璃边框 |
| `--xh-color-glass-highlight` | rgba(255, 255, 255, 0.05) | 玻璃高光 |

#### 语义色（监控指标）

| Token | 值 | 颜色 | 用途 |
|-------|-----|------|------|
| `--xh-color-cpu` | #3b82f6 | 🔵 蓝色 | CPU 使用率 |
| `--xh-color-ram` | #8b5cf6 | 🟣 紫色 | RAM 使用量 |
| `--xh-color-gpu` | #10b981 | 🟢 绿色 | GPU 使用率 |
| `--xh-color-vram` | #f59e0b | 🟠 橙色 | VRAM 使用量 |
| `--xh-color-net` | #0ea5e9 | 🔷 天蓝 | 网络流量 |
| `--xh-color-pwr` | #f43f5e | 🔴 玫红 | 功耗 |

### 排版系统

#### 字体族

```css
--xh-font-sans: 'Segoe UI', system-ui, -apple-system, sans-serif;
--xh-font-mono: 'Consolas', 'Monaco', 'Courier New', monospace;
```

#### 字号

| Token | 值 | 像素 | 用途 |
|-------|-----|------|------|
| `--xh-font-size-xs` | 0.65rem | 10.4px | 极小文本 |
| `--xh-font-size-sm` | 0.75rem | 12px | 小文本 |
| `--xh-font-size-base` | 0.85rem | 13.6px | 基础文本 |
| `--xh-font-size-md` | 0.9rem | 14.4px | 中等文本 |
| `--xh-font-size-lg` | 1.25rem | 20px | 大文本 |
| `--xh-font-size-xl` | 1.8rem | 28.8px | 超大文本 |

### 间距系统

| Token | 值 | 用途 |
|-------|-----|------|
| `--xh-spacing-xs` | 2px | 极小间距 |
| `--xh-spacing-sm` | 4px | 小间距 |
| `--xh-spacing-md` | 6px | 中间距 |
| `--xh-spacing-base` | 10px | 基础间距 |
| `--xh-spacing-lg` | 12px | 大间距 |
| `--xh-spacing-xl` | 16px | 超大间距 |
| `--xh-spacing-2xl` | 20px | 极大间距 |

### 动画系统

#### Duration（持续时间）

| Token | 值 | 用途 |
|-------|-----|------|
| `--xh-duration-instant` | 0ms | 瞬间 |
| `--xh-duration-fast` | 200ms | 快速 |
| `--xh-duration-normal` | 300ms | 正常 |
| `--xh-duration-slow` | 500ms | 缓慢 |
| `--xh-duration-slower` | 1000ms | 更慢 |
| `--xh-duration-pulse` | 2000ms | 脉冲 |

#### Easing（缓动函数）

```css
--xh-ease-linear: linear;
--xh-ease: ease;
--xh-ease-in: ease-in;
--xh-ease-out: ease-out;
--xh-ease-in-out: ease-in-out;
```

---

## 核心组件

### GlassPanel - 玻璃拟态面板

半透明背景和毛玻璃效果的容器组件。

#### 基础用法

```html
<div class="xh-glass-panel">
  内容...
</div>
```

#### 变体

```html
<!-- 带内边距 -->
<div class="xh-glass-panel xh-glass-panel--padded">
  内容...
</div>

<!-- 紧凑内边距 -->
<div class="xh-glass-panel xh-glass-panel--compact">
  内容...
</div>

<!-- 无边框 -->
<div class="xh-glass-panel xh-glass-panel--borderless">
  内容...
</div>

<!-- 高亮边框 -->
<div class="xh-glass-panel xh-glass-panel--highlight">
  内容...
</div>
```

#### CSS 类

| 类名 | 描述 |
|------|------|
| `.xh-glass-panel` | 基础玻璃面板 |
| `.xh-glass-panel--padded` | 带内边距（20px） |
| `.xh-glass-panel--compact` | 紧凑内边距（12px） |
| `.xh-glass-panel--borderless` | 无边框 |
| `.xh-glass-panel--highlight` | 高亮边框 |

---

### StatCard - 资源监控卡片

用于显示系统资源使用情况，信息叠加在图表上。

#### 基础用法

```html
<div class="xh-stat-card xh-glass-panel" style="--accent: var(--xh-color-cpu)">
  <!-- 装饰光晕 -->
  <div class="xh-stat-card__glow" style="background: var(--accent)"></div>

  <!-- 信息区域 -->
  <div class="xh-stat-card__info">
    <div class="xh-stat-card__label">
      <span style="color: var(--accent)">●</span> CPU
      <span class="xh-stat-card__label-indicator">· 45°C</span>
    </div>
    <div class="xh-stat-card__value" id="cpu-value">0%</div>
    <div class="xh-stat-card__subtitle">i9-13900K</div>
  </div>

  <!-- 图表区域 -->
  <div class="xh-stat-card__chart" id="chart-area-cpu">
    <canvas id="chart-cpu" class="xh-stat-card__canvas"></canvas>
  </div>
</div>
```

#### JavaScript 初始化

```javascript
// 创建图表实例
const cpuChart = new MiniChart(
  'chart-cpu',           // Canvas ID
  'chart-area-cpu',      // 容器 ID
  '#3b82f6',             // 颜色
  v => v.toFixed(0) + '%' // 格式化函数
);

// 模拟数据
const cpuData = new Array(40).fill(0);

// 更新循环
setInterval(() => {
  // 添加新数据
  cpuData.shift();
  cpuData.push(Math.random() * 100);

  // 绘制图表
  cpuChart.draw(cpuData, 100);

  // 更新数值
  document.getElementById('cpu-value').innerText =
    cpuData[cpuData.length - 1].toFixed(1) + '%';
}, 1000);
```

#### CSS 类

| 类名 | 描述 |
|------|------|
| `.xh-stat-card` | 基础卡片容器 |
| `.xh-stat-card__info` | 信息区域（左侧） |
| `.xh-stat-card__label` | 标签 |
| `.xh-stat-card__value` | 数值 |
| `.xh-stat-card__value--small` | 小号数值 |
| `.xh-stat-card__subtitle` | 副标题 |
| `.xh-stat-card__chart` | 图表区域（右侧） |
| `.xh-stat-card__canvas` | Canvas 画布 |
| `.xh-stat-card__glow` | 装饰光晕 |

---

## 图表组件

### MiniChart - 迷你图表引擎

实时数据可视化组件，支持左侧渐隐和动态峰谷标注。

#### 特性

- ✅ Canvas 2D 渲染
- ✅ 左侧 50% 渐隐效果
- ✅ 动态峰谷值标记
- ✅ 渐变填充
- ✅ 响应式画布
- ✅ 自定义格式化

#### 构造函数

```javascript
new MiniChart(canvasId, containerId, color, formatFn)
```

**参数**:
- `canvasId` (string): Canvas 元素 ID
- `containerId` (string): 容器元素 ID
- `color` (string): 图表颜色（十六进制）
- `formatFn` (Function): 数值格式化函数

#### 方法

##### draw(data, maxValue)

绘制图表。

```javascript
chart.draw(dataArray, 100);
```

**参数**:
- `data` (Array<number>): 数据数组
- `maxValue` (number): Y 轴最大值（默认 100）

##### resize()

调整画布尺寸以匹配容器。

```javascript
chart.resize();
```

##### destroy()

销毁图表实例，清理资源。

```javascript
chart.destroy();
```

#### 完整示例

```javascript
// 1. 创建图表实例
const chart = new MiniChart(
  'chart-cpu',
  'chart-area-cpu',
  '#3b82f6',
  v => v.toFixed(0) + '%'
);

// 2. 准备数据
const data = new Array(40).fill(0);

// 3. 更新循环
setInterval(() => {
  // 移除最旧数据，添加最新数据
  data.shift();
  data.push(30 + Math.random() * 40);

  // 绘制图表
  chart.draw(data, 100);
}, 1000);
```

#### 格式化函数示例

```javascript
// 百分比
v => v.toFixed(0) + '%'

// GB 格式
v => (v / 100 * 32).toFixed(1) + 'G'

// 网络流量（自动单位）
v => {
  if (v > 1024 * 1024) return (v / (1024 * 1024)).toFixed(1) + 'G';
  if (v > 1024) return (v / 1024).toFixed(1) + 'M';
  return v.toFixed(0) + 'K';
}

// 功耗
v => v.toFixed(0) + 'W'
```

---

### DynamicScaler - 动态缩放控制器

用于网络流量等波动较大的指标，自动调整 Y 轴上限。

#### 特性

- ✅ 立即拔高（超过 90% 时）
- ✅ 延迟缩小（低于 60% 时，3 秒延迟）
- ✅ 平滑过渡（Lerp 0.2 插值）
- ✅ 稳定区间（60%-90%）
- ✅ 最小底线（防止缩放到 0）

#### 构造函数

```javascript
new DynamicScaler(initialMax, shrinkDelay)
```

**参数**:
- `initialMax` (number): 初始上限值（默认 1024）
- `shrinkDelay` (number): 缩小延迟时间（默认 3000ms）

#### 方法

##### update(data)

更新缩放上限。

```javascript
const currentMax = scaler.update(dataArray);
```

**参数**:
- `data` (Array<number>): 数据数组

**返回**: 当前上限值

##### reset(newMax)

重置缩放器。

```javascript
scaler.reset(20480); // 重置为 20MB
```

##### getCurrentMax()

获取当前上限。

```javascript
const max = scaler.getCurrentMax();
```

##### setMinFloor(floor)

设置最小底线。

```javascript
scaler.setMinFloor(10); // 最小 10 KB/s
```

#### 完整示例

```javascript
// 1. 创建缩放器
const netScaler = new DynamicScaler(20480, 3000); // 初始 20MB, 3秒延迟

// 2. 创建图表
const netChart = new MiniChart(
  'chart-net',
  'chart-area-net',
  '#0ea5e9',
  v => {
    if (v > 1024 * 1024) return (v / (1024 * 1024)).toFixed(1) + 'G';
    if (v > 1024) return (v / 1024).toFixed(1) + 'M';
    return v.toFixed(0) + 'K';
  }
);

// 3. 准备数据
const netData = new Array(40).fill(0);

// 4. 更新循环
setInterval(() => {
  // 添加新数据（模拟网络流量波动）
  netData.shift();
  netData.push(Math.random() * 1024 * 20); // 0-20MB

  // 动态缩放
  const currentMax = netScaler.update(netData);

  // 绘制图表
  netChart.draw(netData, currentMax);
}, 1000);
```

---

## 设计模式

### 峰谷值标记

图表自动检测并标注数据的峰值和谷值。

#### 检测算法

1. **候选极值点检测**
   - 峰值：`curr > prev && curr > next`
   - 谷值：`curr < prev && curr < next`

2. **峰谷交替过滤**
   - 确保峰-谷-峰-谷交替规律
   - 最小幅度阈值：5
   - 同类型只保留更极端值

3. **生命周期管理**
   - 清理移出视图的标记
   - 数据左移时更新索引
   - 只在右侧 5 个数据点内添加新标记

#### 样式

```css
.xh-chart-peak-marker {
  /* 峰值：图表主色 */
  color: var(--chart-color);

  /* 谷值：次要文本色 */
  color: #94a3b8;

  /* 背景 */
  background: rgba(0, 0, 0, 0.7);

  /* 过渡 */
  transition: left 0.3s ease, top 0.3s ease, opacity 0.3s ease;
}
```

### 左侧渐隐效果

使用 Canvas 合成模式创造历史数据淡出效果。

```javascript
ctx.globalCompositeOperation = 'destination-out';
const fadeGradient = ctx.createLinearGradient(0, 0, width * 0.5, 0);
fadeGradient.addColorStop(0, 'rgba(0, 0, 0, 1)');      // 完全擦除
fadeGradient.addColorStop(0.6, 'rgba(0, 0, 0, 0.5)');  // 半透明
fadeGradient.addColorStop(1, 'rgba(0, 0, 0, 0)');      // 不擦除
ctx.fillStyle = fadeGradient;
ctx.fillRect(0, 0, width * 0.5, height);
```

**效果**:
- 左侧 0-30%：完全透明
- 左侧 30-50%：渐变过渡
- 右侧 50-100%：完全可见

---

## 响应式设计

### 断点

```css
/* 小屏幕（手机） */
@media (max-width: 768px) {
  /* 单列布局 */
}

/* 中等屏幕（平板） */
@media (min-width: 768px) and (max-width: 1200px) {
  /* 两列布局 */
}

/* 大屏幕（桌面） */
@media (min-width: 1200px) {
  /* 三列布局 */
}
```

### 响应式网格

```html
<div class="stats-grid">
  <div class="xh-stat-card">...</div>
  <div class="xh-stat-card">...</div>
  <div class="xh-stat-card">...</div>
</div>
```

```css
.stats-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 16px;
}

@media (max-width: 1200px) {
  .stats-grid {
    grid-template-columns: repeat(2, 1fr);
  }
}

@media (max-width: 768px) {
  .stats-grid {
    grid-template-columns: 1fr;
  }
}
```

---

## 最佳实践

### 1. 性能优化

```javascript
// ✅ 使用防抖处理 resize 事件
let resizeTimeout;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimeout);
  resizeTimeout = setTimeout(() => chart.resize(), 100);
});

// ✅ 限制数据点数量
const MAX_POINTS = 40;
if (data.length > MAX_POINTS) {
  data.shift();
}

// ✅ 销毁不再使用的图表
chart.destroy();
```

### 2. 可访问性

```html
<!-- ✅ 使用语义化 HTML -->
<section aria-label="系统监控">
  <div class="xh-stat-card" role="region" aria-label="CPU 使用率">
    ...
  </div>
</section>

<!-- ✅ 提供文本替代 -->
<canvas id="chart-cpu" aria-label="CPU 使用率历史曲线"></canvas>
```

### 3. 颜色对比度

所有颜色组合都满足 WCAG AA 标准（对比度 > 4.5:1）。

```css
/* ✅ 高对比度文本 */
color: var(--xh-color-text-primary); /* #f8fafc on #0f172a */

/* ✅ 峰谷值标记背景 */
background: rgba(0, 0, 0, 0.7); /* 确保文本可读 */
```

---

## 浏览器兼容性

| 特性 | Chrome | Firefox | Safari | Edge |
|------|--------|---------|--------|------|
| CSS Variables | ✅ 49+ | ✅ 31+ | ✅ 9.1+ | ✅ 15+ |
| Canvas 2D | ✅ 全部 | ✅ 全部 | ✅ 全部 | ✅ 全部 |
| backdrop-filter | ✅ 76+ | ✅ 103+ | ✅ 9+ | ✅ 79+ |
| Grid Layout | ✅ 57+ | ✅ 52+ | ✅ 10.1+ | ✅ 16+ |

---

## 许可证

MIT License

---

## 更新日志

### v1.0.0 (2026-01-31)

- ✅ 初始版本发布
- ✅ 设计 Tokens 系统
- ✅ GlassPanel 组件
- ✅ StatCard 组件
- ✅ MiniChart 图表引擎
- ✅ DynamicScaler 动态缩放
- ✅ 完整文档和示例

---

*文档生成时间: 2026-01-31*
