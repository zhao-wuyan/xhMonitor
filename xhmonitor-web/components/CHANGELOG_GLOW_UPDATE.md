# 更新日志 - 微弱光晕效果

**日期**: 2026-02-02
**版本**: 1.0.1
**类型**: 设计优化

## 📝 更新内容

### 光晕效果调整

将所有玻璃拟态卡片的背景光晕效果从**适中**调整为**微弱**，提供更加低调优雅的视觉体验。

#### 具体变更

**光晕透明度 (opacity)**:
- 之前: `0.25` (25%)
- 现在: `0.1` (10%)
- 降低幅度: 60%

#### 视觉效果

- ✅ 更加微妙的背景装饰
- ✅ 减少视觉干扰，突出数据内容
- ✅ 保持玻璃拟态设计语言的一致性
- ✅ 适合长时间监控场景

## 📂 更新的文件

### 1. 核心 UI 文件
- ✅ `xhmonitor-web/design/ui-preview-v2.html`
  - 更新 `.card-glow` 样式
  - 所有 6 个监控卡片（CPU、RAM、GPU、VRAM、NET、PWR）

### 2. 组件库文件
- ✅ `xhmonitor-web/components/core/stat-card.css`
  - 更新 `.xh-stat-card__glow` 样式
  - 添加注释说明微弱效果

### 3. Playground 文件
- ✅ `xhmonitor-web/components/playground.html`
  - 更新默认状态 `glowOpacity: 0.1`
  - 更新 default 预设方案
  - 更新控件初始值

### 4. 组件库配置
- ✅ `xhmonitor-web/components/index.js`
  - 在 `tokens.effects` 中添加 `glowOpacity: 0.1`
  - 提供统一的设计令牌

## 🎨 设计理念

### 为什么选择微弱光晕？

1. **减少视觉噪音**
   - 监控面板需要长时间观看
   - 过强的光晕会分散注意力
   - 微弱效果更适合专业工具

2. **突出数据内容**
   - 光晕是装饰性元素
   - 数据和图表才是核心
   - 降低装饰强度让内容更突出

3. **保持设计一致性**
   - 玻璃拟态的核心是透明和模糊
   - 光晕是辅助元素
   - 微弱效果更符合设计原则

4. **适应不同场景**
   - 明亮环境：微弱光晕不刺眼
   - 暗光环境：仍能提供微妙的视觉层次
   - 长时间使用：减少视觉疲劳

## 🔄 迁移指南

### 如果您使用了旧版本

#### 方式 1: 使用新的组件库（推荐）

```html
<!-- 直接使用更新后的组件库 -->
<link rel="stylesheet" href="components/core/stat-card.css">
```

组件库已自动应用微弱光晕效果，无需额外配置。

#### 方式 2: 手动更新现有代码

如果您有自定义的卡片样式：

```css
/* 之前 */
.card-glow {
    opacity: 0.25;
}

/* 更新为 */
.card-glow {
    opacity: 0.1;  /* 微弱光晕效果 */
}
```

#### 方式 3: 使用 CSS 变量覆盖

```css
:root {
    --glow-opacity: 0.1;
}

.card-glow {
    opacity: var(--glow-opacity);
}
```

### 如果您想保持原来的效果

在您的自定义样式中覆盖：

```css
.xh-stat-card__glow {
    opacity: 0.25 !important;
}
```

或在 playground 中调整光晕强度滑块到 0.25。

## 🎯 预设方案对比

### Default 预设（已更新）

| 属性 | 之前 | 现在 |
|------|------|------|
| 光晕强度 | 0.25 | 0.1 |
| 视觉效果 | 适中 | 微弱 |

### 其他预设保持不变

- **Subtle** (轻盈): 0.15 - 保持不变
- **Bold** (大胆): 0.4 - 保持不变
- **Minimal** (极简): 0.1 - 与新默认值一致

## 📊 效果对比

### 视觉强度

```
之前 (0.25): ████████░░ (40% 可见度)
现在 (0.1):  ███░░░░░░░ (16% 可见度)
```

### 适用场景

| 场景 | 之前 (0.25) | 现在 (0.1) |
|------|-------------|------------|
| 演示展示 | ✅ 适合 | ⚠️ 可能偏淡 |
| 日常监控 | ⚠️ 略强 | ✅ 最佳 |
| 长时间使用 | ⚠️ 可能疲劳 | ✅ 舒适 |
| 专业工具 | ⚠️ 略花哨 | ✅ 专业 |

## 🔧 Playground 体验

打开 `components/playground.html` 可以：

1. **查看新的默认效果** - 光晕强度默认为 0.1
2. **实时调整对比** - 拖动"光晕强度"滑块对比不同值
3. **切换预设方案** - 体验不同风格的光晕效果
4. **复制配置提示** - 生成对应的 CSS 代码

## 📝 技术细节

### CSS 实现

```css
/* 微弱光晕效果 */
.xh-stat-card__glow {
  position: absolute;
  left: -20px;
  top: -20px;
  width: 100px;
  height: 100px;
  border-radius: 50%;
  filter: blur(40px);        /* 模糊半径保持不变 */
  opacity: 0.1;              /* 透明度从 0.25 降至 0.1 */
  pointer-events: none;
}
```

### 设计令牌

```javascript
// components/index.js
export const tokens = {
  effects: {
    glowOpacity: 0.1,        // 新增：光晕透明度
    glowBlur: '40px'         // 新增：光晕模糊
  }
};
```

## 🎨 设计系统更新

### 新增设计令牌

在 `tokens.effects` 中新增：

```javascript
effects: {
  glowOpacity: 0.1,    // 光晕透明度
  glowBlur: '40px'     // 光晕模糊半径
}
```

### 使用方式

```javascript
import { tokens } from '@xhmonitor/components';

// 获取光晕配置
const glowOpacity = tokens.effects.glowOpacity;  // 0.1
const glowBlur = tokens.effects.glowBlur;        // '40px'
```

## 🐛 已知问题

无已知问题。

## 🔮 未来计划

- [ ] 添加更多光晕预设（柔和、标准、强烈）
- [ ] 支持动态光晕（根据数值变化调整强度）
- [ ] 提供主题切换（亮色模式下的光晕效果）

## 📞 反馈

如果您对新的光晕效果有任何建议或问题，欢迎：

1. 在 playground 中调整到您喜欢的值
2. 复制底部生成的提示文本
3. 提供反馈或创建 Issue

---

**更新完成时间**: 2026-02-02 13:30
**影响范围**: 所有使用玻璃拟态卡片的组件
**向后兼容**: ✅ 是（可通过 CSS 覆盖恢复旧效果）
