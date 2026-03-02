# xhMonitor Web — 前端项目根目录

## Purpose

xhmonitor-web 是「星核监视器」的 React 前端应用，负责通过 SignalR 实时接收后端监控数据并可视化展示系统资源（CPU、内存、GPU、磁盘、网络、电量）与进程列表。

- 版本：0.2.7
- 开发端口：35180（`vite.config.ts` 中 `server.port` 固定）
- 标题：星核监视器（`index.html`）

## Tech Stack

| 技术 | 版本 | 用途 |
|------|------|------|
| React | ^19.2.0 | UI 框架 |
| TypeScript | ~5.9.3 | 类型系统 |
| Vite | ^7.2.4 | 构建工具 |
| Tailwind CSS | ^4.1.18 | 样式（via `@tailwindcss/vite`） |
| @microsoft/signalr | ^10.0.0 | 实时数据推送 |
| echarts / echarts-for-react | ^6.0.0 | 图表渲染 |
| lucide-react | ^0.562.0 | 图标库 |
| sortablejs | ^1.15.2 | 拖拽排序 |

## Project Structure

```
xhmonitor-web/
├── src/                  # 应用源码（详见 src/CLAUDE.md）
├── components/           # 顶层组件（若有）
├── design/               # 设计稿/资源
├── dist/                 # 构建产物（git 忽略）
├── public/               # 静态资源（直接复制到 dist）
├── index.html            # 应用入口 HTML
├── vite.config.ts        # Vite 构建配置
├── tailwind.config.ts    # Tailwind 主题配置
├── tsconfig.json         # TypeScript 根配置（引用式）
├── tsconfig.app.json     # 应用代码 TS 配置
├── tsconfig.node.json    # Node 工具链 TS 配置
├── eslint.config.js      # ESLint 配置（flat config 格式）
├── package.json          # 依赖与脚本
├── I18N.md               # 国际化文档
└── README.md             # 项目说明
```

## Scripts

```bash
npm run dev       # 启动开发服务器（端口 35180，strictPort）
npm run build     # TypeScript 检查 + Vite 生产构建
npm run lint      # ESLint 代码检查
npm run test      # node --test 运行单元测试
npm run preview   # 预览生产构建
```

## Configuration

### Vite (`vite.config.ts`)

- 插件：`@vitejs/plugin-react`（React 快速刷新）、`@tailwindcss/vite`
- 构建时注入：`__APP_VERSION__`（从 `package.json` 读取）
- 开发服务器：端口 35180，`strictPort: true`（端口占用则报错，不自动换端口）

### TypeScript

采用「引用」模式拆分配置：
- `tsconfig.json`：根配置，引用 `tsconfig.app.json` 和 `tsconfig.node.json`
- `tsconfig.app.json`：应用源码配置（`src/`）
- `tsconfig.node.json`：Vite 配置文件等 Node 环境代码

### ESLint (`eslint.config.js`)

Flat config 格式，针对 `.ts/.tsx` 文件启用：
- `@eslint/js` 推荐规则
- `typescript-eslint` 推荐规则
- `eslint-plugin-react-hooks`（Hooks 规则）
- `eslint-plugin-react-refresh`（HMR 兼容）

## Sub-Module References

- **`src/`** — 应用核心源码，详见 `src/CLAUDE.md`
- **`src/components/`** — UI 组件，详见 `src/components/CLAUDE.md`
- **`src/styles/`** — 响应式样式，详见 `src/styles/CLAUDE.md`

## Integration Points

- **后端 API**：默认连接 `localhost:35179`（开发）/ 同源（生产），由 `src/config/endpoints.ts` 统一管理
- **SignalR Hub**：`/hubs/metrics`，接收系统指标推送
- **桌面端集成**：构建产物由 `XhMonitor.Desktop` 通过 35180 端口托管

## Development Guidelines

- 新增端点配置通过 `src/config/endpoints.ts` 管理，禁止硬编码端口
- 所有用户可见文本通过 `src/i18n.ts` 的 `t(key)` 国际化
- 布局状态通过 `LayoutContext` 统一操作，禁止直接操作 `localStorage`
- 构建版本号自动从 `package.json` 注入，通过 `src/version.ts` 导出
