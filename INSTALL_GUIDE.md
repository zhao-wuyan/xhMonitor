# XhMonitor 安装指南

## 版本说明

XhMonitor 提供两种发布版本：

| 版本类型 | 文件大小 | 依赖要求 | 适用场景 |
|---------|---------|---------|---------|
| **完整版** | 150-190 MB | 无需安装任何依赖 | 推荐给普通用户 |
| **轻量级版** | 15-25 MB | 需要安装 .NET 8 Desktop Runtime | 适合已有 .NET 环境的用户 |

### 如何判断版本类型

查看 `Desktop\XhMonitor.Desktop.exe` 文件大小：
- **完整版**：约 80-100 MB
- **轻量级版**：约 10-15 MB

---

## 完整版安装（推荐）

### 系统要求
- Windows 10/11 x64
- 无需安装任何依赖

### 安装步骤
1. 解压 ZIP 文件到任意目录
2. 双击 `启动服务.bat` 启动应用
3. 完成！

---

## 轻量级版安装

### 系统要求
- Windows 10/11 x64
- .NET 8 Desktop Runtime

### 安装步骤

#### 第一步：安装 .NET 8 Desktop Runtime

1. **访问官方下载页**
   ```
   https://dotnet.microsoft.com/download/dotnet/8.0
   ```

2. **找到正确的下载项**
   - 在页面中找到 **".NET Desktop Runtime 8.0.x"** 部分
   - **不要下载**：
     - ❌ .NET Runtime（基础版，不包含 WPF 支持）
     - ❌ .NET SDK（开发工具包，体积大且不必要）
     - ❌ ASP.NET Core Runtime（仅用于 Web 服务）

3. **下载安装包**
   - 选择 **Windows x64** 版本
   - 文件名类似：`windowsdesktop-runtime-8.0.x-win-x64.exe`
   - 文件大小：约 55 MB

4. **安装**
   - 双击下载的安装包
   - 按照向导完成安装
   - **重启电脑**（重要！）

5. **验证安装**
   - 打开命令提示符（Win + R，输入 `cmd`）
   - 运行命令：
     ```cmd
     dotnet --list-runtimes
     ```
   - 应该看到类似输出：
     ```
     Microsoft.NETCore.App 8.0.x [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]
     Microsoft.WindowsDesktop.App 8.0.x [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
     ```
   - **关键**：必须包含 `Microsoft.WindowsDesktop.App 8.0.x`

#### 第二步：运行 XhMonitor

1. 解压 ZIP 文件到任意目录
2. 双击 `启动服务.bat` 启动应用
3. 完成！

---

## 常见问题

### Q1: 提示缺少 .NET 8，但我已经安装了？

**原因**：可能安装的是基础版 Runtime，而非 Desktop Runtime。

**解决方案**：
1. 运行 `dotnet --list-runtimes` 检查
2. 如果只看到 `Microsoft.NETCore.App`，没有 `Microsoft.WindowsDesktop.App`
3. 重新按照上述步骤安装 **Desktop Runtime**

### Q2: 安装 Desktop Runtime 后还是提示缺少？

**原因**：环境变量未生效。

**解决方案**：
1. 重启电脑
2. 如果还是不行，尝试重新安装 Desktop Runtime

### Q3: 下载链接打不开或下载很慢？

**解决方案**：
1. 使用国内镜像（如果可用）
2. 或者联系项目维护者获取离线安装包

### Q4: 我应该选择哪个版本？

**建议**：
- **普通用户**：选择完整版，开箱即用
- **开发者/已有 .NET 环境**：选择轻量级版，节省空间

### Q5: 完整版也提示缺少 .NET？

**可能原因**：
1. 杀毒软件拦截了运行时组件
2. 文件损坏或解压不完整
3. 缺少 VC++ 运行库

**解决方案**：
1. 将 XhMonitor 目录添加到杀毒软件白名单
2. 重新解压 ZIP 文件
3. 安装 Visual C++ Redistributable 2015-2022：
   ```
   https://aka.ms/vs/17/release/vc_redist.x64.exe
   ```

---

## 技术支持

如果遇到其他问题，请访问：
- GitHub Issues: https://github.com/zhao-wuyan/xhMonitor/issues
- 提供以下信息：
  - Windows 版本
  - XhMonitor 版本（完整版/轻量级版）
  - `dotnet --list-runtimes` 的输出
  - 错误截图或日志
