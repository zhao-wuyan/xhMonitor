# XhMonitor 组件库

<div align="center">

![Version](https://img.shields.io/badge/version-1.0.0-blue.svg)
![License](https://img.shields.io/badge/license-MIT-green.svg)

**玻璃拟态设计 · 实时数据可视化 · 响应式布局**

从 `ui-preview-v2.html` 提取的完整设计系统

[快速开始](docs/QUICK_START.md) · [完整文档](docs/README.md) · [在线示例](examples/index.html)

</div>

---

## ✨ 特性

- 🎨 **玻璃拟态设计** - 半透明背景 + 毛玻璃效果
- 📊 **实时图表引擎** - Canvas 2D 渲染，支持峰谷值标记
- 🌊 **左侧渐隐效果** - 历史数据自然淡出
- 📈 **动态缩放** - 自动调整 Y 轴上限（网络流量等）
- 🎯 **设计 Tokens** - 完整的设计系统（颜色、排版、间距、动画）
- 📱 **响应式布局** - 移动优先，3→2→1 列自适应
- ⚡ **高性能** - 防抖优化，增量更新
- 🔧 **易于集成** - 纯 HTML/CSS/JS，无依赖

---

## 🚀 快速开始

### 1. 引入样式和脚本

```html
<!-- 设计 Tokens -->
<link rel="stylesheet" href="components/core/design-tokens.css">

<!-- 核心组件 -->
<link rel="stylesheet" href="components/core/glass-panel.css">
<link rel="stylesheet" href="components/core/stat-card.css">

<!-- 图表引擎 -->
<script src="components/charts/MiniChart.js"></script>
<script src="components/charts/DynamicScaler.js"></script>
```

### 2. 创建监控卡片

```html
<div class="xh-stat-card xh-glass-panel" style="--accent: var(--xh-color-cpu)">
  <div class="xh-stat-card__glow" style="background: var(--accent)"></div>
  <div class="xh-stat-card__info">
    <div class="xh-stat-card__label">
      <span style="color: var(--accent)">●</span> CPU
    </div>
    <div class="xh-stat-card__value" id="cpu-value">0%</div>
    <div class="xh-stat-card__subtitle">i9-13900K</div>
  </div>
  <div class="xh-stat-card__chart" id="chart-area-cpu">
    <canvas id="chart-cpu" class="xh-stat-card__canvas"></canvas>
  </div>
</div>
```

### 3. 初始化图表

```javascript
// 创建图表实例
const cpuChart = new MiniChart(
  'chart-cpu',
  'chart-area-cpu',
  '#3b82f6',
  v => v.toFixed(0) + '%'
);

// 准备数据
const cpuData = new Array(40).fill(0);

// 更新循环
setInterval(() => {
  cpuData.shift();
  cpuData.push(Math.random() * 100);
  cpuChart.draw(cpuData, 100);
}, 1000);
```

📖 查看[完整快速开始指南](docs/QUICK_START.md)

---

## 📦 组件列表

### 核心组件

| 组件 | 描述 | 文档 |
|------|------|------|
| **GlassPanel** | 玻璃拟态面板容器 | [查看](docs/README.md#glasspanel---玻璃拟态面板) |
| **StatCard** | 资源监控卡片 | [查看](docs/README.md#statcard---资源监控卡片) |

### 图表组件

| 组件 | 描述 | 文档 |
|------|------|------|
| **MiniChart** | 迷你图表引擎 | [查看](docs/README.md#minichart---迷你图表引擎) |
| **DynamicScaler** | 动态缩放控制器 | [查看](docs/README.md#dynamicscaler---动态缩放控制器) |

---

## 🎨 设计系统

### 颜色

```css
/* 语义色（监控指标） */
--xh-color-cpu: #3b82f6;   /* 蓝色 - CPU */
--xh-color-ram: #8b5cf6;   /* 紫色 - RAM */
--xh-color-gpu: #10b981;   /* 绿色 - GPU */
--xh-color-vram: #f59e0b;  /* 橙色 - VRAM */
--xh-color-net: #0ea5e9;   /* 天蓝 - 网络 */
--xh-color-pwr: #f43f5e;   /* 玫红 - 功耗 */
```

### 排版

```css
/* 字体族 */
--xh-font-sans: 'Segoe UI', system-ui, -apple-system, sans-serif;
--xh-font-mono: 'Consolas', 'Monaco', 'Courier New', monospace;

/* 字号 */
--xh-font-size-xs: 0.65rem;   /* 10.4px */
--xh-font-size-sm: 0.75rem;   /* 12px */
--xh-font-size-base: 0.85rem; /* 13.6px */
--xh-font-size-xl: 1.8rem;    /* 28.8px */
```

### 动画

```css
/* Duration */
--xh-duration-fast: 200ms;
--xh-duration-normal: 300ms;
--xh-duration-slow: 500ms;

/* Easing */
--xh-ease: ease;
--xh-ease-in-out: ease-in-out;
```

📖 查看[完整设计 Tokens](core/design-tokens.css)

---

## 📊 图表特性

### MiniChart 图表引擎

- ✅ **Canvas 2D 渲染** - 高性能实时绘制
- ✅ **左侧 50% 渐隐** - 使用 `destination-out` 合成模式
- ✅ **峰谷值标记** - 自动检测并标注峰值和谷值
- ✅ **渐变填充** - 曲线下方柔和的渐变效果
- ✅ **响应式画布** - 自动适应容器尺寸
- ✅ **自定义格式化** - 灵活的数值格式化函数

### DynamicScaler 动态缩放

- ✅ **立即拔高** - 超过 90% 时瞬间调整
- ✅ **延迟缩小** - 低于 60% 时 3 秒后平滑下降
- ✅ **Lerp 插值** - 缓缓下降效果（0.2 因子）
- ✅ **稳定区间** - 60%-90% 保持不变
- ✅ **最小底线** - 防止缩放到 0

---

## 📁 目录结构

```
xhmonitor-web/components/
├── core/                      # 核心组件
│   ├── design-tokens.css      # 设计 Tokens
│   ├── glass-panel.css        # 玻璃拟态面板
│   └── stat-card.css          # 资源监控卡片
├── charts/                    # 图表组件
│   ├── MiniChart.js           # 迷你图表引擎
│   └── DynamicScaler.js       # 动态缩放控制器
├── docs/                      # 文档
│   ├── README.md              # 完整文档
│   └── QUICK_START.md         # 快速开始
├── examples/                  # 示例
│   └── index.html             # 完整示例页面
├── index.js                   # 入口文件
├── package.json               # 包配置
└── README.md                  # 本文件
```

---

## 🎯 使用场景

### 1. 系统监控面板

```javascript
// CPU, RAM, GPU, VRAM, NET, PWR
const charts = {
  cpu: new MiniChart('chart-cpu', 'chart-area-cpu', '#3b82f6', fmtPercent),
  ram: new MiniChart('chart-ram', 'chart-area-ram', '#8b5cf6', fmtGB),
  // ...
};
```

### 2. 网络流量监控（动态缩放）

```javascript
const netScaler = new DynamicScaler(20480, 3000);
const netChart = new MiniChart('chart-net', 'chart-area-net', '#0ea5e9', fmtNet);

setInterval(() => {
  const currentMax = netScaler.update(netData);
  netChart.draw(netData, currentMax);
}, 1000);
```

### 3. 自定义监控指标

```javascript
// 温度监控
const tempChart = new MiniChart(
  'chart-temp',
  'chart-area-temp',
  '#ff6b6b',
  v => v.toFixed(1) + '°C'
);

// 磁盘 I/O
const diskChart = new MiniChart(
  'chart-disk',
  'chart-area-disk',
  '#51cf66',
  v => v.toFixed(0) + ' MB/s'
);
```

---

## 🌐 浏览器兼容性

| 特性 | Chrome | Firefox | Safari | Edge |
|------|--------|---------|--------|------|
| CSS Variables | ✅ 49+ | ✅ 31+ | ✅ 9.1+ | ✅ 15+ |
| Canvas 2D | ✅ 全部 | ✅ 全部 | ✅ 全部 | ✅ 全部 |
| backdrop-filter | ✅ 76+ | ✅ 103+ | ✅ 9+ | ✅ 79+ |
| Grid Layout | ✅ 57+ | ✅ 52+ | ✅ 10.1+ | ✅ 16+ |

---

## 📚 文档

- 📖 [完整文档](docs/README.md) - 详细的 API 文档和使用指南
- 🚀 [快速开始](docs/QUICK_START.md) - 5 分钟快速上手
- 🎨 [在线示例](examples/index.html) - 交互式组件演示
- 💡 [设计系统](core/design-tokens.css) - 完整的设计 Tokens

---

## 🔧 开发

### 本地运行示例

```bash
# 启动本地服务器
cd xhmonitor-web/components
python -m http.server 8080

# 访问示例页面
open http://localhost:8080/examples/index.html
```

### 集成到项目

```bash
# 复制组件库到你的项目
cp -r xhmonitor-web/components /path/to/your/project/

# 在 HTML 中引入
<link rel="stylesheet" href="components/core/design-tokens.css">
<script src="components/charts/MiniChart.js"></script>
```

---

## 🎨 设计理念

### 玻璃拟态 (Glassmorphism)

- **半透明背景** - `rgba(30, 41, 59, 0.6)`
- **毛玻璃效果** - `backdrop-filter: blur(16px)`
- **边框高光** - `rgba(255, 255, 255, 0.08)`
- **柔和阴影** - `0 4px 6px -1px rgba(0, 0, 0, 0.1)`

### 数据可视化

- **左侧渐隐** - 历史数据自然淡出，突出最新数据
- **峰谷标记** - 自动标注关键数据点
- **动态缩放** - 自适应数据范围，避免固定上限
- **渐变填充** - 柔和的视觉效果

### 响应式设计

- **移动优先** - 从小屏幕开始设计
- **断点系统** - 768px (手机), 1200px (平板)
- **Grid 布局** - 3→2→1 列自适应

---

## 📊 性能优化

### 1. Canvas 渲染

```javascript
// ✅ 防抖处理 resize 事件
let resizeTimeout;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimeout);
  resizeTimeout = setTimeout(() => chart.resize(), 100);
});
```

### 2. 数据管理

```javascript
// ✅ 限制数据点数量
const MAX_POINTS = 40;
if (data.length > MAX_POINTS) {
  data.shift();
}
```

### 3. 标记生命周期

```javascript
// ✅ 增量更新，复用 DOM 元素
// ✅ 只添加新出现的标记
// ✅ 只移除移出视图的标记
```

---

## 🎯 最佳实践

### 1. 使用设计 Tokens

```css
/* ✅ 使用 CSS 变量 */
color: var(--xh-color-text-primary);
font-family: var(--xh-font-sans);
transition: all var(--xh-duration-fast) var(--xh-ease);

/* ❌ 避免硬编码 */
color: #f8fafc;
font-family: 'Segoe UI';
transition: all 200ms ease;
```

### 2. 格式化函数

```javascript
// ✅ 使用语义化的格式化函数
const fmtPercent = v => v.toFixed(0) + '%';
const fmtGB = v => (v / 100 * 32).toFixed(1) + 'G';

// ✅ 自动单位转换
const fmtNet = v => {
  if (v > 1024 * 1024) return (v / (1024 * 1024)).toFixed(1) + 'G';
  if (v > 1024) return (v / 1024).toFixed(1) + 'M';
  return v.toFixed(0) + 'K';
};
```

### 3. 销毁资源

```javascript
// ✅ 组件卸载时销毁图表
chart.destroy();

// ✅ 清理定时器
clearInterval(updateInterval);
```

---

## 🤝 贡献

欢迎贡献代码、报告问题或提出建议！

1. Fork 本仓库
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启 Pull Request

---

## 📄 许可证

MIT License - 详见 [LICENSE](LICENSE) 文件

---

## 🙏 致谢

- 设计灵感来自 `ui-preview-v2.html`
- 玻璃拟态设计理念
- Canvas 2D API

---

## 📮 联系方式

- 项目主页: [GitHub](https://github.com/xhmonitor/components)
- 问题反馈: [Issues](https://github.com/xhmonitor/components/issues)
- 文档: [在线文档](docs/README.md)

---

<div align="center">

**XhMonitor 组件库 v1.0.0**

从 `ui-preview-v2.html` 提取的完整设计系统

Made with ❤️ by XhMonitor Team

</div>
