//! 跨 crate 共享的错误类型。
//!
//! 边界原则：`CoreError` 只描述**领域**失败（存储、桥接、功耗后端、配置）。
//! HTTP 状态码映射留在 `xhm-service`，因为 `xhm-core` 不依赖 axum。

use std::fmt;

/// 领域层统一 Result 别名。
pub type Result<T> = std::result::Result<T, CoreError>;

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    /// SQLite 访问失败（连接、SQL、schema 迁移）。
    #[error("storage: {0}")]
    Storage(String),

    /// 期望存在的记录不存在。调用方通常映射为 HTTP 404。
    #[error("not found: {0}")]
    NotFound(String),

    /// 入参不满足契约。调用方通常映射为 HTTP 400。
    #[error("invalid argument: {0}")]
    InvalidArgument(String),

    /// LHM bridge 子进程不可用或未产出可用快照。
    #[error("lhm bridge: {0}")]
    LhmBridge(String),

    /// RyzenAdj native/CLI 两条路径都不可用。
    #[error("ryzenadj: {0}")]
    RyzenAdj(String),

    /// 当前机器/后端不支持该能力。调用方通常映射为 HTTP 404。
    #[error("unsupported: {0}")]
    Unsupported(String),

    /// 需要管理员权限但进程未提权。调用方通常映射为 HTTP 403。
    #[error("elevation required: {0}")]
    ElevationRequired(String),

    /// 配置文件缺失或格式错误。
    #[error("configuration: {0}")]
    Configuration(String),

    /// JSON 编解码失败。
    #[error("serde: {0}")]
    Serde(#[from] serde_json::Error),

    /// 文件系统 / 子进程 IO 失败。
    #[error("io: {0}")]
    Io(#[from] std::io::Error),
}

impl CoreError {
    /// 便捷构造：存储错误。
    pub fn storage(msg: impl fmt::Display) -> Self {
        CoreError::Storage(msg.to_string())
    }

    /// 便捷构造：记录不存在。
    pub fn not_found(msg: impl fmt::Display) -> Self {
        CoreError::NotFound(msg.to_string())
    }

    /// 便捷构造：入参非法。
    pub fn invalid(msg: impl fmt::Display) -> Self {
        CoreError::InvalidArgument(msg.to_string())
    }
}
