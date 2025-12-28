# Debug 打包模式使用指南

## 问题背景
Lite 版本（不包含 .NET Runtime）运行失败，需要 Debug 模式来诊断问题。

## 已完成的修改

### 1. 日志路径修复 (Program.cs)
- **问题**: 使用相对路径 `logs/` 导致打包后日志目录找不到
- **修复**: 改用绝对路径 `AppContext.BaseDirectory/logs/`
- **效果**: 日志文件夹会在 Service.exe 所在目录正确生成

### 2. Lite 模式原生依赖修复 (publish.ps1)
- **问题**: 缺少 `-r win-x64` 导致 SQLite 原生库未复制
- **修复**: Lite 模式也指定 RID
- **效果**: e_sqlite3.dll 等原生依赖正确复制

### 3. 新增 Debug 打包模式
- **参数**: `-Debug` 开关
- **特性**:
  - 使用 Debug 配置编译（而非 Release）
  - **保留 .pdb 符号文件**用于调试
  - **显示控制台窗口**（Service 可直接看到错误）
  - 完整的异常堆栈信息

## 使用方法

### 方式 1: 使用测试脚本（推荐）
```batch
.\test-debug-build.bat
```

### 方式 2: 手动命令
```powershell
# Debug + Lite 模式
.\publish.ps1 -Lite -Debug -Version "debug-test"

# Debug + 完整模式
.\publish.ps1 -Debug -Version "debug-test"

# 仅打包 Service（跳过 Desktop）
.\publish.ps1 -Lite -Debug -SkipDesktop -Version "debug-test"
```

## 调试步骤

1. **运行 Debug 打包**
   ```batch
   .\test-debug-build.bat
   ```

2. **进入 Service 目录**
   ```batch
   cd release\XhMonitor-vdebug-test\Service
   ```

3. **直接运行 Service**
   ```batch
   XhMonitor.Service.exe
   ```
   - 会显示控制台窗口
   - 看到详细的错误信息和堆栈跟踪
   - 日志文件在 `logs\` 目录

4. **检查日志**
   ```batch
   type logs\xhmonitor-*.log
   ```

## 打包模式对比

| 模式 | 命令 | 体积 | 控制台 | 符号文件 | 用途 |
|------|------|------|--------|----------|------|
| **Release** | `.\publish.ps1` | 最大 | ❌ | ❌ | 正式发布 |
| **Release Lite** | `.\publish.ps1 -Lite` | 最小 | ❌ | ❌ | 正式发布(需.NET) |
| **Debug** | `.\publish.ps1 -Debug` | 最大 | ✅ | ✅ | 调试问题 |
| **Debug Lite** | `.\publish.ps1 -Lite -Debug` | 中等 | ✅ | ✅ | 调试问题(需.NET) |

## 常见问题排查

### 问题 1: 找不到 DLL
- **现象**: `DllNotFoundException: e_sqlite3`
- **原因**: 原生依赖未复制
- **检查**: Service 目录下是否有 `e_sqlite3.dll`

### 问题 2: 日志文件夹不产生
- **现象**: `logs/` 目录不存在
- **原因**: 已修复（AppContext.BaseDirectory）
- **验证**: 运行后检查 Service 目录

### 问题 3: 缺少 .NET Runtime
- **现象**: `You must install .NET to run this application`
- **原因**: Lite 模式需要系统安装 .NET 8
- **解决**: 安装 .NET 8 Runtime

## 修改文件清单

✅ `XhMonitor.Service\Program.cs` - 日志路径修复
✅ `publish.ps1` - Lite 模式 RID 修复 + Debug 模式
✅ `test-debug-build.bat` - 测试脚本（新增）
✅ `DEBUG_GUIDE.md` - 本文档（新增）

## 下一步

运行 `.\test-debug-build.bat`，然后将**控制台的完整错误输出**发送过来，我可以进一步诊断问题。
