### 1: 上下文全量检索 (Auggie Interface)
**执行条件**：在生成任何建议或代码前。
1.  **工具调用**：调用 `mcp__auggie-mcp__codebase-retrieval`。
2.  **检索策略**：
    - 禁止基于假设（Assumption）回答。
    - 使用自然语言（NL）构建语义查询（Where/What/How）。
    - **完整性检查**：必须获取相关类、函数、变量的完整定义与签名。若上下文不足，触发递归检索。