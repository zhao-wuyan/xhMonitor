//! `xhm-core` — xhMonitor Rust 实现的共享层。
//!
//! 这个 crate 是 `xhm-service` 与 `xhm-desktop` 的公共契约层，
//! 唯一职责是**精确复刻既有 C# 版本的线上格式与领域语义**，
//! 使未修改的 React 前端与既有 SQLite 库继续可用。
//!
//! - [`models`] — 持久化实体 + REST 响应体
//! - [`wire`] — SignalR / SSE 实时推送载荷
//! - [`time`] — .NET 兼容的三种时间格式
//! - [`traits`] — 外部边界抽象（LHM / RyzenAdj / 存储 / 时钟）+ 内存 mock
//! - [`error`] — 统一错误类型
//!
//! 本 crate 不依赖 axum、tokio 或 rusqlite：HTTP 状态码映射、异步调度、
//! SQL 实现都属于 `xhm-service`。

#![deny(rust_2018_idioms)]
#![warn(missing_debug_implementations)]

pub mod error;
pub mod models;
pub mod time;
pub mod traits;
pub mod wire;

pub use error::{CoreError, Result};
