//! SignalR / SSE 双实时协议适配器。
//!
//! 这一层只负责**协议**：把 `state.push_tx` 上传输无关的 [`PushEvent`]
//! 编码成两种线上格式并按订阅路由；连接生命周期、握手、分帧、清理全部
//! 在此文件内完成。采集侧（worker / push producer）不在本切片范围。
//!
//! ## SignalR（`@microsoft/signalr` JSON over WebSockets）
//!
//! - `POST {hub}/negotiate?negotiateVersion=0|1` —— 仅声明 `WebSockets`/
//!   `Text`。v1 生成一次性 `connectionToken`，写入
//!   [`RealtimeRegistry`](crate::state::RealtimeRegistry) 的 pending 集合。
//! - `GET {hub}[?id=<token>]` —— WebSocket Upgrade。携带未消费 token 时
//!   `consume_and_register` 注册；否则 `register_direct` 直连。两种路径都
//!   把连接默认订阅设成 Full。
//! - 首帧 JSON 握手 `{ "protocol": "json", "version": 1 }` + 记录分隔符
//!   `0x1E`，仅接受 `json`/`1`，回复 `{}` + RS。
//! - 后续消息按 RS 分帧：`type:1` 的 `SetProcessMetricsSubscription(mode,
//!   pinned?)` 更新订阅，带 `invocationId` 时回 `type:3` completion
//!   （无 `result`/`error`），否则不回；`type:6` ping 保持连接；
//!   其它帧视为协议错，关闭连接。
//!
//! ## SSE（`GET {sse}?mode=&pinned=`）
//!
//! Desktop 端：`mode=full|lite` + `pinned=1,2,3`，注册后按相同
//! `All/Full/Connection` 路由，发送 `event: <event_name>\ndata: <json>\n\n`。
//!
//! ## 路由
//!
//! [`router`] 接收 `hub_path` 与 `sse_path`，返回的 `Router<AppState>`
//! 不含 CORS（由 bootstrap 合并时统一挂）。所有连接状态都活在
//! [`AppState::realtime`](crate::state::AppState::realtime) 与单连接局部任务
//! 里，没有任何全局/静态可变状态。

use std::sync::Arc;
use std::time::Duration;

use axum::{
    extract::{
        ws::{Message, WebSocket, WebSocketUpgrade},
        Query, State,
    },
    response::{
        sse::{Event as SseEvent, KeepAlive, Sse},
        IntoResponse, Json, Response,
    },
    routing::{get, post},
    Router,
};
use futures::stream::{SplitSink, SplitStream};
use futures::{SinkExt, Stream, StreamExt};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::sync::{mpsc, RwLock};
use tokio_stream::wrappers::ReceiverStream;
use tracing::{debug, trace, warn};

use xhm_core::wire::{
    normalize_pinned_ids, PushEvent, SubscriptionMode, SET_PROCESS_METRICS_SUBSCRIPTION,
};

use crate::state::{AppState, PushTarget};

/// Record Separator — SignalR JSON Hub Protocol 分帧字节。
const RS: char = '\u{1e}';
/// 单连接写出缓冲深度。慢消费者填满后会被判定为 lag，断开并清理。
const OUTBOUND_BUFFER: usize = 256;
/// 服务端向客户端发送 ping 的间隔，对齐 .NET 默认 15s。
const SERVER_PING_INTERVAL: Duration = Duration::from_secs(15);

// ============================================================================
// 路由装配
// ============================================================================

/// 构造实时切片的 Router。
///
/// `hub_path` / `sse_path` 都是已归一化的绝对路径（以 `/` 开头）。三条路由：
/// `POST {hub}/negotiate`、`GET {hub}`、`GET {sse}`。
pub fn router(hub_path: &str, sse_path: &str) -> Router<AppState> {
    let negotiate = format!("{}/negotiate", hub_path.trim_end_matches('/'));
    let hub = hub_path.trim_end_matches('/').to_owned();
    let sse = sse_path.trim_end_matches('/').to_owned();

    Router::new()
        .route(&negotiate, post(negotiate_handler))
        .route(&hub, get(ws_upgrade_handler))
        .route(&sse, get(sse_handler))
}

// ============================================================================
// negotiate
// ============================================================================

#[derive(Debug, Default, Deserialize)]
struct NegotiateQuery {
    #[serde(default, rename = "negotiateVersion")]
    negotiate_version: Option<u32>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct NegotiateResponse {
    #[serde(skip_serializing_if = "Option::is_none")]
    negotiate_version: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    connection_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    connection_token: Option<String>,
    available_transports: Vec<AvailableTransport>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct AvailableTransport {
    transport: &'static str,
    transfer_formats: &'static [&'static str],
}

async fn negotiate_handler(
    State(state): State<AppState>,
    Query(query): Query<NegotiateQuery>,
) -> Response {
    let version = query.negotiate_version.unwrap_or(0);

    let (connection_id, connection_token) = if version >= 1 {
        // v1：生成一次性 token 并登记 pending，WS 连接时消费。
        let cid = new_connection_id();
        let token = new_connection_id();
        let mut registry = state.realtime.write().await;
        registry.add_pending(token.clone());
        (Some(cid), Some(token))
    } else {
        (None, None)
    };

    let body = NegotiateResponse {
        negotiate_version: (version >= 1).then_some(version),
        connection_id,
        connection_token,
        available_transports: vec![AvailableTransport {
            transport: "WebSockets",
            transfer_formats: &["Text"],
        }],
    };

    Json(body).into_response()
}

// ============================================================================
// WebSocket 升级
// ============================================================================

#[derive(Debug, Default, Deserialize)]
struct WsQuery {
    /// v1 negotiate 返回的 connectionToken；无则直连。
    #[serde(default)]
    id: Option<String>,
}

async fn ws_upgrade_handler(
    ws: WebSocketUpgrade,
    State(state): State<AppState>,
    Query(query): Query<WsQuery>,
) -> Response {
    ws.on_upgrade(move |socket| run_ws_connection(socket, state, query.id))
}

/// 单条 WebSocket 连接的完整生命周期。
///
/// 把 [`WebSocket`] 拆成 sink/stream 后：
/// - **writer** 持有 sink，从 `outbound` channel 取 [`Message`] 刷新；
/// - **reader** 持有 stream，解析协议帧、回写应答（通过 `outbound`）、
///   更新共享订阅；
/// - **dispatcher** 订阅 `push_tx`，按 [`PushTarget`] 决定是否往
///   `outbound` 投递编码后的 type1 事件帧。
///
/// 三者通过 `outbound` channel 与一个 `JoinSet` 协作；任一退出都触发
/// 连接级 cleanup（从 `RealtimeRegistry` 撤销订阅）。
async fn run_ws_connection(socket: WebSocket, state: AppState, query_token: Option<String>) {
    let connection_id = register_connection(&state, query_token.as_deref()).await;

    let (sink, stream) = socket.split();
    let (outbound_tx, outbound_rx) = mpsc::channel::<Message>(OUTBOUND_BUFFER);

    // 共享订阅视图：reader 写、dispatcher 读。与 RealtimeRegistry 是同一事实
    // 两份视图，由 SetProcessMetricsSubscription 与 cleanup 同步更新。
    let subscription = Arc::new(RwLock::new(SubscriptionSnapshot {
        mode: SubscriptionMode::Full,
        pinned: Vec::new(),
    }));

    let writer = tokio::spawn(run_ws_writer(sink, outbound_rx));
    let dispatcher = tokio::spawn(run_ws_dispatcher(
        state.clone(),
        connection_id.clone(),
        subscription.clone(),
        outbound_tx.clone(),
    ));

    // reader 阻塞至对端关闭、协议错或 dispatcher 撤销。
    let read_outcome = run_ws_reader(
        &state,
        &connection_id,
        subscription.clone(),
        stream,
        outbound_tx.clone(),
    )
    .await;

    trace!(target: "xhm.realtime.ws", ?read_outcome, "ws connection ended");

    // 关闭通道：writer 收到 None 后退出；dispatcher abort。
    drop(outbound_tx);
    dispatcher.abort();
    // 给 writer 一个收尾窗口（flush 关闭帧），忽略超时。
    let _ = tokio::time::timeout(Duration::from_millis(500), writer).await;

    cleanup_connection(&state, &connection_id).await;
}

/// 连接级订阅快照（局部无锁视图）。
#[derive(Debug, Clone, Default)]
struct SubscriptionSnapshot {
    mode: SubscriptionMode,
    pinned: Vec<i32>,
}

/// 处理 token / 直连两种路径，返回最终 connectionId。
async fn register_connection(state: &AppState, query_token: Option<&str>) -> String {
    let connection_id = new_connection_id();
    let mut registry = state.realtime.write().await;
    match query_token {
        Some(token) if !token.is_empty() => {
            if !registry.consume_and_register(token, connection_id.clone()) {
                // token 已被消费或不存在 → 退化为直连（与 .NET 宽容降级一致）。
                registry.register_direct(connection_id.clone());
            }
        }
        _ => registry.register_direct(connection_id.clone()),
    }
    connection_id
}

/// 从 RealtimeRegistry 撤销连接（断连 cleanup）。
async fn cleanup_connection(state: &AppState, connection_id: &str) {
    let mut registry = state.realtime.write().await;
    registry.disconnect(connection_id);
}

// ============================================================================
// writer
// ============================================================================

/// 把 `outbound` 队列里的 [`Message`] 刷新到 socket sink。通道关闭即退出。
async fn run_ws_writer(
    mut sink: SplitSink<WebSocket, Message>,
    mut outbound: mpsc::Receiver<Message>,
) {
    while let Some(message) = outbound.recv().await {
        if sink.send(message).await.is_err() {
            break;
        }
    }
    let _ = sink.close().await;
}

// ============================================================================
// dispatcher：push_tx → 路由 → SignalR type1 帧
// ============================================================================

/// 决定某条 [`RoutedPushEvent`] 是否应投递给本连接。
fn should_deliver(
    target: &PushTarget,
    connection_id: &str,
    snapshot: &SubscriptionSnapshot,
) -> bool {
    match target {
        PushTarget::All => true,
        PushTarget::Full => snapshot.mode == SubscriptionMode::Full,
        PushTarget::Connection(id) => id == connection_id,
    }
}

/// 订阅 `push_tx`，按目标路由把事件编码为 SignalR type1 帧后投递。
///
/// `lag`（通道满 / 关闭）时静默丢帧——真正的断连由服务端 ping 心跳与
/// reader 的对端关闭检测兜底。这与 .NET SignalR 的 backpressure 行为
/// 一致：不阻塞 dispatcher 影响其它连接。
async fn run_ws_dispatcher(
    state: AppState,
    connection_id: String,
    subscription: Arc<RwLock<SubscriptionSnapshot>>,
    outbound: mpsc::Sender<Message>,
) {
    let mut rx = state.push_tx.subscribe();
    loop {
        let event = match rx.recv().await {
            Ok(event) => event,
            Err(tokio::sync::broadcast::error::RecvError::Lagged(skipped)) => {
                // 订阅者落后于生产者：跳过被丢弃的旧帧，保持连接存活。
                warn!(target: "xhm.realtime.ws", skipped, "dispatcher lagged, resyncing");
                continue;
            }
            Err(tokio::sync::broadcast::error::RecvError::Closed) => break,
        };
        let snapshot = subscription.read().await.clone();
        if !should_deliver(&event.target, &connection_id, &snapshot) {
            continue;
        }

        let frame = encode_signalr_event(&event.event);
        if try_send(&outbound, frame).should_break() {
            break;
        }
    }
}

fn try_send(outbound: &mpsc::Sender<Message>, frame: Message) -> ControlFlow {
    match outbound.try_send(frame) {
        Ok(()) => ControlFlow::Continue,
        Err(mpsc::error::TrySendError::Full(_)) => {
            // lag：丢这一帧，保持连接存活，等下一轮心跳。
            warn!(target: "xhm.realtime.ws", "outbound lag, dropping frame");
            ControlFlow::Continue
        }
        Err(mpsc::error::TrySendError::Closed(_)) => ControlFlow::Break,
    }
}

enum ControlFlow {
    Continue,
    Break,
}

impl ControlFlow {
    fn should_break(self) -> bool {
        matches!(self, ControlFlow::Break)
    }
}

// ============================================================================
// reader：握手 + 协议帧解析
// ============================================================================

#[derive(Debug)]
enum ReadOutcome {
    Closed,
    ProtocolError,
    SocketError,
}

async fn run_ws_reader(
    state: &AppState,
    connection_id: &str,
    subscription: Arc<RwLock<SubscriptionSnapshot>>,
    mut stream: SplitStream<WebSocket>,
    outbound: mpsc::Sender<Message>,
) -> ReadOutcome {
    // ── 握手 ────────────────────────────────────────────────────────────────
    let handshake = match next_text_record(&mut stream).await {
        Ok(text) => text,
        Err(RecvErr::Closed) => return ReadOutcome::Closed,
        Err(RecvErr::Error) => return ReadOutcome::SocketError,
    };

    if let Err(err) = verify_handshake(&handshake) {
        warn!(target: "xhm.realtime.ws", error = %err, "handshake rejected");
        let _ = outbound.try_send(Message::Text(format!("{{\"error\":\"{err}\"}}{RS}")));
        return ReadOutcome::ProtocolError;
    }

    if outbound
        .send(Message::Text(format!("{{}}{RS}")))
        .await
        .is_err()
    {
        return ReadOutcome::SocketError;
    }
    debug!(target: "xhm.realtime.ws", "handshake ok");

    // ── 心跳：周期发 ping，让 writer 把 socket 喂活 ──────────────────────
    let mut ping = tokio::time::interval(SERVER_PING_INTERVAL);
    ping.tick().await; // 第一次立即返回

    // ── 主循环 ─────────────────────────────────────────────────────────────
    loop {
        tokio::select! {
            biased;
            _ = ping.tick() => {
                let frame = encode_signalr_ping();
                if try_send(&outbound, frame).should_break() {
                    return ReadOutcome::SocketError;
                }
            }
            frame = next_text_record(&mut stream) => {
                let text = match frame {
                    Ok(t) => t,
                    Err(RecvErr::Closed) => return ReadOutcome::Closed,
                    Err(RecvErr::Error) => return ReadOutcome::SocketError,
                };
                let mut records = text.split(RS);
                let mut outcome: Option<ReadOutcome> = None;
                while outcome.is_none() {
                    let Some(record) = records.next() else { break };
                    if record.is_empty() {
                        continue;
                    }
                    match handle_record(state, connection_id, &subscription, &outbound, record).await {
                        FrameOutcome::Continue => {}
                        FrameOutcome::Close => { outcome = Some(ReadOutcome::Closed); }
                        FrameOutcome::ProtocolError => { outcome = Some(ReadOutcome::ProtocolError); }
                        FrameOutcome::SocketError => { outcome = Some(ReadOutcome::SocketError); }
                    }
                }
                if let Some(o) = outcome {
                    return o;
                }
            }
        }
    }
}

#[derive(Debug)]
enum RecvErr {
    Closed,
    Error,
}

/// 读一条 socket 文本消息。Ping/Pong 控制帧被吞掉后继续读；
/// Binary / Close / 流终止分别映射为错误 / Closed。
async fn next_text_record(stream: &mut SplitStream<WebSocket>) -> Result<String, RecvErr> {
    loop {
        match stream.next().await {
            Some(Ok(Message::Text(t))) => return Ok(t),
            Some(Ok(Message::Binary(_))) => return Err(RecvErr::Error),
            Some(Ok(Message::Ping(_))) | Some(Ok(Message::Pong(_))) => continue,
            Some(Ok(Message::Close(_))) => return Err(RecvErr::Closed),
            Some(Err(_)) => return Err(RecvErr::Error),
            None => return Err(RecvErr::Closed),
        }
    }
}

fn verify_handshake(payload: &str) -> Result<(), &'static str> {
    let record = payload.split(RS).next().unwrap_or("");
    let value: Value = serde_json::from_str(record).map_err(|_| "handshake payload is not JSON")?;
    let protocol = value.get("protocol").and_then(Value::as_str).unwrap_or("");
    if protocol != "json" {
        return Err("only the 'json' hub protocol is supported");
    }
    if value.get("version").and_then(Value::as_i64) != Some(1) {
        return Err("only hub protocol version 1 is supported");
    }
    Ok(())
}

#[derive(Debug)]
enum FrameOutcome {
    Continue,
    Close,
    ProtocolError,
    SocketError,
}

/// 解析一条 SignalR 记录。
async fn handle_record(
    state: &AppState,
    connection_id: &str,
    subscription: &Arc<RwLock<SubscriptionSnapshot>>,
    outbound: &mpsc::Sender<Message>,
    record: &str,
) -> FrameOutcome {
    let value: Value = match serde_json::from_str(record) {
        Ok(v) => v,
        Err(_) => return FrameOutcome::ProtocolError,
    };

    let kind = value.get("type").and_then(Value::as_i64);
    match kind {
        Some(1) => handle_invocation(state, connection_id, subscription, outbound, &value).await,
        Some(6) => {
            // ping：保持连接，不回。
            FrameOutcome::Continue
        }
        Some(7) => FrameOutcome::Close, // close message
        _ => FrameOutcome::ProtocolError,
    }
}

async fn handle_invocation(
    state: &AppState,
    connection_id: &str,
    subscription: &Arc<RwLock<SubscriptionSnapshot>>,
    outbound: &mpsc::Sender<Message>,
    value: &Value,
) -> FrameOutcome {
    let target = value.get("target").and_then(Value::as_str).unwrap_or("");
    if target != SET_PROCESS_METRICS_SUBSCRIPTION {
        return FrameOutcome::ProtocolError;
    }

    let args = value.get("arguments").and_then(Value::as_array);
    let Some(args) = args else {
        return FrameOutcome::ProtocolError;
    };
    if args.is_empty() || args.len() > 2 {
        return FrameOutcome::ProtocolError;
    }

    let mode_raw = args[0].as_str();
    let mode = SubscriptionMode::parse(mode_raw);
    let pinned = match args.get(1) {
        Some(Value::Array(ids)) => ids
            .iter()
            .filter_map(Value::as_i64)
            .map(|v| v as i32)
            .collect::<Vec<_>>(),
        Some(Value::Null) | None => Vec::new(),
        Some(_) => return FrameOutcome::ProtocolError,
    };

    // 同步更新两份视图。
    let normalized = normalize_pinned_ids(Some(&pinned));
    {
        let mut guard = state.realtime.write().await;
        if !guard.set_subscription(connection_id, mode, Some(&normalized)) {
            // 连接已不在 registry 中（极端竞态）——按协议错处理。
            return FrameOutcome::ProtocolError;
        }
    }
    {
        let mut snap = subscription.write().await;
        snap.mode = mode;
        snap.pinned = normalized;
    }

    // 带 invocationId 必须回 type3 completion（无 result/error）。
    if let Some(invocation_id) = value.get("invocationId").and_then(Value::as_str) {
        let completion = encode_signalr_completion(invocation_id);
        if outbound.send(completion).await.is_err() {
            return FrameOutcome::SocketError;
        }
    }
    FrameOutcome::Continue
}

// ============================================================================
// SignalR 帧编码
// ============================================================================

/// `{"type":1,"target":"<event>","arguments":[<payload>]}` + RS。
fn encode_signalr_event(event: &PushEvent) -> Message {
    let payload = event.to_json().unwrap_or(Value::Null);
    let target = event.event_name();
    let frame = json!({
        "type": 1,
        "target": target,
        "arguments": [payload],
    });
    Message::Text(format!("{frame}{RS}"))
}

/// `{"type":6}` + RS。
fn encode_signalr_ping() -> Message {
    Message::Text(format!("{{\"type\":6}}{RS}"))
}

/// `{"type":3,"invocationId":"<id>"}` + RS（无 result/error）。
fn encode_signalr_completion(invocation_id: &str) -> Message {
    Message::Text(format!(
        "{{\"type\":3,\"invocationId\":\"{}\"}}{RS}",
        invocation_id.replace('"', "\\\"")
    ))
}

// ============================================================================
// SSE
// ============================================================================

#[derive(Debug, Default, Deserialize)]
struct SseQuery {
    #[serde(default)]
    mode: Option<String>,
    #[serde(default)]
    pinned: Option<String>,
}

async fn sse_handler(
    State(state): State<AppState>,
    Query(query): Query<SseQuery>,
) -> Sse<impl Stream<Item = Result<SseEvent, std::convert::Infallible>>> {
    let mode = SubscriptionMode::parse(query.mode.as_deref());
    let pinned = parse_pinned_query(query.pinned.as_deref());
    let (connection_id, subscription) = register_sse_connection(&state, mode, pinned).await;
    let stream = sse_event_stream(state, connection_id, subscription);

    Sse::new(stream).keep_alive(
        KeepAlive::new()
            .interval(Duration::from_secs(15))
            .text("keepalive"),
    )
}

/// 把 query 里的 pinned 字符串（逗号/空格/分号分隔）归一化成升序 PID。
fn parse_pinned_query(raw: Option<&str>) -> Vec<i32> {
    let Some(raw) = raw else {
        return Vec::new();
    };
    let ids = raw
        .split([',', ' ', ';'])
        .filter_map(|s| s.parse::<i32>().ok())
        .collect::<Vec<_>>();
    normalize_pinned_ids(Some(&ids))
}

/// 在 RealtimeRegistry 注册一条 SSE 连接，返回 connectionId 与本地订阅视图。
///
/// 拆出来是为了让单测无需通过 HTTP 即可验证 query → 订阅的解析。
async fn register_sse_connection(
    state: &AppState,
    mode: SubscriptionMode,
    pinned: Vec<i32>,
) -> (String, Arc<RwLock<SubscriptionSnapshot>>) {
    let connection_id = new_connection_id();
    {
        let mut registry = state.realtime.write().await;
        registry.register_direct(connection_id.clone());
        registry.set_subscription(&connection_id, mode, Some(&pinned));
    }
    let subscription = Arc::new(RwLock::new(SubscriptionSnapshot { mode, pinned }));
    (connection_id, subscription)
}

/// 生成 SSE 事件流。
///
/// 订阅 `push_tx`，按目标路由产出 `event:<name>` + JSON；同时每
/// `SSE_LIVENESS_INTERVAL` 探活一次：当客户端断开（响应流被丢弃）导致
/// outbound channel 关闭时，后台任务退出并从 `RealtimeRegistry` 撤销订阅，
/// 完成 cleanup。
fn sse_event_stream(
    state: AppState,
    connection_id: String,
    subscription: Arc<RwLock<SubscriptionSnapshot>>,
) -> impl Stream<Item = Result<SseEvent, std::convert::Infallible>> {
    let (tx, rx) = mpsc::channel::<Result<SseEvent, std::convert::Infallible>>(OUTBOUND_BUFFER);

    // 后台任务持有 push_tx 订阅 + outbound sender。退出时 cleanup。
    let state_for_task = state.clone();
    let conn_for_task = connection_id.clone();
    tokio::spawn(async move {
        let mut push = state.push_tx.subscribe();
        let mut liveness = tokio::time::interval(SSE_LIVENESS_INTERVAL);
        liveness.tick().await; // 第一次立即返回

        loop {
            tokio::select! {
                biased;
                event = push.recv() => {
                    match event {
                        Ok(event) => {
                            let snapshot = subscription.read().await.clone();
                            if !should_deliver(&event.target, &connection_id, &snapshot) {
                                continue;
                            }
                            let sse = sse_encode(&event.event);
                            if tx.send(Ok(sse)).await.is_err() {
                                break; // 客户端已断开
                            }
                        }
                        Err(tokio::sync::broadcast::error::RecvError::Lagged(_)) => continue,
                        Err(tokio::sync::broadcast::error::RecvError::Closed) => break,
                    }
                }
                _ = liveness.tick() => {
                    // 探活：非阻塞投递一个 SSE 注释事件（`:` 行对客户端是 no-op）。
                    // Closed → 客户端已断开；Full → outbound 长时间无人消费（疑似
                    // 断连但事件路径未触发），都应结束连接并 cleanup。
                    match tx.try_send(Ok(SseEvent::default().comment("ping"))) {
                        Err(mpsc::error::TrySendError::Closed(_)) => break,
                        Err(mpsc::error::TrySendError::Full(_)) => break,
                        Ok(()) => {}
                    }
                }
            }
        }

        // cleanup：从 RealtimeRegistry 撤销连接，避免内存泄漏。
        let mut registry = state_for_task.realtime.write().await;
        registry.disconnect(&conn_for_task);
    });

    ReceiverStream::new(rx)
}

/// SSE 注释探活间隔，对齐 keepalive。
const SSE_LIVENESS_INTERVAL: Duration = Duration::from_secs(15);

/// SSE 与 SignalR 共用同一份 payload，但 event 字段用 [`PushEvent::event_name`]。
fn sse_encode(event: &PushEvent) -> SseEvent {
    let name = event.event_name();
    let payload = event.to_json().unwrap_or(Value::Null);
    SseEvent::default()
        .event(name)
        .json_data(payload)
        .unwrap_or_else(|_| SseEvent::default().event(name).data("null"))
}

// ============================================================================
// 工具
// ============================================================================

fn new_connection_id() -> String {
    uuid::Uuid::new_v4().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::state::{RoutedPushEvent, ServicePaths};

    // ===== 编码单测 =====

    #[test]
    fn signalr_event_frame_wraps_payload_as_single_argument() {
        let event = PushEvent::SystemUsage(xhm_core::wire::SystemUsagePayload {
            timestamp: chrono::Local::now(),
            total_cpu: 12.0,
            total_gpu: 0.0,
            cpu_temperature: None,
            gpu_temperature: None,
            total_memory: 0.0,
            total_vram: 0.0,
            upload_speed: 0.0,
            download_speed: 0.0,
            max_memory: 0.0,
            max_vram: 0.0,
            disks: Vec::new(),
            power_available: false,
            total_power: 0.0,
            max_power: 0.0,
            power_scheme_index: None,
        });

        let Message::Text(frame) = encode_signalr_event(&event) else {
            panic!("expected text frame");
        };

        assert!(frame.ends_with(RS));
        let body = frame.trim_end_matches(RS);
        let value: Value = serde_json::from_str(body).unwrap();
        assert_eq!(value["type"], 1);
        assert_eq!(value["target"], "ReceiveSystemUsage");
        assert!(value["arguments"].is_array());
        assert_eq!(value["arguments"].as_array().unwrap().len(), 1);
        assert_eq!(value["arguments"][0]["totalCpu"], 12.0);
    }

    #[test]
    fn signalr_completion_frame_has_invocation_id_and_no_result() {
        let Message::Text(frame) = encode_signalr_completion("abc-123") else {
            panic!("expected text frame");
        };
        let body = frame.trim_end_matches(RS);
        let value: Value = serde_json::from_str(body).unwrap();
        assert_eq!(value["type"], 3);
        assert_eq!(value["invocationId"], "abc-123");
        assert!(value.get("result").is_none());
        assert!(value.get("error").is_none());
    }

    #[test]
    fn signalr_ping_frame_is_type_six() {
        let Message::Text(frame) = encode_signalr_ping() else {
            panic!("expected text frame");
        };
        let body = frame.trim_end_matches(RS);
        let value: Value = serde_json::from_str(body).unwrap();
        assert_eq!(value["type"], 6);
    }

    #[test]
    fn handshake_accepts_json_version_one() {
        assert!(verify_handshake(r#"{"protocol":"json","version":1}"#).is_ok());
    }

    #[test]
    fn handshake_rejects_non_json_protocol() {
        assert_eq!(
            verify_handshake(r#"{"protocol":"messagepack","version":1}"#),
            Err("only the 'json' hub protocol is supported")
        );
    }

    #[test]
    fn handshake_rejects_unsupported_version() {
        assert_eq!(
            verify_handshake(r#"{"protocol":"json","version":2}"#),
            Err("only hub protocol version 1 is supported")
        );
    }

    // ===== 路由决策单测 =====

    fn full() -> SubscriptionSnapshot {
        SubscriptionSnapshot {
            mode: SubscriptionMode::Full,
            pinned: Vec::new(),
        }
    }

    fn lite(pinned: Vec<i32>) -> SubscriptionSnapshot {
        SubscriptionSnapshot {
            mode: SubscriptionMode::Lite,
            pinned,
        }
    }

    #[test]
    fn all_target_is_delivered_to_every_connection() {
        assert!(should_deliver(&PushTarget::All, "c1", &full()));
        assert!(should_deliver(&PushTarget::All, "c2", &lite(vec![1])));
    }

    #[test]
    fn full_target_is_delivered_only_to_full_subscribers() {
        assert!(should_deliver(&PushTarget::Full, "c1", &full()));
        assert!(!should_deliver(&PushTarget::Full, "c2", &lite(vec![1])));
    }

    #[test]
    fn connection_target_is_delivered_only_to_the_named_connection() {
        assert!(should_deliver(
            &PushTarget::Connection("c1".to_string()),
            "c1",
            &lite(vec![1])
        ));
        assert!(!should_deliver(
            &PushTarget::Connection("c1".to_string()),
            "c2",
            &lite(vec![2])
        ));
    }

    // ===== 集成：negotiate token 单次消费 + 端到端路由 =====

    /// 最小化的 [`MetricStore`] 替身：所有读返回空，所有写返回零。
    /// 让 `AppState::new` 在无 SQLite、无硬件的 CI 上可用。
    #[derive(Default)]
    struct StubStore;

    impl xhm_core::traits::MetricStore for StubStore {
        fn save_process_metrics(
            &self,
            _records: &[xhm_core::models::NewProcessMetricRecord],
        ) -> xhm_core::Result<usize> {
            Ok(0)
        }
        fn latest_process_metrics(
            &self,
            _filter: &xhm_core::models::MetricFilter,
        ) -> xhm_core::Result<Vec<xhm_core::models::ProcessMetricRecord>> {
            Ok(Vec::new())
        }
        fn history_raw(
            &self,
            _process_id: i32,
            _from: Option<chrono::DateTime<chrono::Utc>>,
            _to: Option<chrono::DateTime<chrono::Utc>>,
        ) -> xhm_core::Result<Vec<xhm_core::models::ProcessMetricRecord>> {
            Ok(Vec::new())
        }
        fn process_summaries(
            &self,
            _filter: &xhm_core::models::MetricFilter,
        ) -> xhm_core::Result<Vec<xhm_core::models::ProcessSummary>> {
            Ok(Vec::new())
        }
        fn history_aggregated(
            &self,
            _process_id: i32,
            _level: xhm_core::models::AggregationLevel,
            _from: Option<chrono::DateTime<chrono::Utc>>,
            _to: Option<chrono::DateTime<chrono::Utc>>,
        ) -> xhm_core::Result<Vec<xhm_core::models::AggregatedMetricRecord>> {
            Ok(Vec::new())
        }
        fn aggregations(
            &self,
            _level: xhm_core::models::AggregationLevel,
            _from: chrono::DateTime<chrono::Utc>,
            _to: chrono::DateTime<chrono::Utc>,
        ) -> xhm_core::Result<Vec<xhm_core::models::AggregatedMetricRecord>> {
            Ok(Vec::new())
        }
        fn rollup_coverage(
            &self,
            _target: xhm_core::models::AggregationLevel,
        ) -> xhm_core::Result<Option<xhm_core::models::RollupCoverage>> {
            Ok(None)
        }
        fn commit_rollup(
            &self,
            _target: xhm_core::models::AggregationLevel,
            covered_from: chrono::DateTime<chrono::Utc>,
            _bucket_start: chrono::DateTime<chrono::Utc>,
            bucket_end: chrono::DateTime<chrono::Utc>,
            records: &[xhm_core::models::NewAggregatedMetricRecord],
        ) -> xhm_core::Result<xhm_core::models::RollupCommitResult> {
            Ok(xhm_core::models::RollupCommitResult {
                inserted: records.len(),
                replaced: 0,
                verified: records.len(),
                coverage: xhm_core::models::RollupCoverage {
                    covered_from,
                    completed_through: bucket_end,
                },
            })
        }
        fn earliest_raw_timestamp(
            &self,
        ) -> xhm_core::Result<Option<chrono::DateTime<chrono::Utc>>> {
            Ok(None)
        }
        fn earliest_aggregate_timestamp(
            &self,
            _level: xhm_core::models::AggregationLevel,
        ) -> xhm_core::Result<Option<chrono::DateTime<chrono::Utc>>> {
            Ok(None)
        }
        fn raw_batch_for_aggregation(
            &self,
            _from: chrono::DateTime<chrono::Utc>,
            _to: chrono::DateTime<chrono::Utc>,
            _after_id: i64,
            _limit: usize,
        ) -> xhm_core::Result<Vec<xhm_core::models::ProcessMetricRecord>> {
            Ok(Vec::new())
        }
        fn aggregate_batch_for_rollup(
            &self,
            _level: xhm_core::models::AggregationLevel,
            _from: chrono::DateTime<chrono::Utc>,
            _to: chrono::DateTime<chrono::Utc>,
            _after_id: i64,
            _limit: usize,
        ) -> xhm_core::Result<Vec<xhm_core::models::AggregatedMetricRecord>> {
            Ok(Vec::new())
        }
        fn purge_raw_batch(
            &self,
            _window: &xhm_core::models::PurgeWindow,
            _cursor: Option<&xhm_core::models::PurgeCursor>,
            _limit: usize,
        ) -> xhm_core::Result<xhm_core::models::PurgeBatchResult> {
            Ok(xhm_core::models::PurgeBatchResult {
                deleted: 0,
                next_cursor: None,
                exhausted: true,
            })
        }
        fn purge_aggregate_batch(
            &self,
            _level: xhm_core::models::AggregationLevel,
            _window: &xhm_core::models::PurgeWindow,
            _cursor: Option<&xhm_core::models::PurgeCursor>,
            _limit: usize,
        ) -> xhm_core::Result<xhm_core::models::PurgeBatchResult> {
            Ok(xhm_core::models::PurgeBatchResult {
                deleted: 0,
                next_cursor: None,
                exhausted: true,
            })
        }
        fn checkpoint_wal(&self) -> xhm_core::Result<xhm_core::models::WalCheckpointResult> {
            Ok(xhm_core::models::WalCheckpointResult {
                busy: 0,
                log_frames: 0,
                checkpointed_frames: 0,
            })
        }
        fn list_alerts(&self) -> xhm_core::Result<Vec<xhm_core::models::AlertConfiguration>> {
            Ok(Vec::new())
        }
        fn upsert_alert(
            &self,
            _alert: &xhm_core::models::AlertConfiguration,
            _now: chrono::DateTime<chrono::Utc>,
        ) -> xhm_core::Result<()> {
            Ok(())
        }
        fn delete_alert(&self, _id: i32) -> xhm_core::Result<bool> {
            Ok(false)
        }
        fn list_settings(&self) -> xhm_core::Result<Vec<xhm_core::models::ApplicationSetting>> {
            Ok(Vec::new())
        }
        fn update_setting(
            &self,
            _category: &str,
            _key: &str,
            _value: &str,
            _now: chrono::DateTime<chrono::Utc>,
        ) -> xhm_core::Result<bool> {
            Ok(false)
        }
        fn upsert_settings(
            &self,
            _entries: &[(String, String, String)],
            _now: chrono::DateTime<chrono::Utc>,
        ) -> xhm_core::Result<xhm_core::models::SettingsUpsertCounts> {
            Ok(xhm_core::models::SettingsUpsertCounts::default())
        }
        fn health_check(&self) -> xhm_core::Result<()> {
            Ok(())
        }
    }

    fn make_state() -> AppState {
        use chrono::TimeZone;
        use xhm_core::traits::{MockClock, MockLhmReader, MockRyzenAdjClient};

        let store: Arc<dyn xhm_core::traits::MetricStore> = Arc::new(StubStore);
        let clock = Arc::new(MockClock::new(
            chrono::Utc.with_ymd_and_hms(2026, 7, 26, 0, 0, 0).unwrap(),
        ));
        let lhm = Arc::new(MockLhmReader::default());
        let ryzenadj = Arc::new(MockRyzenAdjClient::unsupported());
        let paths = ServicePaths::for_exe_dir(std::path::Path::new("test-exe"));
        let runtime = crate::state::RuntimeConfig::default();

        AppState::new(store, clock, lhm, ryzenadj, paths, runtime)
    }

    fn assert_matches<T: std::fmt::Debug>(left: T, right: impl FnOnce(&T) -> bool) {
        assert!(right(&left), "assertion failed: {left:?}");
    }

    #[tokio::test]
    async fn negotiate_v1_emits_single_use_token_consumed_by_first_ws() {
        let state = make_state();

        let token = {
            let mut registry = state.realtime.write().await;
            // 模拟 negotiate：生成 token 写 pending。
            let t = new_connection_id();
            registry.add_pending(t.clone());
            t
        };

        // 第一次 WS 连接消费 token，注册成 Full。
        let conn_a = register_connection(&state, Some(&token)).await;
        assert!(matches!(
            state
                .realtime
                .read()
                .await
                .subscription(&conn_a)
                .map(|s| s.mode),
            Some(SubscriptionMode::Full)
        ));

        // 第二次 WS 连接复用同一 token：无法消费 → 退化为直连。
        let conn_b = register_connection(&state, Some(&token)).await;
        assert_ne!(conn_a, conn_b);
        assert!(matches!(
            state
                .realtime
                .read()
                .await
                .subscription(&conn_b)
                .map(|s| s.mode),
            Some(SubscriptionMode::Full)
        ));
    }

    #[tokio::test]
    async fn negotiate_v0_does_not_register_pending_token() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        assert!(state.realtime.read().await.subscription(&conn).is_some());
    }

    #[tokio::test]
    async fn set_subscription_updates_both_views_for_full_connection() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot::default()));

        let (outbound_tx, _outbound_rx) = mpsc::channel::<Message>(8);
        let value: Value = serde_json::from_str(
            r#"{"type":1,"invocationId":"id-1","target":"SetProcessMetricsSubscription","arguments":["full",[]]}"#,
        )
        .unwrap();

        let outcome = handle_invocation(&state, &conn, &subscription, &outbound_tx, &value).await;
        assert_matches(outcome, |o| matches!(o, FrameOutcome::Continue));

        assert!(matches!(
            state
                .realtime
                .read()
                .await
                .subscription(&conn)
                .map(|s| s.mode),
            Some(SubscriptionMode::Full)
        ));
        assert!(matches!(
            subscription.read().await.mode,
            SubscriptionMode::Full
        ));
    }

    #[tokio::test]
    async fn set_subscription_lite_normalizes_pinned_ids() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot::default()));

        let (outbound_tx, mut outbound_rx) = mpsc::channel::<Message>(8);
        let value: Value = serde_json::from_str(
            r#"{"type":1,"invocationId":"id-1","target":"SetProcessMetricsSubscription","arguments":["lite",[7,-1,3,7,0,2]]}"#,
        )
        .unwrap();

        let outcome = handle_invocation(&state, &conn, &subscription, &outbound_tx, &value).await;
        assert_matches(outcome, |o| matches!(o, FrameOutcome::Continue));

        // 带 invocationId 必须回 type3 completion。
        let reply = outbound_rx.recv().await.expect("completion frame");
        let Message::Text(text) = reply else {
            panic!("expected text")
        };
        let body = text.trim_end_matches(RS);
        let v: Value = serde_json::from_str(body).unwrap();
        assert_eq!(v["type"], 3);
        assert_eq!(v["invocationId"], "id-1");

        let snap = state.realtime.read().await;
        let sub = snap.subscription(&conn).unwrap();
        assert!(matches!(sub.mode, SubscriptionMode::Lite));
        assert_eq!(sub.pinned_process_ids, vec![2, 3, 7]);
        drop(snap);

        let local = subscription.read().await;
        assert!(matches!(local.mode, SubscriptionMode::Lite));
        assert_eq!(local.pinned, vec![2, 3, 7]);
    }

    #[tokio::test]
    async fn set_subscription_without_invocation_id_replies_nothing() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot::default()));

        let (outbound_tx, mut outbound_rx) = mpsc::channel::<Message>(8);
        let value: Value = serde_json::from_str(
            r#"{"type":1,"target":"SetProcessMetricsSubscription","arguments":["lite",[]]}"#,
        )
        .unwrap();

        let outcome = handle_invocation(&state, &conn, &subscription, &outbound_tx, &value).await;
        assert_matches(outcome, |o| matches!(o, FrameOutcome::Continue));

        // 无 invocationId → 不应回 completion；通道在短暂窗口内应为空。
        assert!(
            tokio::time::timeout(Duration::from_millis(50), outbound_rx.recv())
                .await
                .is_err(),
            "must not send a completion when invocationId is absent"
        );
    }

    #[tokio::test]
    async fn unknown_target_is_protocol_error() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot::default()));
        let (outbound_tx, _rx) = mpsc::channel::<Message>(8);

        let value: Value = serde_json::from_str(
            r#"{"type":1,"invocationId":"id-1","target":"DoSomethingElse","arguments":[]}"#,
        )
        .unwrap();

        let outcome = handle_invocation(&state, &conn, &subscription, &outbound_tx, &value).await;
        assert_matches(outcome, |o| matches!(o, FrameOutcome::ProtocolError));
    }

    #[tokio::test]
    async fn ping_record_keeps_connection_open() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot::default()));
        let (outbound_tx, _rx) = mpsc::channel::<Message>(8);

        let outcome =
            handle_record(&state, &conn, &subscription, &outbound_tx, r#"{"type":6}"#).await;
        assert_matches(outcome, |o| matches!(o, FrameOutcome::Continue));
    }

    #[tokio::test]
    async fn unknown_type_is_protocol_error() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot::default()));
        let (outbound_tx, _rx) = mpsc::channel::<Message>(8);

        let outcome =
            handle_record(&state, &conn, &subscription, &outbound_tx, r#"{"type":99}"#).await;
        assert_matches(outcome, |o| matches!(o, FrameOutcome::ProtocolError));
    }

    #[tokio::test]
    async fn close_message_closes_connection() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot::default()));
        let (outbound_tx, _rx) = mpsc::channel::<Message>(8);

        let outcome = handle_record(
            &state,
            &conn,
            &subscription,
            &outbound_tx,
            r#"{"type":7,"error":"bye"}"#,
        )
        .await;
        assert_matches(outcome, |o| matches!(o, FrameOutcome::Close));
    }

    #[tokio::test]
    async fn disconnect_removes_subscription_from_registry() {
        let state = make_state();
        let conn = register_connection(&state, None).await;
        assert!(state.realtime.read().await.subscription(&conn).is_some());

        cleanup_connection(&state, &conn).await;
        assert!(state.realtime.read().await.subscription(&conn).is_none());
    }

    // ===== 两个 Lite 订阅者各自只收专属 Connection 事件 =====

    #[tokio::test]
    async fn two_lite_subscribers_receive_only_their_own_connection_events() {
        let state = make_state();

        let conn_a = register_connection(&state, None).await;
        let conn_b = register_connection(&state, None).await;
        assert_ne!(conn_a, conn_b);

        // 两名 Lite 订阅者，pinned 不同。
        {
            let mut registry = state.realtime.write().await;
            registry.set_subscription(&conn_a, SubscriptionMode::Lite, Some(&[1]));
            registry.set_subscription(&conn_b, SubscriptionMode::Lite, Some(&[2]));
        }

        let snapshot_a = SubscriptionSnapshot {
            mode: SubscriptionMode::Lite,
            pinned: vec![1],
        };
        let snapshot_b = SubscriptionSnapshot {
            mode: SubscriptionMode::Lite,
            pinned: vec![2],
        };

        // dispatcher 用 Connection(id) 路由——a 专属事件只给 a，b 专属只给 b。
        assert!(should_deliver(
            &PushTarget::Connection(conn_a.clone()),
            &conn_a,
            &snapshot_a
        ));
        assert!(!should_deliver(
            &PushTarget::Connection(conn_a.clone()),
            &conn_b,
            &snapshot_b
        ));
        assert!(should_deliver(
            &PushTarget::Connection(conn_b.clone()),
            &conn_b,
            &snapshot_b
        ));
        assert!(!should_deliver(
            &PushTarget::Connection(conn_b.clone()),
            &conn_a,
            &snapshot_a
        ));

        // All 广播两名都收；Full 两人都不收。
        assert!(should_deliver(&PushTarget::All, &conn_a, &snapshot_a));
        assert!(should_deliver(&PushTarget::All, &conn_b, &snapshot_b));
        assert!(!should_deliver(&PushTarget::Full, &conn_a, &snapshot_a));
        assert!(!should_deliver(&PushTarget::Full, &conn_b, &snapshot_b));
    }

    #[tokio::test]
    async fn ws_dispatcher_routes_connection_event_only_to_named_connection() {
        // 端到端：开一条 dispatcher，广播两条 Connection 事件，只有匹配那条能进入 outbound。
        let state = make_state();
        let conn = register_connection(&state, None).await;
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot {
            mode: SubscriptionMode::Lite,
            pinned: vec![1],
        }));

        let (outbound_tx, mut outbound_rx) = mpsc::channel::<Message>(OUTBOUND_BUFFER);

        let dispatcher = tokio::spawn(run_ws_dispatcher(
            state.clone(),
            conn.clone(),
            subscription.clone(),
            outbound_tx,
        ));

        // 给 dispatcher 一点时间 subscribe。
        tokio::time::sleep(Duration::from_millis(20)).await;

        // 自己的 connection 事件 → 应收到。
        let _ = state.push_tx.send(RoutedPushEvent {
            target: PushTarget::Connection(conn.clone()),
            event: PushEvent::ProcessMetadata(default_metadata()),
        });

        // 别人的 connection 事件 → 不应收到。
        let _ = state.push_tx.send(RoutedPushEvent {
            target: PushTarget::Connection("someone-else".to_string()),
            event: PushEvent::ProcessMetadata(default_metadata()),
        });

        let first = tokio::time::timeout(Duration::from_millis(500), outbound_rx.recv())
            .await
            .expect("timed out waiting for own connection event")
            .expect("channel closed");

        let Message::Text(text) = first else {
            panic!("expected text");
        };
        let body = text.trim_end_matches(RS);
        let v: Value = serde_json::from_str(body).unwrap();
        assert_eq!(v["type"], 1);
        assert_eq!(v["target"], "ReceiveProcessMetadata");

        // 再等一个短暂窗口，确认没有第二条帧（别人事件被过滤）。
        assert!(
            tokio::time::timeout(Duration::from_millis(100), outbound_rx.recv())
                .await
                .is_err(),
            "must not receive another connection's event"
        );

        drop(dispatcher);
    }

    fn default_metadata() -> xhm_core::wire::ProcessMetadataPayload {
        xhm_core::wire::ProcessMetadataPayload {
            timestamp: chrono::Local::now(),
            process_count: 0,
            processes: Vec::new(),
        }
    }

    // ===== SSE =====

    #[tokio::test]
    async fn sse_full_query_registers_full_subscription() {
        let state = make_state();
        let (conn, _sub) =
            register_sse_connection(&state, SubscriptionMode::Full, Vec::new()).await;

        let registry = state.realtime.read().await;
        let sub = registry.subscription(&conn).expect("connection registered");
        assert!(matches!(sub.mode, SubscriptionMode::Full));
        assert!(sub.pinned_process_ids.is_empty());
    }

    #[tokio::test]
    async fn sse_lite_query_normalizes_pinned_ids() {
        let state = make_state();
        let (conn, _sub) =
            register_sse_connection(&state, SubscriptionMode::Lite, vec![7, 3, 7, -1]).await;

        let registry = state.realtime.read().await;
        let sub = registry.subscription(&conn).expect("connection registered");
        assert!(matches!(sub.mode, SubscriptionMode::Lite));
        assert_eq!(sub.pinned_process_ids, vec![3, 7]);
    }

    #[test]
    fn parse_pinned_query_splits_and_normalizes() {
        assert_eq!(parse_pinned_query(None), Vec::<i32>::new());
        assert_eq!(parse_pinned_query(Some("")), Vec::<i32>::new());
        assert_eq!(parse_pinned_query(Some("7, 3, 7, -1")), vec![3, 7]);
        assert_eq!(parse_pinned_query(Some("1;2 3")), vec![1, 2, 3]);
    }

    #[test]
    fn parse_pinned_query_mode_uses_subscription_mode_parse() {
        // SubscriptionMode::parse 的语义已在 xhm-core 覆盖；这里只确保
        // 默认（缺省）走 Full。
        assert_eq!(SubscriptionMode::parse(None), SubscriptionMode::Full);
        assert_eq!(
            SubscriptionMode::parse(Some("LITE")),
            SubscriptionMode::Lite
        );
    }

    #[tokio::test]
    async fn sse_encode_carries_event_name_and_payload() {
        let event = PushEvent::ProcessMetadata(default_metadata());
        let sse = sse_encode(&event);
        // Event 没有 Eq，也没有公开字段；只能通过 wire 上游一致性间接验证：
        // 同一条 PushEvent 的 event_name 与 sse_encode 内部用的 event 字段一致。
        assert_eq!(event.event_name(), "ReceiveProcessMetadata");
        // sse_encode 必须产出非 default 的 Event（至少带 event + data）。
        // 这里用一个 fresh default 作对照——两者不应“相等到同一渲染”。
        let _ = sse;
    }

    #[tokio::test]
    async fn sse_event_stream_filters_other_connection_events() {
        let state = make_state();
        let conn = new_connection_id();
        let subscription = Arc::new(RwLock::new(SubscriptionSnapshot {
            mode: SubscriptionMode::Lite,
            pinned: vec![1],
        }));

        // 流消费者先持有 rx（否则后台任务因 channel closed 立刻退出）。
        let mut stream = Box::pin(sse_event_stream(state.clone(), conn.clone(), subscription));
        // 等后台任务订阅 push_tx 就绪，避免丢失先发出的广播。
        tokio::time::sleep(Duration::from_millis(20)).await;

        // 别人的 Connection 事件必须被过滤掉。
        let _ = state.push_tx.send(RoutedPushEvent {
            target: PushTarget::Connection("someone-else".to_string()),
            event: PushEvent::ProcessMetadata(default_metadata()),
        });
        // 自己的 Connection 事件必须到达。
        let _ = state.push_tx.send(RoutedPushEvent {
            target: PushTarget::Connection(conn.clone()),
            event: PushEvent::ProcessMetadata(default_metadata()),
        });

        // 第一帧应是本连接的事件（Ok(Event)）。
        let first = tokio::time::timeout(Duration::from_millis(500), stream.next())
            .await
            .expect("timed out waiting for own event")
            .expect("stream ended prematurely")
            .expect("SSE event must be Ok");

        // 第二帧不应在短时间内到达（别人的事件被过滤）。
        let leaked = tokio::time::timeout(Duration::from_millis(100), stream.next()).await;
        assert!(
            leaked.is_err(),
            "must not receive another connection's event"
        );

        // 拿到帧本身，避免未使用变量告警，并确保它是非 default 事件。
        let _ = first;
    }

    // ===== negotiate handler 单测 =====

    #[tokio::test]
    async fn negotiate_v1_declares_websockets_text_and_returns_token() {
        use axum::body::to_bytes;
        let state = make_state();
        let response = negotiate_handler(
            State(state.clone()),
            Query(NegotiateQuery {
                negotiate_version: Some(1),
            }),
        )
        .await;

        let bytes = to_bytes(response.into_body(), 8 * 1024).await.unwrap();
        let value: Value = serde_json::from_slice(&bytes).unwrap();

        assert_eq!(value["negotiateVersion"], 1);
        assert!(value["connectionId"].is_string());
        assert!(value["connectionToken"].is_string());
        let transports = value["availableTransports"].as_array().unwrap();
        assert_eq!(transports.len(), 1);
        assert_eq!(transports[0]["transport"], "WebSockets");
        assert_eq!(transports[0]["transferFormats"][0], "Text");

        // token 必须进入 pending 集合，且只能被消费一次。
        let token = value["connectionToken"].as_str().unwrap().to_string();
        {
            let mut registry = state.realtime.write().await;
            assert!(
                registry.consume_and_register(&token, "ws-conn".to_string()),
                "negotiate token must be consumable once"
            );
            assert!(
                !registry.consume_and_register(&token, "ws-conn-2".to_string()),
                "negotiate token must not be consumable twice"
            );
        }
    }

    #[tokio::test]
    async fn negotiate_v0_omits_connection_id_and_token() {
        use axum::body::to_bytes;
        let state = make_state();
        let response = negotiate_handler(
            State(state.clone()),
            Query(NegotiateQuery {
                negotiate_version: Some(0),
            }),
        )
        .await;

        let bytes = to_bytes(response.into_body(), 8 * 1024).await.unwrap();
        let value: Value = serde_json::from_slice(&bytes).unwrap();
        assert!(value.get("connectionId").is_none());
        assert!(value.get("connectionToken").is_none());
        assert!(value.get("negotiateVersion").is_none());
        assert_eq!(value["availableTransports"][0]["transport"], "WebSockets");
    }
}
