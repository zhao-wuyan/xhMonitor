# XhMonitor 发布脚本使用说明

## 概述

提供发布脚本用于生成绿色免安装版本和安装包：
- `publish.ps1` - PowerShell脚本
- `build-installer.ps1` - 构建安装包

## 使用方法

### 方法一：PowerShell脚本

```powershell
# 基本用法（默认版本 1.0.0，包含.NET运行时）
.\publish.ps1

# 指定版本号
.\publish.ps1 -Version "2.0.0"

# 轻量级模式（不包含.NET运行时，文件更小）
.\publish.ps1 -Lite

# 只发布服务端
.\publish.ps1 -SkipDesktop

# 只发布桌面端
.\publish.ps1 -SkipService

# 不压缩 ZIP
.\publish.ps1 -NoZip

# 组合使用
.\publish.ps1 -Version "2.0.1" -Lite -NoZip
```

**特点：**
- 支持参数化配置
- 彩色输出，易读性强
- 自动计算发布包大小
- 支持选择性发布
- **支持轻量级模式（-Lite）**


### 方法二：build-installer脚本（构建安装包）

```shell
.\build-installer.ps1 -Lite -Version "版本号"
```

## 发布产物

执行脚本后，将在 `release/` 目录生成以下结构：

```
release/
├─ XhMonitor-v1.0.0/
│  ├─ Service/                      # 后端服务
│  │  ├─ XhMonitor.Service.exe     # 单文件可执行程序（含.NET运行时）
│  │  └─ appsettings.json          # 配置文件
│  ├─ Desktop/                      # 桌面应用
│  │  └─ XhMonitor.Desktop.exe     # 单文件可执行程序（含.NET运行时）
│  ├─ 启动服务.bat                  # 一键启动脚本
│  ├─ 停止服务.bat                  # 一键停止脚本
│  └─ README.txt                    # 使用说明
└─ XhMonitor-v1.0.0.zip            # 压缩包（可选）
```

## 发布配置

### 发布模式
- **配置**：Release（生产优化）
- **目标平台**：win-x64
- **完整模式（默认）**：
  - 自包含 .NET 8 运行时
  - 单文件发布
  - 无需系统安装 .NET
- **轻量级模式（-Lite）**：
  - 不包含 .NET 运行时
  - 需要系统预装 .NET 8
  - 文件体积更小

### 文件大小参考
**完整模式：**
- Service: ~70-90 MB
- Desktop: ~80-100 MB
- 总计: ~150-190 MB
- ZIP压缩后: ~60-80 MB

**轻量级模式：**
- Service: ~5-10 MB
- Desktop: ~10-15 MB
- 总计: ~15-25 MB
- ZIP压缩后: ~8-12 MB

## 用户使用流程

### 完整模式（默认）

发布后，用户使用非常简单：

1. **解压** `XhMonitor-v1.0.0.zip` 到任意目录
2. **双击** `启动服务.bat` - 自动启动后端服务和桌面应用
3. **双击** `停止服务.bat` - 停止所有服务

**无需安装任何依赖！**

### 轻量级模式

发布后，用户需要：

1. **确保已安装 .NET 8 Runtime**
   - 下载地址：https://dotnet.microsoft.com/download/dotnet/8.0
   - 选择 ".NET Runtime" 或 ".NET Desktop Runtime"
2. **解压** `XhMonitor-v1.0.0.zip` 到任意目录
3. **双击** `启动服务.bat` - 自动启动后端服务和桌面应用
4. **双击** `停止服务.bat` - 停止所有服务

## 配置修改

用户可以修改 `Service\appsettings.json` 来调整：

```json
{
  "Monitor": {
    "IntervalSeconds": 3,
    "Keywords": ["关键词1", "关键词2"]
  },
  "Database": {
    "RetentionDays": 30,          // 数据保留天数
    "CleanupIntervalHours": 24    // 清理间隔
  },
  "Server": {
    "Port": 35179                 // 服务端口
  }
}
```

## 注意事项

1. **首次发布**：首次执行可能需要较长时间，因为需要下载依赖
2. **磁盘空间**：确保有足够磁盘空间（至少 500MB）
3. **杀毒软件**：可能被误报，需添加信任
4. **防火墙**：首次运行可能提示允许网络访问

## 常见问题

### Q: 发布失败怎么办？
A: 检查：
- 是否安装 .NET 8 SDK
- 是否有网络连接（下载依赖）
- 磁盘空间是否足够

### Q: 如何减小发布包大小？
A: 修改脚本，启用 PublishTrimmed（但可能影响兼容性）

### Q: 能否发布为多个平台？
A: 可以，修改 `-r win-x64` 参数为：
- `win-x86` - Windows 32位
- `linux-x64` - Linux 64位
- `osx-x64` - macOS 64位

## 版本管理建议

```cmd
# 开发版本
.\publish.ps1 -Version "1.0.0-dev"

# 测试版本
.\publish.ps1 -Version "1.0.0-beta"

# 正式版本
.\publish.ps1 -Version "1.0.0"

# 补丁版本
.\publish.ps1 -Version "1.0.1"
```

## 安装程序构建

除了绿色版，还可以构建带安装向导的安装程序（使用 Inno Setup）。

### 前置条件

安装 Inno Setup 6.x：https://jrsoftware.org/isdl.php

### 构建安装程序

```powershell
# 完整构建（发布 + 编译安装程序）
.\build-installer.ps1

# 指定版本号
.\build-installer.ps1 -Version "1.0.0"

# 跳过发布步骤（需要已有发布文件）
.\build-installer.ps1 -SkipPublish

# 查看帮助
.\build-installer.ps1 -Help
```

### 安装程序功能

- **软件名称**：星核监视器 (XhMonitor)
- **创建开始菜单快捷方式**
- **创建桌面快捷方式**（可选）
- **开机自启动**（可选）
- **完整卸载支持**（自动停止服务、清理数据库和日志）
- **中英文界面支持**

### 输出文件

```
release/
├─ XhMonitor-v1.0.0/           # 绿色版目录
├─ XhMonitor-v1.0.0.zip        # 绿色版压缩包
└─ XhMonitor-v1.0.0-Setup.exe  # 安装程序
```

## 自动化发布

可以集成到 CI/CD 流程：

```yaml
# GitHub Actions 示例
- name: Publish Release
  run: .\publish.ps1 -Version "${{ github.ref_name }}"

- name: Build Installer
  run: .\build-installer.ps1 -Version "${{ github.ref_name }}" -SkipPublish
```

---

**最后更新**: 2026-01-28
