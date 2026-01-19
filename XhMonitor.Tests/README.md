# XhMonitor.Tests

xUnit 测试项目,用于测试 XhMonitor.Core 的进程名称解析功能。

## 测试覆盖

### ProcessNameResolverTests
测试 `ProcessNameResolver` 服务的核心功能:
- 两级匹配逻辑 (ProcessName + Keywords)
- 两种提取器 (Regex 和 Direct)
- 匹配优先级 (有关键字的规则优先)
- 大小写不敏感匹配
- 失败处理和回退逻辑
- 线程安全性
- 正则表达式缓存

### ExtractorTests
测试两种提取器的具体实现:
- **RegexExtractor**: 正则表达式提取,支持捕获组和格式化
- **DirectExtractor**: 直接返回配置的 DisplayName

### CommandLineFixtures
包含 10+ 真实命令行示例的测试数据:
- Python + FastAPI/Django/ComfyUI
- Chrome (Direct 类型)
- 边缘情况 (无匹配规则、大小写、多关键字)

## 运行测试

### 运行所有测试
```bash
dotnet test
```

### 运行特定测试类
```bash
dotnet test --filter "FullyQualifiedName~ProcessNameResolverTests"
```

### 运行特定测试方法
```bash
dotnet test --filter "FullyQualifiedName~Resolve_WithKeywordMatch_ShouldPrioritizeKeywordRule"
```

### 生成代码覆盖率报告
```bash
dotnet test --collect:"XPlat Code Coverage"
```

覆盖率报告将生成在 `TestResults/` 目录下。

## 测试统计

- **总测试数**: 41
- **测试固件**: 10+ 场景
- **覆盖的提取器**: 2 种 (Regex, Direct)
- **覆盖的匹配逻辑**: 两级匹配 (ProcessName + Keywords)

## 依赖项

- xUnit 2.6.2
- FluentAssertions 6.12.0
- Moq 4.20.70
- Microsoft.Extensions.Configuration 8.0.0
