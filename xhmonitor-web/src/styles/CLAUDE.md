# xhMonitor Web Styles

## Purpose

该目录包含 xhMonitor Web 前端的全局样式定义。主要目标是提供响应式布局系统、主题变量管理、移动端适配以及跨组件共享的 CSS 工具类。

## Structure

```
styles/
└── responsive.css    # 全局响应式布局与组件样式
```

## Files

- **`responsive.css`**: 核心样式文件，包含：
  - CSS 自定义属性（变量）定义，如网格列数、间距、面板宽度
  - 应用壳层（`.xh-app-shell`）布局与背景模糊效果
  - Header 区域样式（品牌标识、状态徽章、操作按钮）
  - 统计网格（`.stats-grid`）响应式列数控制（3列/2列/1列）
  - 移动端导航栏（`.mobile-nav`）固定定位布局
  - 设置抽屉（`.settings-drawer`、`.settings-backdrop`）过渡动画
  - 响应式断点：`@media` 与 `@container` 双轨适配（768px、1200px）

## Key CSS Variables

```css
--xh-grid-columns: 3              /* 默认网格列数 */
--xh-grid-gap: 16px               /* 网格间距 */
--xh-panel-width: 320px           /* 侧边面板宽度 */
--xh-bg-blur-opacity: 0.3         /* 背景模糊透明度 */
```

## Responsive Strategy

- **Container Queries**（优先）: `@container xh-app` 基于容器宽度自适应，避免全局 viewport 污染
- **Media Queries**（兜底）: `@media (max-width: 768/1200px)` 覆盖不支持 container query 的场景
- **Body 修饰符**: `body.has-bg-image`、`body.no-gradient` 控制背景渲染模式

## Dependencies

- 被 `src/index.css` 或 `src/App.tsx` 全局导入
- 与 `src/App.css`（Tailwind/主题变量）配合使用
- 组件通过 CSS 类名引用，无直接 JS 依赖
