# 局域网访问安全功能实现文档

## 📋 功能概述

本次实现为 XhMonitor 添加了完整的局域网访问安全功能，包括：
- ✅ 本机IP地址显示
- ✅ IP白名单配置（支持单IP和CIDR格式）
- ✅ 访问密钥认证（可选）
- ✅ 安全中间件（IP验证 + 密钥验证）
- ✅ Windows防火墙自动配置
- ✅ 反向代理架构（Desktop作为代理层）

---

## 🏗️ 架构设计

### 原架构
```
局域网设备 ❌ 无法访问
    ↓
Desktop (localhost:35180) → 静态文件服务器
Service (localhost:35179) → API服务（仅localhost）
```

### 新架构（反向代理 + 安全层）
```
局域网设备 ✅ 可访问
    ↓
Desktop Web服务器 (0.0.0.0:35180 或 localhost:35180)
    ├─ 安全中间件（IP白名单 + 访问密钥验证）
    ├─ /api/* → 代理到 localhost:35179/api/*
    ├─ /hubs/* → 代理到 localhost:35179/hubs/* (SignalR)
    └─ /* → 静态文件 (wwwroot)
         ↓
Service (localhost:35179) - 保持localhost监听（安全）
```

### 核心优势

| 维度 | 说明 |
|------|------|
| **安全性** | Service保持localhost监听，不直接暴露到局域网 |
| **访问控制** | Desktop层实现IP白名单和密钥验证 |
| **向后兼容** | 默认关闭局域网访问，不影响现有功能 |
| **易于管理** | 通过设置页一键开关，无需修改配置文件 |
| **防火墙自动化** | 自动配置Windows防火墙规则，防止重复添加 |

---

## 📁 修改的文件

### 1. 核心配置文件

#### `XhMonitor.Core/Configuration/ConfigurationDefaults.cs`
**修改内容**：
- 添加 `EnableAccessKey` 常量（默认 false）
- 添加 `AccessKey` 常量（默认空字符串）
- 添加 `IpWhitelist` 常量（默认空字符串）
- 添加对应的键名常量

**关键代码**：
```csharp
public const bool EnableAccessKey = false;
public const string AccessKey = "";
public const string IpWhitelist = "";
```

---

### 2. ViewModel层

#### `XhMonitor.Desktop/ViewModels/SettingsViewModel.cs`
**修改内容**：
- 添加 `EnableAccessKey`, `AccessKey`, `IpWhitelist`, `LocalIpAddress` 属性
- 实现 `LoadLocalIpAddress()` 方法获取本机IPv4地址
- 在 `LoadSettingsAsync()` 中加载安全配置
- 在 `SaveSettingsAsync()` 中保存安全配置

**关键代码**：
```csharp
private void LoadLocalIpAddress()
{
    var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
    var localIp = host.AddressList
        .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                           && !System.Net.IPAddress.IsLoopback(ip));
    LocalIpAddress = localIp?.ToString() ?? "未检测到";
}
```

---

### 3. UI层

#### `XhMonitor.Desktop/Windows/SettingsWindow.xaml`
**修改内容**：
- 添加本机IP地址显示（绿色高亮）
- 添加"局域网安全设置"卡片
- 添加"启用访问密钥"开关
- 添加访问密钥输入框（支持自动生成）
- 添加IP白名单多行文本框（支持CIDR格式）

**UI结构**：
```xml
<!-- 本机IP地址显示 -->
<Border Background="#1E2A1E">
    <TextBlock Text="本机IP: " />
    <TextBlock Text="{Binding LocalIpAddress}" Foreground="#81C784" />
</Border>

<!-- 局域网安全设置卡片 -->
<Border Style="{StaticResource SettingsCard}">
    <!-- 访问密钥开关 -->
    <CheckBox IsChecked="{Binding EnableAccessKey}" />

    <!-- 访问密钥输入 -->
    <TextBox Text="{Binding AccessKey}" IsEnabled="{Binding EnableAccessKey}" />

    <!-- IP白名单 -->
    <TextBox Text="{Binding IpWhitelist}" AcceptsReturn="True" />
</Border>
```

---

#### `XhMonitor.Desktop/Windows/SettingsWindow.xaml.cs`
**修改内容**：
- 添加 `GetOriginalLanAccessAsync()` 方法检测配置变更
- 在 `Save_Click()` 中添加防火墙配置逻辑
- 配置变更时提示重启应用

**关键代码**：
```csharp
// 配置防火墙规则（如果局域网访问设置变更）
if (lanAccessChanged)
{
    var firewallResult = await FirewallManager.ConfigureFirewallAsync(
        _viewModel.EnableLanAccess,
        35180);

    if (!firewallResult.Success)
    {
        // 提示用户防火墙配置失败
    }
}
```

---

### 4. 服务层

#### `XhMonitor.Desktop/Services/WebServerService.cs`
**修改内容**：
- 添加安全配置读取逻辑 `GetSecurityConfigAsync()`
- 实现IP白名单验证 `IsIpAllowed()`
- 实现CIDR格式匹配 `IsIpInCidr()`
- 实现访问密钥生成 `GenerateAccessKey()`
- 添加安全中间件（IP白名单 + 访问密钥验证）

**关键代码**：
```csharp
// 安全中间件
if (securityConfig.EnableLanAccess)
{
    app.Use(async (context, next) =>
    {
        // IP白名单检查
        if (!string.IsNullOrWhiteSpace(securityConfig.IpWhitelist))
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "";
            if (!IsIpAllowed(clientIp, securityConfig.IpWhitelist))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Access denied: IP not in whitelist");
                return;
            }
        }

        // 访问密钥验证
        if (securityConfig.EnableAccessKey && !string.IsNullOrWhiteSpace(securityConfig.AccessKey))
        {
            var providedKey = context.Request.Headers["X-Access-Key"].ToString();
            if (providedKey != securityConfig.AccessKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Access denied: Invalid access key");
                return;
            }
        }

        await next();
    });
}
```

**CIDR匹配算法**：
```csharp
private static bool IsIpInCidr(string ipAddress, string cidr)
{
    var parts = cidr.Split('/');
    var networkAddress = IPAddress.Parse(parts[0]);
    var prefixLength = int.Parse(parts[1]);
    var clientAddress = IPAddress.Parse(ipAddress);

    // 计算子网掩码
    var maskBytes = new byte[networkBytes.Length];
    for (int i = 0; i < maskBytes.Length; i++)
    {
        var bitsInByte = Math.Min(8, prefixLength - (i * 8));
        if (bitsInByte <= 0)
            maskBytes[i] = 0;
        else if (bitsInByte >= 8)
            maskBytes[i] = 0xFF;
        else
            maskBytes[i] = (byte)(0xFF << (8 - bitsInByte));
    }

    // 比较网络地址
    for (int i = 0; i < networkBytes.Length; i++)
    {
        if ((networkBytes[i] & maskBytes[i]) != (clientBytes[i] & maskBytes[i]))
            return false;
    }

    return true;
}
```

---

#### `XhMonitor.Desktop/Services/FirewallManager.cs` ⭐ **新建文件**
**功能**：
- 自动配置Windows防火墙规则
- 检测规则是否已存在（防止重复添加）
- 支持规则创建、更新、删除
- 使用 `netsh advfirewall` 命令

**关键方法**：
```csharp
public static async Task<(bool Success, string Message)> ConfigureFirewallAsync(bool enableLanAccess, int port)
{
    if (enableLanAccess)
    {
        var exists = await CheckRuleExistsAsync();
        if (exists)
            return await UpdateFirewallRuleAsync(port);
        else
            return await CreateFirewallRuleAsync(port);
    }
    else
    {
        var exists = await CheckRuleExistsAsync();
        if (exists)
            return await DeleteFirewallRuleAsync();
        return (true, "无需配置防火墙");
    }
}
```

**防火墙规则配置**：
```bash
netsh advfirewall firewall add rule name="XhMonitor Web Access" \
    description="Allow inbound connections to XhMonitor web interface" \
    dir=in action=allow protocol=TCP localport=35180 \
    profile=private,domain
```

---

### 5. 项目文件

#### `XhMonitor.Desktop/XhMonitor.Desktop.csproj`
**修改内容**：
- 添加 `Yarp.ReverseProxy` NuGet包（版本 2.*）

**关键代码**：
```xml
<PackageReference Include="Yarp.ReverseProxy" Version="2.*" />
```

---

## 🔒 安全机制详解

### 1. IP白名单验证

**支持格式**：
- 单个IP地址：`192.168.1.100`
- CIDR格式：`192.168.1.0/24`
- 多个IP（逗号或换行分隔）：
  ```
  192.168.1.100
  192.168.1.200
  192.168.1.0/24
  ```

**验证流程**：
```
客户端请求 → 提取客户端IP → 检查白名单
    ├─ 白名单为空 → 允许访问
    ├─ IP在白名单 → 允许访问
    └─ IP不在白名单 → 403 Forbidden
```

---

### 2. 访问密钥认证

**密钥生成**：
- 使用 `RandomNumberGenerator` 生成32字节随机数
- Base64编码后移除特殊字符（+, /, =）
- 截取前32位作为密钥

**验证流程**：
```
客户端请求 → 提取 X-Access-Key 头 → 验证密钥
    ├─ 密钥正确 → 允许访问
    └─ 密钥错误 → 401 Unauthorized
```

**使用方式**：
```bash
# 浏览器扩展（如ModHeader）
X-Access-Key: MySecretKey123

# curl命令
curl -H "X-Access-Key: MySecretKey123" http://192.168.1.100:35180
```

---

### 3. 防火墙自动配置

**规则名称**：`XhMonitor Web Access`

**配置时机**：
- 启用局域网访问时：自动创建规则
- 禁用局域网访问时：自动删除规则
- 端口变更时：自动更新规则

**防重复逻辑**：
```csharp
var exists = await CheckRuleExistsAsync();
if (exists)
{
    // 更新现有规则（先删除再创建）
    await UpdateFirewallRuleAsync(port);
}
else
{
    // 创建新规则
    await CreateFirewallRuleAsync(port);
}
```

---

## 🧪 测试场景

### 场景1：基础局域网访问（无安全限制）
```
配置：
✅ 启用局域网访问
❌ 启用访问密钥
IP白名单：（留空）

预期：局域网内所有设备可自由访问
```

### 场景2：IP白名单限制
```
配置：
✅ 启用局域网访问
❌ 启用访问密钥
IP白名单：192.168.1.50, 192.168.1.100

预期：
- 192.168.1.50 → ✅ 成功
- 192.168.1.100 → ✅ 成功
- 192.168.1.200 → ❌ 403 Forbidden
```

### 场景3：CIDR格式白名单
```
配置：
✅ 启用局域网访问
❌ 启用访问密钥
IP白名单：192.168.1.0/24

预期：
- 192.168.1.1~254 → ✅ 成功
- 192.168.2.100 → ❌ 403 Forbidden
```

### 场景4：访问密钥认证
```
配置：
✅ 启用局域网访问
✅ 启用访问密钥
访问密钥：MySecretKey123
IP白名单：（留空）

预期：
- 无密钥访问 → ❌ 401 Unauthorized
- 错误密钥 → ❌ 401 Unauthorized
- 正确密钥 → ✅ 成功
```

### 场景5：组合安全策略
```
配置：
✅ 启用局域网访问
✅ 启用访问密钥
访问密钥：SecureKey456
IP白名单：192.168.1.0/24

预期：
- 192.168.1.50 + 无密钥 → ❌ 401 Unauthorized
- 192.168.1.50 + 正确密钥 → ✅ 成功
- 192.168.2.100 + 正确密钥 → ❌ 403 Forbidden
```

---

## 📊 安全性评估

### 当前安全措施

| 安全层 | 实现 | 防护能力 |
|--------|------|----------|
| **网络隔离** | Service保持localhost监听 | 🟢 高 - Service不直接暴露 |
| **IP白名单** | 支持单IP和CIDR | 🟢 高 - 限制访问来源 |
| **访问密钥** | HTTP头验证 | 🟡 中 - 明文传输（HTTP） |
| **防火墙** | 自动配置Windows防火墙 | 🟢 高 - 系统级防护 |
| **代理层控制** | Desktop作为反向代理 | 🟢 高 - 集中访问控制 |

### 已知限制

| 限制 | 影响 | 建议 |
|------|------|------|
| HTTP明文传输 | 密钥可被嗅探 | 生产环境建议使用HTTPS |
| 无速率限制 | 可能被暴力破解 | 添加速率限制中间件 |
| 无审计日志 | 无法追踪访问记录 | 添加访问日志记录 |
| 密钥存储在数据库 | 数据库泄露风险 | 考虑加密存储 |

---

## 🚀 使用指南

### 1. 启用局域网访问

**步骤**：
1. 打开设置页 → 系统选项
2. 查看"本机IP"（例如：`192.168.1.100`）
3. 开启"启用局域网访问"
4. 点击"保存"
5. 确认防火墙配置提示
6. 选择"是"重启应用

**访问地址**：
- 本机：`http://localhost:35180`
- 局域网：`http://192.168.1.100:35180`

---

### 2. 配置IP白名单

**步骤**：
1. 在"局域网安全设置"中找到"IP白名单"
2. 输入允许的IP地址（每行一个）：
   ```
   192.168.1.50
   192.168.1.100
   192.168.1.0/24
   ```
3. 点击"保存"并重启

**格式说明**：
- 单个IP：`192.168.1.100`
- CIDR段：`192.168.1.0/24`（表示 192.168.1.1~254）
- 多个IP：逗号或换行分隔
- 留空：不限制IP访问

---

### 3. 启用访问密钥

**步骤**：
1. 开启"启用访问密钥"
2. 输入自定义密钥（或留空自动生成）
3. 点击"保存"并重启
4. 记录生成的密钥

**使用密钥访问**：

**方法1：浏览器扩展（推荐）**
1. 安装Chrome扩展：ModHeader
2. 添加请求头：
   - Name: `X-Access-Key`
   - Value: `你的密钥`
3. 访问 `http://192.168.1.100:35180`

**方法2：curl命令**
```bash
curl -H "X-Access-Key: 你的密钥" http://192.168.1.100:35180
```

---

### 4. 验证防火墙规则

**检查规则**：
```powershell
# 以管理员身份运行PowerShell
netsh advfirewall firewall show rule name="XhMonitor Web Access"
```

**预期输出**：
```
规则名称:                             XhMonitor Web Access
----------------------------------------------------------------------
已启用:                               是
方向:                                 入站
配置文件:                             域,专用
本地端口:                             35180
协议:                                 TCP
操作:                                 允许
```

**手动删除规则**（如需要）：
```powershell
netsh advfirewall firewall delete rule name="XhMonitor Web Access"
```

---

## 🔧 故障排查

### 问题1：局域网无法访问

**可能原因**：
1. 防火墙规则未创建
2. 路由器隔离了设备
3. IP地址错误

**解决方案**：
```powershell
# 1. 检查防火墙规则
netsh advfirewall firewall show rule name="XhMonitor Web Access"

# 2. 手动创建规则
netsh advfirewall firewall add rule name="XhMonitor Web Access" dir=in action=allow protocol=TCP localport=35180

# 3. 验证本机IP
ipconfig | findstr "IPv4"
```

---

### 问题2：访问密钥验证失败

**可能原因**：
1. 密钥输入错误
2. HTTP头未正确设置
3. 密钥包含特殊字符

**解决方案**：
1. 检查密钥是否完全匹配（区分大小写）
2. 确认HTTP头名称为 `X-Access-Key`
3. 使用curl测试：
   ```bash
   curl -v -H "X-Access-Key: 你的密钥" http://192.168.1.100:35180
   ```

---

### 问题3：IP白名单不生效

**可能原因**：
1. CIDR格式错误
2. IP地址格式错误
3. 配置未保存

**解决方案**：
1. 验证CIDR格式：`192.168.1.0/24`（不是 `192.168.1.0-24`）
2. 确认IP地址格式：`192.168.1.100`（不是 `192.168.001.100`）
3. 重新保存配置并重启应用

---

## 📝 配置示例

### 场景1：家庭网络（信任环境）
```
✅ 启用局域网访问
❌ 启用访问密钥
IP白名单：（留空）

适用：家庭局域网，所有设备可信
```

### 场景2：办公网络（半信任环境）
```
✅ 启用局域网访问
✅ 启用访问密钥
访问密钥：MyOfficeKey2024
IP白名单：192.168.10.0/24

适用：办公室局域网，限制特定子网+密钥保护
```

### 场景3：公共网络（不信任环境）
```
❌ 启用局域网访问

适用：公共WiFi，完全禁用局域网访问
```

---

## 🎯 后续增强建议

### 1. HTTPS支持
```csharp
builder.WebHost.UseKestrel(options => {
    options.ListenAnyIP(35180, listenOptions => {
        listenOptions.UseHttps("certificate.pfx", "password");
    });
});
```

### 2. 速率限制
```csharp
app.UseRateLimiter(options => {
    options.AddFixedWindowLimiter("api", opt => {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 100;
    });
});
```

### 3. 访问日志
```csharp
app.Use(async (context, next) => {
    var ip = context.Connection.RemoteIpAddress;
    var path = context.Request.Path;
    Debug.WriteLine($"[{DateTime.Now}] {ip} -> {path}");
    await next();
});
```

### 4. 密钥加密存储
```csharp
// 使用Windows DPAPI加密
var encryptedKey = ProtectedData.Protect(
    Encoding.UTF8.GetBytes(accessKey),
    null,
    DataProtectionScope.CurrentUser);
```

---

## ✅ 实现清单

- [x] 显示本机IP地址
- [x] IP白名单配置（单IP + CIDR）
- [x] 访问密钥功能（可选）
- [x] 安全中间件（IP验证 + 密钥验证）
- [x] 防火墙自动配置
- [x] 防重复添加防火墙规则
- [x] UI增强（安全设置卡片）
- [x] 配置变更检测
- [x] 自动重启提示
- [x] 编译通过验证

---

## 📚 参考资料

- [YARP官方文档](https://microsoft.github.io/reverse-proxy/)
- [ASP.NET Core中间件](https://docs.microsoft.com/aspnet/core/fundamentals/middleware/)
- [Windows防火墙netsh命令](https://docs.microsoft.com/windows-server/networking/technologies/netsh/netsh-contexts)
- [CIDR表示法](https://en.wikipedia.org/wiki/Classless_Inter-Domain_Routing)

---

**实现日期**：2026-02-04
**版本**：v1.0
**状态**：✅ 已完成并通过编译
