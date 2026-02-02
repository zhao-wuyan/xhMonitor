# XhMonitor 组件库 - 快速开始

## 5 分钟快速上手

### 步骤 1: 引入样式和脚本

```html
<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>我的监控面板</title>

    <!-- 引入组件样式 -->
    <link rel="stylesheet" href="components/core/design-tokens.css">
    <link rel="stylesheet" href="components/core/glass-panel.css">
    <link rel="stylesheet" href="components/core/stat-card.css">
</head>
<body>
    <!-- 你的内容 -->

    <!-- 引入图表引擎 -->
    <script src="components/charts/MiniChart.js"></script>
    <script src="components/charts/DynamicScaler.js"></script>
</body>
</html>
```

### 步骤 2: 创建一个监控卡片

```html
<div class="xh-stat-card xh-glass-panel" style="--accent: var(--xh-color-cpu)">
  <!-- 装饰光晕 -->
  <div class="xh-stat-card__glow" style="background: var(--accent)"></div>

  <!-- 信息区域 -->
  <div class="xh-stat-card__info">
    <div class="xh-stat-card__label">
      <span style="color: var(--accent)">●</span> CPU
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

### 步骤 3: 初始化图表

```javascript
// 1. 创建图表实例
const cpuChart = new MiniChart(
  'chart-cpu',           // Canvas ID
  'chart-area-cpu',      // 容器 ID
  '#3b82f6',             // 颜色
  v => v.toFixed(0) + '%' // 格式化函数
);

// 2. 准备数据缓冲区（40 个数据点）
const cpuData = new Array(40).fill(0);

// 3. 更新循环（每秒更新一次）
setInterval(() => {
  // 移除最旧数据，添加最新数据
  cpuData.shift();
  cpuData.push(Math.random() * 100); // 替换为真实数据

  // 绘制图表
  cpuChart.draw(cpuData, 100);

  // 更新数值显示
  const currentValue = cpuData[cpuData.length - 1];
  document.getElementById('cpu-value').innerText =
    currentValue.toFixed(1) + '%';
}, 1000);
```

### 步骤 4: 运行示例

打开浏览器访问你的 HTML 文件，你将看到一个带有实时曲线图的监控卡片！

---

## 常见场景

### 场景 1: 网络流量监控（动态缩放）

```javascript
// 创建动态缩放器
const netScaler = new DynamicScaler(20480, 3000); // 初始 20MB, 3秒延迟

// 创建图表
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

const netData = new Array(40).fill(0);

setInterval(() => {
  netData.shift();
  netData.push(Math.random() * 1024 * 20); // 0-20MB

  // 动态调整 Y 轴上限
  const currentMax = netScaler.update(netData);
  netChart.draw(netData, currentMax);
}, 1000);
```

### 场景 2: 多个监控卡片

```html
<div class="stats-grid">
  <!-- CPU -->
  <div class="xh-stat-card xh-glass-panel" style="--accent: var(--xh-color-cpu)">
    ...
  </div>

  <!-- RAM -->
  <div class="xh-stat-card xh-glass-panel" style="--accent: var(--xh-color-ram)">
    ...
  </div>

  <!-- GPU -->
  <div class="xh-stat-card xh-glass-panel" style="--accent: var(--xh-color-gpu)">
    ...
  </div>
</div>

<style>
.stats-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 16px;
}

@media (max-width: 1200px) {
  .stats-grid { grid-template-columns: repeat(2, 1fr); }
}

@media (max-width: 768px) {
  .stats-grid { grid-template-columns: 1fr; }
}
</style>
```

### 场景 3: 自定义颜色

```html
<!-- 使用自定义颜色 -->
<div class="xh-stat-card xh-glass-panel" style="--accent: #ff6b6b">
  <div class="xh-stat-card__glow" style="background: var(--accent)"></div>
  ...
</div>

<script>
const customChart = new MiniChart(
  'chart-custom',
  'chart-area-custom',
  '#ff6b6b', // 自定义颜色
  v => v.toFixed(0) + ' units'
);
</script>
```

---

## 集成真实数据

### 从 API 获取数据

```javascript
// 定期从 API 获取数据
async function fetchCpuData() {
  try {
    const response = await fetch('/api/system/cpu');
    const data = await response.json();
    return data.usage; // 假设返回 { usage: 45.2 }
  } catch (error) {
    console.error('Failed to fetch CPU data:', error);
    return 0;
  }
}

// 更新循环
setInterval(async () => {
  const cpuUsage = await fetchCpuData();

  cpuData.shift();
  cpuData.push(cpuUsage);

  cpuChart.draw(cpuData, 100);
  document.getElementById('cpu-value').innerText = cpuUsage.toFixed(1) + '%';
}, 1000);
```

### 使用 WebSocket 实时数据

```javascript
// 连接 WebSocket
const ws = new WebSocket('ws://localhost:8080/system-stats');

ws.onmessage = (event) => {
  const stats = JSON.parse(event.data);

  // 更新 CPU
  cpuData.shift();
  cpuData.push(stats.cpu);
  cpuChart.draw(cpuData, 100);

  // 更新 RAM
  ramData.shift();
  ramData.push(stats.ram);
  ramChart.draw(ramData, 100);

  // 更新显示
  document.getElementById('cpu-value').innerText = stats.cpu.toFixed(1) + '%';
  document.getElementById('ram-value').innerText = (stats.ram / 100 * 32).toFixed(1) + ' GB';
};
```

---

## 性能优化建议

### 1. 限制数据点数量

```javascript
const MAX_POINTS = 40; // 推荐 30-60 个点

if (data.length > MAX_POINTS) {
  data.shift(); // 移除最旧数据
}
```

### 2. 使用 requestAnimationFrame

```javascript
let animationId;

function updateCharts() {
  // 更新所有图表
  charts.cpu.draw(cpuData, 100);
  charts.ram.draw(ramData, 100);

  // 继续下一帧
  animationId = requestAnimationFrame(updateCharts);
}

// 启动
updateCharts();

// 停止
cancelAnimationFrame(animationId);
```

### 3. 销毁不再使用的图表

```javascript
// 组件卸载时
chart.destroy();
```

---

## 故障排查

### 问题 1: 图表不显示

**检查清单**:
- ✅ Canvas 元素是否存在？
- ✅ 容器元素是否存在？
- ✅ Canvas 是否有高度？（父容器需要设置高度）
- ✅ 是否调用了 `chart.draw()`？

```javascript
// 调试代码
console.log('Canvas:', document.getElementById('chart-cpu'));
console.log('Container:', document.getElementById('chart-area-cpu'));
console.log('Canvas size:', chart.canvas.width, chart.canvas.height);
```

### 问题 2: 峰谷值标记不显示

**原因**: 数据变化幅度太小（< 5）

**解决方案**: 调整最小幅度阈值

```javascript
// 修改 MiniChart.js 中的 minAmplitude
const minAmplitude = 3; // 降低阈值
```

### 问题 3: 动态缩放不工作

**检查清单**:
- ✅ 是否创建了 DynamicScaler 实例？
- ✅ 是否调用了 `scaler.update(data)`？
- ✅ 是否将返回值传递给 `chart.draw(data, maxValue)`？

```javascript
// 正确用法
const currentMax = netScaler.update(netData);
netChart.draw(netData, currentMax); // 传递 currentMax
```

---

## 下一步

- 📖 阅读[完整文档](docs/README.md)
- 🎨 查看[在线示例](examples/index.html)
- 💡 探索[设计 Tokens](core/design-tokens.css)
- 🚀 集成到你的项目

---

## 需要帮助？

- 查看[完整文档](docs/README.md)
- 查看[示例代码](examples/)
- 提交 Issue

---

*快速开始指南 - XhMonitor 组件库 v1.0.0*
