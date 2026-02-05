# XhMonitor Web

<div align="center">

![Version](https://img.shields.io/badge/version-0.2.6-blue.svg)
![React](https://img.shields.io/badge/React-19-61dafb.svg)
![TypeScript](https://img.shields.io/badge/TypeScript-5.9-3178c6.svg)
![Vite](https://img.shields.io/badge/Vite-7-646cff.svg)

**实时监控 · 玻璃拟态设计 · 响应式布局**

XhMonitor 的 Web 前端应用，提供实时进程资源监控和可视化

[快速开始](#快速开始) · [功能特性](#功能特性) · [技术栈](#技术栈) · [开发指南](#开发指南)

</div>

---

## 功能特性

### 🎨 现代化 UI 设计
- **玻璃拟态设计** - 半透明背景 + 毛玻璃效果，现代化视觉体验
- **响应式布局** - 支持桌面、平板、移动端自适应
- **可拖拽网格** - 自由调整指标卡片顺序和布局
- **主题定制** - 支持自定义主题颜色、透明度、背景图片
- **暗色模式** - 护眼暗色主题，适合长时间使用

### 📊 实时数据可视化
- **实时图表** - Canvas 2D 渲染，支持峰谷值标记
- **多维度监控** - CPU、内存、GPU、显存、硬盘、功耗、网络速度
- **进程列表** - 实时显示进程资源占用，支持搜索和排序
- **历史数据** - 60 秒历史数据曲线，左侧渐隐效果
- **动态缩放** - 自动调整 Y 轴上限（网络流量等）

### 🔄 实时通信
- **SignalR 集成** - 实时推送最新指标，延迟 < 100ms
- **自动重连** - 连接断开自动重连，保证数据连续性
- **状态指示** - 实时显示连接状态（在线/离线/重连中）

### 🌍 国际化支持
- **中英文切换** - 完整的中英文界面
- **易于扩展** - 基于 i18n 模块，支持添加更多语言

### 🔒 安全认证
- **访问密钥认证** - 支持访问密钥保护数据接口
- **IP 白名单** - 支持 IP 白名单限制访问来源
- **局域网访问控制** - 可配置是否允许局域网访问

---

## 技术栈

### 核心框架
- **React 19** - 最新的 React 版本，支持 Compiler 和 Server Components
- **TypeScript 5.9** - 类型安全，提升开发体验
- **Vite 7** - 极速的开发服务器和构建工具

### UI 框架
- **TailwindCSS v4** - 原子化 CSS 框架，快速构建 UI
- **Lucide React** - 现代化图标库
- **ECharts 6** - 强大的图表库（用于复杂图表）

### 状态管理
- **React Context** - 轻量级状态管理
  - `LayoutContext` - 布局状态管理
  - `TimeSeriesContext` - 时序数据管理
  - `AuthContext` - 认证状态管理

### 实时通信
- **@microsoft/signalr** - SignalR 客户端，实时数据推送

### 开发工具
- **ESLint 9** - 代码质量检查
- **TypeScript ESLint** - TypeScript 代码规范

---

## 快速开始

### 前置要求

- Node.js >= 18
- npm >= 9

### 安装依赖

```bash
cd xhmonitor-web
npm install
```

### 开发模式

```bash
npm run dev
```

访问 http://localhost:35180

### 生产构建

```bash
npm run build
```

构建产物位于 `dist/` 目录

### 预览构建

```bash
npm run preview
```

---

## 项目结构

```
xhmonitor-web/
├── src/
│   ├── components/          # UI 组件
│   │   ├── ChartCanvas.tsx  # 图表画布组件
│   │   ├── DiskWidget.tsx   # 硬盘指标组件
│   │   ├── DraggableGrid.tsx # 可拖拽网格
│   │   ├── Header.tsx       # 页面头部
│   │   ├── ProcessList.tsx  # 进程列表
│   │   ├── SettingsDrawer.tsx # 设置抽屉
│   │   └── StatCard.tsx     # 指标卡片
│   ├── contexts/            # React Context
│   │   ├── AuthContext.tsx  # 认证状态
│   │   ├── LayoutContext.tsx # 布局状态
│   │   └── TimeSeriesContext.tsx # 时序数据
│   ├── hooks/               # 自定义 Hooks
│   │   ├── useMetricsHub.ts # SignalR 连接
│   │   ├── useMetricConfig.ts # 指标配置
│   │   ├── useTimeSeries.ts # 时序数据
│   │   ├── useLayoutState.ts # 布局状态
│   │   └── useAdaptiveScroll.ts # 自适应滚动
│   ├── pages/               # 页面组件
│   │   ├── AccessKeyScreen.tsx # 访问密钥输入页
│   │   └── DesktopWidget.tsx # 桌面小部件
│   ├── config/              # 配置文件
│   │   ├── endpoints.ts     # API 端点配置
│   │   └── accessKey.ts     # 访问密钥管理
│   ├── utils/               # 工具函数
│   │   ├── apiFetch.ts      # 统一 API 请求
│   │   └── index.ts         # 通用工具
│   ├── types.ts             # TypeScript 类型定义
│   ├── i18n.ts              # 国际化配置
│   ├── version.ts           # 版本信息
│   ├── App.tsx              # 主应用组件
│   └── main.tsx             # 应用入口
├── public/                  # 静态资源
├── components/              # 组件库（独立）
│   ├── core/                # 核心组件
│   ├── charts/              # 图表组件
│   └── docs/                # 组件文档
├── package.json
├── vite.config.ts           # Vite 配置
├── tsconfig.json            # TypeScript 配置
└── README.md
```

---

## 开发指南

### 添加新指标

1. **更新类型定义** (`src/types.ts`)

```typescript
export interface SystemUsage {
  // ... 现有字段
  newMetric: number;
}
```

2. **更新时序数据选择器** (`src/App.tsx`)

```typescript
const timeSeriesOptions = useMemo(
  () => ({
    selectors: {
      // ... 现有选择器
      newMetric: (usage: SystemUsage) => usage.newMetric,
    },
  }),
  []
);
```

3. **添加指标卡片** (`src/App.tsx`)

```tsx
<StatCard
  key="newMetric"
  cardId="newMetric"
  title={t('New Metric')}
  value={systemUsage?.newMetric ?? 0}
  unit="unit"
  accentColor="#color"
>
  <ChartCanvas
    seriesKey="newMetric"
    color="#color"
    formatFn={(v) => v.toFixed(1) + 'unit'}
  />
</StatCard>
```

4. **更新国际化** (`src/i18n.ts`)

```typescript
export const i18n = {
  zh: {
    'New Metric': '新指标',
  },
  en: {
    'New Metric': 'New Metric',
  },
};
```

### 自定义主题

在设置面板中可以自定义：
- **主题颜色** - 每个指标卡片的主题色
- **透明度** - 面板透明度（0-100%）
- **背景图片** - 自定义背景图片
- **模糊效果** - 背景模糊程度
- **遮罩透明度** - 背景遮罩透明度

### API 端点配置

编辑 `src/config/endpoints.ts`:

```typescript
export const API_BASE_URL = 'http://localhost:35179/api/v1';
export const METRICS_HUB_URL = 'http://localhost:35179/hubs/metrics';
```

### 访问密钥配置

1. 后端启用访问密钥认证（`appsettings.json`）:

```json
{
  "System": {
    "EnableAccessKey": true,
    "AccessKey": "your-secret-key"
  }
}
```

2. Web 端会自动显示访问密钥输入页面
3. 输入正确的访问密钥后即可访问数据

---

## 组件库

项目包含独立的组件库 (`components/`)，提供可复用的 UI 组件和图表引擎。

### 特性
- 🎨 玻璃拟态设计
- 📊 实时图表引擎（Canvas 2D）
- 🌊 左侧渐隐效果
- 📈 动态缩放
- 🎯 完整的设计 Tokens
- 📱 响应式布局
- ⚡ 高性能（防抖优化）
- 🔧 易于集成（纯 HTML/CSS/JS）

### 文档
- [组件库 README](components/README.md)
- [快速开始](components/docs/QUICK_START.md)
- [完整文档](components/docs/README.md)
- [在线示例](components/examples/index.html)

---

## 性能优化

### 图表渲染优化
- **Canvas 2D 渲染** - 高性能图表渲染
- **防抖优化** - 避免频繁重绘
- **增量更新** - 只更新变化的数据点
- **传感器缓存** - 减少硬件轮询频率

### 数据传输优化
- **SignalR 实时推送** - 避免轮询，减少网络开销
- **数据压缩** - 减少传输数据量
- **自动重连** - 连接断开自动重连

### 渲染优化
- **React.memo** - 避免不必要的组件重渲染
- **useMemo/useCallback** - 缓存计算结果和回调函数
- **虚拟滚动** - 大列表性能优化（进程列表）

---

## 浏览器兼容性

- Chrome >= 90
- Firefox >= 88
- Safari >= 14
- Edge >= 90

---

## 常见问题

### 无法连接到后端服务

1. 确认后端服务已启动（`XhMonitor.Service.exe`）
2. 检查端口配置（默认 35179）
3. 检查防火墙设置

### 访问密钥认证失败

1. 确认后端已启用访问密钥认证
2. 确认输入的访问密钥正确
3. 检查浏览器控制台错误信息

### 图表不显示

1. 确认 SignalR 连接成功（查看连接状态）
2. 检查浏览器控制台错误信息
3. 确认后端正在推送数据

---

## 贡献指南

欢迎贡献代码、报告问题或提出建议！

### 开发流程

1. Fork 本仓库
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'feat: Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 创建 Pull Request

### 代码规范

- 遵循 ESLint 规则
- 使用 TypeScript 类型注解
- 编写清晰的注释
- 保持代码简洁

---

## 许可证

MIT License

---

## 相关链接

- [主项目 README](../README.md)
- [CHANGELOG](../CHANGELOG.md)
- [后端服务文档](../XhMonitor.Service/README.md)
- [桌面应用文档](../XhMonitor.Desktop/README.md)

---

**XhMonitor Web** - 高性能的 Windows 进程资源监控 Web 应用
