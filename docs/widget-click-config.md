# 悬浮窗指标点击配置说明

## 📋 配置文件位置

配置文件会自动生成在：
```
Service/data/widget-settings.json
```

## 🎯 配置结构

### 1. 全局开关

```json
{
  "enableMetricClick": true  // 是否启用指标点击功能（总开关）
}
```

- `true`: 启用指标点击功能
- `false`: 禁用所有指标点击（即使单个指标配置为启用也不会生效）

### 2. 指标级配置

```json
{
  "metricClickActions": {
    "power": {
      "enabled": true,              // 是否启用该指标的点击
      "action": "togglePowerMode",  // 点击时执行的动作
      "parameters": {               // 动作参数（可选）
        "modes": "balanced,performance,powersaver"
      }
    }
  }
}
```

## 📝 配置示例

### 示例 1：仅启用功耗点击

```json
{
  "enableMetricClick": true,
  "metricClickActions": {
    "cpu": { "enabled": false, "action": "none" },
    "memory": { "enabled": false, "action": "none" },
    "gpu": { "enabled": false, "action": "none" },
    "power": {
      "enabled": true,
      "action": "togglePowerMode",
      "parameters": {
        "modes": "balanced,performance,powersaver"
      }
    }
  }
}
```

### 示例 2：启用多个指标点击

```json
{
  "enableMetricClick": true,
  "metricClickActions": {
    "cpu": {
      "enabled": true,
      "action": "openTaskManager"
    },
    "power": {
      "enabled": true,
      "action": "togglePowerMode"
    },
    "gpu": {
      "enabled": true,
      "action": "openGpuSettings"
    }
  }
}
```

### 示例 3：完全禁用点击功能

```json
{
  "enableMetricClick": false,
  "metricClickActions": {}
}
```

## 🔧 支持的动作类型

| 动作类型 | 说明 | 示例 |
|---------|------|------|
| `none` | 无操作 | 默认值 |
| `togglePowerMode` | 切换功耗模式 | 在平衡/性能/省电模式间切换 |
| `openTaskManager` | 打开任务管理器 | 打开 Windows 任务管理器 |
| `openGpuSettings` | 打开 GPU 设置 | 打开 NVIDIA/AMD 控制面板 |
| `showDetails` | 显示详情 | 打开指标详情窗口 |
| `custom` | 自定义动作 | 通过 parameters 传递自定义参数 |

### ⚠️ 设备验证限制

**功耗模式切换** (`togglePowerMode`) 需要设备验证：
- 仅**星核设备**支持功耗模式切换
- 非星核设备点击功耗指标时，切换功能将被禁用
- 设备验证通过 `DeviceVerifier` 服务自动完成

## 🌐 API 接口

### 获取配置

```http
GET http://localhost:35179/api/v1/widgetconfig
```

**响应示例：**
```json
{
  "enableMetricClick": true,
  "metricClickActions": {
    "power": {
      "enabled": true,
      "action": "togglePowerMode",
      "parameters": { "modes": "balanced,performance,powersaver" }
    }
  }
}
```

### 更新完整配置

```http
POST http://localhost:35179/api/v1/widgetconfig
Content-Type: application/json

{
  "enableMetricClick": true,
  "metricClickActions": { ... }
}
```

### 更新单个指标配置

```http
POST http://localhost:35179/api/v1/widgetconfig/power
Content-Type: application/json

{
  "enabled": true,
  "action": "togglePowerMode",
  "parameters": { "modes": "balanced,performance,powersaver" }
}
```

## 💡 使用建议

1. **安全性**：默认禁用所有点击功能，用户需要主动启用
2. **渐进式启用**：先启用全局开关，再逐个启用需要的指标
3. **测试验证**：修改配置后，刷新悬浮窗查看效果
4. **备份配置**：修改前备份 `widget-settings.json` 文件

## 🎨 视觉反馈

- **启用点击**：鼠标悬浮时显示高亮背景，光标变为手型
- **禁用点击**：鼠标悬浮无反应，光标保持默认样式
- **提示文本**：启用时显示"点击执行 XX 操作"，禁用时仅显示指标名称
- **点击动画**：点击时显示视觉反馈动画效果 (v1.2 新增)

## 🔄 动态更新

配置修改后会立即生效，无需重启应用：
1. 修改 `widget-settings.json` 文件
2. 或通过 API 接口更新配置
3. 悬浮窗会自动重新加载配置
