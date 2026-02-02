# 链接验证

## 主 README 中的文档链接

### 核心组件

- [GlassPanel](docs/README.md#glasspanel---玻璃拟态面板) ✅
- [StatCard](docs/README.md#statcard---资源监控卡片) ✅

### 图表组件

- [MiniChart](docs/README.md#minichart---迷你图表引擎) ✅
- [DynamicScaler](docs/README.md#dynamicscaler---动态缩放控制器) ✅

## 验证结果

所有链接已修复，指向正确的章节标题。

### Markdown 锚点规则

GitHub Markdown 锚点生成规则：
1. 转换为小写
2. 空格替换为连字符 `-`
3. 移除特殊字符（除了连字符）
4. 中文字符保留

示例：
- `### GlassPanel - 玻璃拟态面板` → `#glasspanel---玻璃拟态面板`
- `### StatCard - 资源监控卡片` → `#statcard---资源监控卡片`

## 修复内容

**修复前**:
```markdown
| **GlassPanel** | 玻璃拟态面板容器 | [查看](docs/README.md#glasspanel) |
```

**修复后**:
```markdown
| **GlassPanel** | 玻璃拟态面板容器 | [查看](docs/README.md#glasspanel---玻璃拟态面板) |
```

---

*链接验证完成 - 2026-01-31*
