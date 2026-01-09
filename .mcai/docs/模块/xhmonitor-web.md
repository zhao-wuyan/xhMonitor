# xhmonitor-web

本模块是 XhMonitor 的前端 Web 应用，使用 React 19 + TypeScript + Vite 构建，提供现代化的数据可视化界面。

## 职责

- 实时显示进程指标数据
- 提供进程列表和搜索功能
- 支持历史数据可视化（ECharts）
- 国际化支持（中英文）
- Glassmorphism UI 设计

## 结构

```
xhmonitor-web/
├── src/
│   ├── components/      # React 组件
│   │   ├── App.tsx            # 主应用组件
│   │   ├── ProcessList.tsx    # 进程列表
│   │   ├── MetricChart.tsx    # 指标图表
│   │   └── SystemSummary.tsx  # 系统摘要
│   ├── hooks/           # 自定义 Hooks
│   │   ├── useMetricsHub.ts   # SignalR 连接
│   │   └── useMetricConfig.ts # 指标配置
│   ├── types.ts         # TypeScript 类型定义
│   ├── utils.ts         # 工具函数
│   ├── i18n.ts          # 国际化配置
│   └── main.tsx         # 应用入口
├── index.html
├── package.json
├── vite.config.ts
├── tsconfig.json
└── tailwind.config.ts
```

## 关键文件

| 文件 | 目的 |
|------|------|
| `App.tsx` | 主应用组件，包含所有子组件和布局 |
| `ProcessList.tsx` | 显示所有监控进程的列表 |
| `MetricChart.tsx` | 使用 ECharts 显示指标趋势图 |
| `useMetricsHub.ts` | SignalR 连接和数据订阅 Hook |
| `i18n.ts` | 国际化配置（中英文） |

## 依赖

**本模块依赖**:
- `react`, `react-dom` - UI 框架
- `@microsoft/signalr` - SignalR 客户端
- `echarts`, `echarts-for-react` - 图表库
- `tailwindcss` - 样式框架
- `lucide-react` - 图标库

**后端依赖**:
- REST API: `http://localhost:35179/api/v1`
- SignalR Hub: `http://localhost:35179/hubs/metrics`

## 自定义 Hooks

### useMetricsHub

用于连接 SignalR Hub 并接收实时数据：

```typescript
const { metricsData, connectionStatus, isConnected } = useMetricsHub();

// metricsData: 实时指标数据数组
// connectionStatus: 连接状态 (Connecting, Connected, Disconnected, Error)
// isConnected: 是否已连接（布尔值）
```

### useMetricConfig

用于获取指标配置和元数据：

```typescript
const { metrics, isLoading, error } = useMetricConfig();

// metrics: 指标元数据数组
// isLoading: 加载状态
// error: 错误信息
```

## 组件结构

### App.tsx

主应用组件，包含：
- 顶部导航（语言切换）
- 系统摘要区域
- 进程列表（带搜索和排序）
- 指标图表区域

### ProcessList.tsx

进程列表组件，功能：
- 显示所有监控进程
- 搜索过滤（进程名、关键词）
- 排序（进程名、最后活跃时间）
- 点击查看详情

### MetricChart.tsx

指标图表组件，功能：
- 使用 ECharts 渲染图表
- 支持时间范围选择
- 支持多指标对比
- 响应式设计

## 国际化

### 使用方式

```typescript
import { t, changeLanguage, getCurrentLanguage } from '../i18n';

// 获取翻译文本
const text = t('Process List');

// 切换语言
changeLanguage('en');

// 获取当前语言
const lang = getCurrentLanguage(); // 'zh' 或 'en'
```

### 添加翻译

在 `i18n.ts` 中添加：

```typescript
export const i18n = {
  zh: {
    'Your Key': '中文翻译',
  },
  en: {
    'Your Key': 'Your Key',
  },
};
```

## 样式规范

### Glassmorphism 设计

使用 TailwindCSS 实现毛玻璃效果：

```typescript
// 基础玻璃卡片
<div className="glass rounded-xl p-6 backdrop-blur-md bg-white/10 border border-white/20">
  {/* 内容 */}
</div>

// 深色玻璃背景
<div className="bg-gradient-to-br from-blue-500/20 to-purple-500/20">
  {/* 内容 */}
</div>
```

### 颜色系统

| 用途 | 颜色 | Tailwind 类 |
|------|------|------------|
| 背景 | 深蓝渐变 | `from-slate-900 to-slate-800` |
| 卡片 | 半透明白 | `bg-white/10` |
| 边框 | 半透明白 | `border-white/20` |
| 文字 | 白色 | `text-white` |
| 文字（次要） | 灰色 | `text-gray-300` |

## 开发流程

### 启动开发服务器

```bash
cd xhmonitor-web
npm install
npm run dev
```

### 构建生产版本

```bash
npm run build
# 输出到 dist/ 目录
```

### 运行代码检查

```bash
npm run lint
```

## 添加新组件

### 创建组件文件

```typescript
// src/components/MyComponent.tsx
import React from 'react';
import { t } from '../i18n';

export const MyComponent: React.FC = () => {
  return (
    <div className="glass rounded-xl p-6">
      <h2 className="text-2xl font-bold">{t('My Component')}</h2>
      {/* 组件内容 */}
    </div>
  );
};
```

### 在 App.tsx 中使用

```typescript
import { MyComponent } from './components/MyComponent';

export const App = () => {
  return (
    <div className="App">
      <MyComponent />
    </div>
  );
};
```

## 性能优化

### 代码分割

使用 React.lazy 和 Suspense 实现按需加载：

```typescript
import React, { lazy, Suspense } from 'react';

const MetricChart = lazy(() => import('./components/MetricChart'));

<Suspense fallback={<div>Loading...</div>}>
  <MetricChart />
</Suspense>
```

### 数据缓存

使用 React Query 或类似库缓存 API 数据（可选）。

## 故障排查

### SignalR 连接失败

- 检查后端服务是否运行在 `http://localhost:35179`
- 检查 CORS 配置
- 查看浏览器控制台错误

### 图表不显示

- 检查 ECharts 容器是否有宽高
- 检查数据格式是否正确
- 查看浏览器控制台错误

### 样式不生效

- 确保 TailwindCSS 正确配置
- 检查类名拼写
- 使用浏览器开发者工具检查样式

## 未来改进

- [ ] 添加用户认证
- [ ] 支持自定义仪表板
- [ ] 添加数据导出功能
- [ ] 支持移动端响应式设计
- [ ] 添加暗黑/亮色主题切换
- [ ] 实现离线缓存（PWA）
