//! Service 客户端装配根：REST + SSE（TASK-003/TASK-004）。
//!
//! 这一层只消费 xhm-service 的既有 REST/SSE 契约，不创建新 API、不修改 service。
//! HTTP transport 在 [`rest`] 内由 `reqwest::Client` 承载；测试通过 wiremock 注入 URL。

pub mod rest;
pub mod sse;

pub use sse::{SseControl, SseMessage, SseStream, SseStreamBuilder, SseSubscription};
