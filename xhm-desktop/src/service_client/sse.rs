//! Frozen five-event SSE client (TASK-004).
//!
//! A supervisor owns the current subscription. Mode/pin changes cancel the old
//! request, wait for it to finish, then create a new `/api/v1/events` request.
//! Network I/O, retry sleeps, cancellation, and bounded-channel backpressure stay
//! outside the Slint event loop.

use std::sync::Arc;
use std::time::Duration;

use futures::future::BoxFuture;
use futures::StreamExt;
use tokio::sync::{mpsc, watch};
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;
use url::Url;
use xhm_core::wire::{
    events, HardwareLimitsPayload, ProcessMetadataPayload, ProcessSnapshotPayload, PushEvent,
    SubscriptionMode, SystemUsagePayload,
};

use crate::config::{Config, DEFAULT_SSE_PATH};

const MAX_CONNECT_ATTEMPTS: usize = 10;
const RETRY_INTERVAL: Duration = Duration::from_secs(2);

#[derive(Debug, Clone)]
pub enum SseMessage {
    Connected,
    Disconnected,
    Event(PushEvent),
    UnknownEvent { event: String },
    BadJson { event: String, error: String },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SseSubscription {
    pub mode: SubscriptionMode,
    pub pinned: Vec<i32>,
}

impl SseSubscription {
    pub fn new(mode: SubscriptionMode, pinned: Vec<i32>) -> Self {
        let pinned = xhm_core::wire::normalize_pinned_ids(Some(&pinned));
        Self { mode, pinned }
    }
}

impl Default for SseSubscription {
    fn default() -> Self {
        Self::new(SubscriptionMode::Lite, Vec::new())
    }
}

#[derive(Debug, Clone)]
pub struct SseStreamBuilder {
    base_url: Url,
    subscription: SseSubscription,
}

impl SseStreamBuilder {
    pub fn from_config(config: &Config) -> Result<Self, url::ParseError> {
        Self::new(&config.api_base)
    }

    pub fn new(base_url: &str) -> Result<Self, url::ParseError> {
        Ok(Self {
            base_url: Url::parse(base_url)?,
            subscription: SseSubscription::default(),
        })
    }

    pub fn mode(mut self, mode: SubscriptionMode) -> Self {
        self.subscription.mode = mode;
        self
    }

    pub fn pinned(mut self, pinned: Vec<i32>) -> Self {
        self.subscription.pinned = xhm_core::wire::normalize_pinned_ids(Some(&pinned));
        self
    }

    pub fn build(self) -> SseStream {
        SseStream {
            base_url: self.base_url,
            subscription: self.subscription,
        }
    }
}

#[derive(Debug)]
pub struct SseStream {
    base_url: Url,
    subscription: SseSubscription,
}

impl SseStream {
    pub fn url(&self) -> Url {
        subscription_url(&self.base_url, &self.subscription)
    }

    /// Start a supervisor. The initial subscription is Collapsed/Lite unless the
    /// builder explicitly selected another mode.
    pub fn spawn(self, http: reqwest::Client) -> (mpsc::Receiver<SseMessage>, SseControl) {
        let (output_tx, output_rx) = mpsc::channel(crate::EVENT_CHANNEL_CAPACITY);
        let (subscription_tx, subscription_rx) = watch::channel(self.subscription);
        let cancel = CancellationToken::new();
        let task_cancel = cancel.clone();
        let base_url = self.base_url;
        let task = tokio::spawn(async move {
            supervise(base_url, http, output_tx, subscription_rx, task_cancel).await;
        });
        (
            output_rx,
            SseControl {
                subscription_tx,
                cancel,
                task: Some(task),
            },
        )
    }
}

#[derive(Debug)]
pub struct SseControl {
    subscription_tx: watch::Sender<SseSubscription>,
    cancel: CancellationToken,
    task: Option<JoinHandle<()>>,
}

impl SseControl {
    /// Update mode/pins. A real change cancels and recreates the active request.
    pub fn resubscribe(&self, mode: SubscriptionMode, pinned: Vec<i32>) -> bool {
        let next = SseSubscription::new(mode, pinned);
        self.subscription_tx.send_if_modified(|current| {
            if *current == next {
                false
            } else {
                *current = next;
                true
            }
        })
    }

    pub fn cancel(&self) {
        self.cancel.cancel();
    }

    pub async fn shutdown(mut self) {
        self.cancel.cancel();
        if let Some(task) = self.task.take() {
            let _ = task.await;
        }
    }
}

impl Drop for SseControl {
    fn drop(&mut self) {
        self.cancel.cancel();
    }
}

fn subscription_url(base_url: &Url, subscription: &SseSubscription) -> Url {
    let mut url = base_url.clone();
    url.set_path(DEFAULT_SSE_PATH);
    url.set_query(None);
    url.set_fragment(None);
    let mode = match subscription.mode {
        SubscriptionMode::Full => "full",
        SubscriptionMode::Lite => "lite",
    };
    {
        let mut query = url.query_pairs_mut();
        query.append_pair("mode", mode);
        if !subscription.pinned.is_empty() {
            let pinned = subscription
                .pinned
                .iter()
                .map(i32::to_string)
                .collect::<Vec<_>>()
                .join(",");
            query.append_pair("pinned", &pinned);
        }
    }
    url
}

async fn supervise(
    base_url: Url,
    http: reqwest::Client,
    output: mpsc::Sender<SseMessage>,
    mut subscriptions: watch::Receiver<SseSubscription>,
    cancel: CancellationToken,
) {
    let runner: Arc<dyn ConnectionRunner> = Arc::new(ReqwestConnectionRunner { http });

    loop {
        let subscription = subscriptions.borrow_and_update().clone();
        let url = subscription_url(&base_url, &subscription);
        let child_cancel = cancel.child_token();
        let child_runner = Arc::clone(&runner);
        let child_output = output.clone();
        let child_token = child_cancel.clone();
        let mut child = tokio::spawn(async move {
            run_connection_loop(child_runner.as_ref(), url, child_output, child_token).await;
        });

        tokio::select! {
            _ = cancel.cancelled() => {
                child_cancel.cancel();
                let _ = child.await;
                return;
            }
            changed = subscriptions.changed() => {
                child_cancel.cancel();
                let _ = child.await;
                if changed.is_err() {
                    return;
                }
                if !send_message(&output, SseMessage::Disconnected, &cancel).await.is_sent() {
                    return;
                }
            }
            _ = &mut child => {
                tokio::select! {
                    _ = cancel.cancelled() => return,
                    changed = subscriptions.changed() => {
                        if changed.is_err() {
                            return;
                        }
                    }
                }
            }
        }
    }
}

trait ConnectionRunner: Send + Sync {
    fn connect<'a>(
        &'a self,
        url: &'a Url,
        output: &'a mpsc::Sender<SseMessage>,
        cancel: &'a CancellationToken,
    ) -> BoxFuture<'a, ConnectOutcome>;
}

#[derive(Debug, Clone)]
struct ReqwestConnectionRunner {
    http: reqwest::Client,
}

impl ConnectionRunner for ReqwestConnectionRunner {
    fn connect<'a>(
        &'a self,
        url: &'a Url,
        output: &'a mpsc::Sender<SseMessage>,
        cancel: &'a CancellationToken,
    ) -> BoxFuture<'a, ConnectOutcome> {
        Box::pin(connect_and_drain(&self.http, url, output, cancel))
    }
}

async fn run_connection_loop(
    runner: &dyn ConnectionRunner,
    url: Url,
    output: mpsc::Sender<SseMessage>,
    cancel: CancellationToken,
) {
    let mut consecutive_failures = 0_usize;
    loop {
        match runner.connect(&url, &output, &cancel).await {
            ConnectOutcome::Cancelled | ConnectOutcome::ChannelClosed => return,
            ConnectOutcome::ConnectFailed(reason) => {
                consecutive_failures += 1;
                tracing::warn!(
                    attempt = consecutive_failures,
                    max = MAX_CONNECT_ATTEMPTS,
                    %url,
                    %reason,
                    "SSE connection attempt failed"
                );
                if consecutive_failures == MAX_CONNECT_ATTEMPTS {
                    tracing::error!(%url, "SSE consecutive-failure budget exhausted");
                    return;
                }
            }
            ConnectOutcome::EstablishedEnded(reason) => {
                // A successfully established response resets the failure budget.
                consecutive_failures = 0;
                tracing::warn!(%url, %reason, "SSE connection ended; reconnecting");
            }
        }

        tokio::select! {
            _ = cancel.cancelled() => return,
            _ = tokio::time::sleep(RETRY_INTERVAL) => {}
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum ConnectOutcome {
    EstablishedEnded(String),
    ConnectFailed(String),
    Cancelled,
    ChannelClosed,
}

async fn connect_and_drain(
    http: &reqwest::Client,
    url: &Url,
    output: &mpsc::Sender<SseMessage>,
    cancel: &CancellationToken,
) -> ConnectOutcome {
    let response = tokio::select! {
        _ = cancel.cancelled() => return ConnectOutcome::Cancelled,
        response = http.get(url.clone()).send() => response,
    };
    let response = match response {
        Ok(response) if response.status().is_success() => response,
        Ok(response) => {
            return ConnectOutcome::ConnectFailed(format!("status {}", response.status()));
        }
        Err(error) => return ConnectOutcome::ConnectFailed(error.to_string()),
    };

    match send_message(output, SseMessage::Connected, cancel).await {
        SendOutcome::Sent => {}
        SendOutcome::Cancelled => return ConnectOutcome::Cancelled,
        SendOutcome::Closed => return ConnectOutcome::ChannelClosed,
    }

    let mut bytes = response.bytes_stream();
    let mut framer = SseFramer::new();
    loop {
        tokio::select! {
            _ = cancel.cancelled() => return ConnectOutcome::Cancelled,
            chunk = bytes.next() => match chunk {
                Some(Ok(chunk)) => {
                    for message in framer.feed(&chunk) {
                        match send_message(output, message, cancel).await {
                            SendOutcome::Sent => {}
                            SendOutcome::Cancelled => return ConnectOutcome::Cancelled,
                            SendOutcome::Closed => return ConnectOutcome::ChannelClosed,
                        }
                    }
                }
                Some(Err(error)) => {
                    let reason = format!("body read failed: {error}");
                    let _ = send_message(output, SseMessage::Disconnected, cancel).await;
                    return ConnectOutcome::EstablishedEnded(reason);
                }
                None => {
                    let _ = send_message(output, SseMessage::Disconnected, cancel).await;
                    return ConnectOutcome::EstablishedEnded("stream ended".to_owned());
                }
            }
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SendOutcome {
    Sent,
    Cancelled,
    Closed,
}

impl SendOutcome {
    fn is_sent(self) -> bool {
        self == Self::Sent
    }
}

async fn send_message(
    output: &mpsc::Sender<SseMessage>,
    message: SseMessage,
    cancel: &CancellationToken,
) -> SendOutcome {
    tokio::select! {
        _ = cancel.cancelled() => SendOutcome::Cancelled,
        result = output.send(message) => {
            if result.is_ok() { SendOutcome::Sent } else { SendOutcome::Closed }
        }
    }
}

#[derive(Debug, Default)]
pub struct SseFramer {
    buffer: Vec<u8>,
    pending_event: Option<String>,
    pending_data: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct FramedEvent {
    event: Option<String>,
    data: Option<String>,
}

impl SseFramer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn feed(&mut self, chunk: &[u8]) -> Vec<SseMessage> {
        self.buffer.extend_from_slice(chunk);
        let mut output = Vec::new();
        loop {
            let Some(newline) = self.buffer.iter().position(|byte| *byte == b'\n') else {
                break;
            };
            let mut line: Vec<u8> = self.buffer.drain(..=newline).collect();
            while matches!(line.last(), Some(b'\n' | b'\r')) {
                line.pop();
            }
            if line.is_empty() {
                if let Some(event) = self.take_pending().and_then(|event| decode(&event)) {
                    output.push(event);
                }
                continue;
            }
            if line[0] == b':' {
                continue;
            }
            let (field, value) = match line.iter().position(|byte| *byte == b':') {
                Some(colon) => {
                    let value_start = if line.get(colon + 1) == Some(&b' ') {
                        colon + 2
                    } else {
                        colon + 1
                    };
                    (&line[..colon], &line[value_start..])
                }
                None => (line.as_slice(), &[][..]),
            };
            match field {
                b"event" => {
                    self.pending_event = Some(String::from_utf8_lossy(value).into_owned());
                }
                b"data" => append_data(&mut self.pending_data, value),
                _ => {}
            }
        }
        output
    }

    fn take_pending(&mut self) -> Option<FramedEvent> {
        let event = self.pending_event.take();
        let data = self.pending_data.take();
        if event.is_none() && data.is_none() {
            None
        } else {
            Some(FramedEvent { event, data })
        }
    }
}

fn append_data(pending: &mut Option<String>, value: &[u8]) {
    let value = String::from_utf8_lossy(value);
    match pending {
        Some(existing) => {
            existing.push('\n');
            existing.push_str(&value);
        }
        None => *pending = Some(value.into_owned()),
    }
}

fn decode(framed: &FramedEvent) -> Option<SseMessage> {
    let event_name = framed.event.as_deref()?;
    let data = framed.data.as_deref().unwrap_or("");
    let payload: serde_json::Value = match serde_json::from_str(data) {
        Ok(payload) => payload,
        Err(error) => {
            return Some(SseMessage::BadJson {
                event: event_name.to_owned(),
                error: error.to_string(),
            });
        }
    };
    match decode_known(event_name, payload) {
        Ok(Some(event)) => Some(SseMessage::Event(event)),
        Ok(None) => Some(SseMessage::UnknownEvent {
            event: event_name.to_owned(),
        }),
        Err(error) => Some(SseMessage::BadJson {
            event: event_name.to_owned(),
            error: error.to_string(),
        }),
    }
}

fn decode_known(
    name: &str,
    payload: serde_json::Value,
) -> Result<Option<PushEvent>, serde_json::Error> {
    match name {
        events::RECEIVE_HARDWARE_LIMITS => serde_json::from_value::<HardwareLimitsPayload>(payload)
            .map(PushEvent::HardwareLimits)
            .map(Some),
        events::RECEIVE_SYSTEM_USAGE => serde_json::from_value::<SystemUsagePayload>(payload)
            .map(PushEvent::SystemUsage)
            .map(Some),
        events::RECEIVE_PROCESS_METRICS => {
            serde_json::from_value::<ProcessSnapshotPayload>(payload)
                .map(PushEvent::ProcessMetrics)
                .map(Some)
        }
        events::RECEIVE_PROCESS_METRICS_LITE => {
            serde_json::from_value::<ProcessSnapshotPayload>(payload)
                .map(PushEvent::ProcessMetricsLite)
                .map(Some)
        }
        events::RECEIVE_PROCESS_METADATA => {
            serde_json::from_value::<ProcessMetadataPayload>(payload)
                .map(PushEvent::ProcessMetadata)
                .map(Some)
        }
        _ => Ok(None),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Mutex;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;
    use wiremock::matchers::{method, path, query_param};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    fn hardware_event() -> String {
        "event: ReceiveHardwareLimits\ndata: {\"timestamp\":\"2026-07-27T12:00:00+08:00\",\"maxMemory\":16384.0,\"maxVram\":16384.0}\n\n".to_owned()
    }

    fn system_event() -> String {
        "event: ReceiveSystemUsage\ndata: {\"timestamp\":\"2026-07-27T12:00:01+08:00\",\"totalCpu\":12.5,\"totalGpu\":0.0,\"cpuTemperature\":null,\"gpuTemperature\":null,\"totalMemory\":8192.0,\"totalVram\":1024.0,\"uploadSpeed\":0.0,\"downloadSpeed\":0.0,\"maxMemory\":16384.0,\"maxVram\":16384.0,\"disks\":[],\"powerAvailable\":false,\"totalPower\":0.0,\"maxPower\":0.0,\"powerSchemeIndex\":null}\n\n".to_owned()
    }

    fn process_event(name: &str) -> String {
        format!("event: {name}\ndata: {{\"timestamp\":\"2026-07-27T12:00:02+08:00\",\"processCount\":1,\"processes\":[{{\"processId\":42,\"processName\":\"app\",\"metrics\":{{\"memory\":100.0}}}}]}}\n\n")
    }

    fn metadata_event() -> String {
        "event: ReceiveProcessMetadata\ndata: {\"timestamp\":\"2026-07-27T12:00:03+08:00\",\"processCount\":1,\"processes\":[{\"processId\":42,\"processName\":\"app\",\"commandLine\":\"./app\",\"displayName\":\"App\"}]}\n\n".to_owned()
    }

    #[test]
    fn framer_decodes_exactly_five_frozen_events() {
        let mut framer = SseFramer::new();
        let messages = framer.feed(
            format!(
                "{}{}{}{}{}",
                hardware_event(),
                system_event(),
                process_event(events::RECEIVE_PROCESS_METRICS),
                process_event(events::RECEIVE_PROCESS_METRICS_LITE),
                metadata_event()
            )
            .as_bytes(),
        );
        assert_eq!(messages.len(), 5);
        assert!(matches!(
            messages[0],
            SseMessage::Event(PushEvent::HardwareLimits(_))
        ));
        assert!(matches!(
            messages[1],
            SseMessage::Event(PushEvent::SystemUsage(_))
        ));
        assert!(matches!(
            messages[2],
            SseMessage::Event(PushEvent::ProcessMetrics(_))
        ));
        assert!(matches!(
            messages[3],
            SseMessage::Event(PushEvent::ProcessMetricsLite(_))
        ));
        assert!(matches!(
            messages[4],
            SseMessage::Event(PushEvent::ProcessMetadata(_))
        ));
    }

    #[test]
    fn framer_handles_split_chunks_comments_unknown_and_multiline_data() {
        let mut framer = SseFramer::new();
        let event = hardware_event();
        let split = event.len() / 2;
        assert!(framer.feed(&event.as_bytes()[..split]).is_empty());
        assert!(matches!(
            framer.feed(&event.as_bytes()[split..]).as_slice(),
            [SseMessage::Event(PushEvent::HardwareLimits(_))]
        ));
        assert!(framer.feed(b": keepalive\n\n").is_empty());
        assert!(matches!(
            framer
                .feed(b"event: Future\ndata: {\"x\":1}\n\n")
                .as_slice(),
            [SseMessage::UnknownEvent { .. }]
        ));
        let multiline = b"event: ReceiveHardwareLimits\ndata: {\"maxMemory\":1.0,\ndata: \"maxVram\":1.0,\ndata: \"timestamp\":\"2026-07-27T00:00:00+08:00\"}\n\n";
        assert!(matches!(
            framer.feed(multiline).as_slice(),
            [SseMessage::Event(PushEvent::HardwareLimits(_))]
        ));
    }

    #[test]
    fn known_event_syntax_and_schema_errors_are_bad_json() {
        for data in ["{not json}", "{\"maxMemory\":1.0}"] {
            let mut framer = SseFramer::new();
            let messages =
                framer.feed(format!("event: ReceiveHardwareLimits\ndata: {data}\n\n").as_bytes());
            assert!(matches!(
                messages.as_slice(),
                [SseMessage::BadJson { event, .. }] if event == events::RECEIVE_HARDWARE_LIMITS
            ));
        }
    }

    #[test]
    fn default_subscription_is_lite_and_urls_normalize_pins() {
        let stream = SseStreamBuilder::new("http://localhost:35181/root?old=1#f")
            .unwrap()
            .build();
        assert_eq!(
            stream.url().as_str(),
            "http://localhost:35181/api/v1/events?mode=lite"
        );
        let stream = SseStreamBuilder::new("http://localhost:35181")
            .unwrap()
            .mode(SubscriptionMode::Full)
            .pinned(vec![7, 3, 7, -1])
            .build();
        let pairs: Vec<_> = stream.url().query_pairs().into_owned().collect();
        assert_eq!(
            pairs,
            vec![
                ("mode".to_owned(), "full".to_owned()),
                ("pinned".to_owned(), "3,7".to_owned())
            ]
        );
    }

    #[tokio::test]
    async fn finite_stream_emits_connection_lifecycle_and_events() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path(DEFAULT_SSE_PATH))
            .and(query_param("mode", "full"))
            .respond_with(ResponseTemplate::new(200).set_body_raw(
                format!("{}{}{}", hardware_event(), system_event(), metadata_event()),
                "text/event-stream",
            ))
            .mount(&server)
            .await;
        let stream = SseStreamBuilder::new(&server.uri())
            .unwrap()
            .mode(SubscriptionMode::Full)
            .build();
        let (mut messages, control) = stream.spawn(reqwest::Client::new());
        let mut received = Vec::new();
        while received.len() < 5 {
            received.push(messages.recv().await.unwrap());
        }
        control.shutdown().await;
        assert!(matches!(received[0], SseMessage::Connected));
        assert!(matches!(
            received[1],
            SseMessage::Event(PushEvent::HardwareLimits(_))
        ));
        assert!(matches!(
            received[2],
            SseMessage::Event(PushEvent::SystemUsage(_))
        ));
        assert!(matches!(
            received[3],
            SseMessage::Event(PushEvent::ProcessMetadata(_))
        ));
        assert!(matches!(received[4], SseMessage::Disconnected));
    }

    #[tokio::test]
    async fn resubscribe_cancels_old_request_and_recreates_query() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let base = format!("http://{}", listener.local_addr().unwrap());
        let (request_tx, mut request_rx) = mpsc::channel(2);
        let server = tokio::spawn(capture_two_sse_requests(listener, request_tx));

        let stream = SseStreamBuilder::new(&base).unwrap().build();
        let (_messages, control) = stream.spawn(reqwest::Client::new());
        let first = request_rx.recv().await.unwrap();
        assert_query(&first, "lite", None);

        assert!(control.resubscribe(SubscriptionMode::Full, vec![7, 3, 7, -1]));
        let second = request_rx.recv().await.unwrap();
        assert_query(&second, "full", Some("3,7"));
        control.shutdown().await;
        server.await.unwrap();
    }

    #[tokio::test(start_paused = true)]
    async fn ten_consecutive_failures_use_nine_exact_two_second_delays() {
        let runner = Arc::new(ScriptedRunner::new(vec![ScriptedOutcome::Fail; 10]));
        let task_runner = Arc::clone(&runner);
        let (output, _rx) = mpsc::channel(8);
        let cancel = CancellationToken::new();
        let start = tokio::time::Instant::now();
        let task = tokio::spawn(async move {
            run_connection_loop(
                task_runner.as_ref(),
                Url::parse("http://127.0.0.1:1/api/v1/events").unwrap(),
                output,
                cancel,
            )
            .await;
        });
        tokio::task::yield_now().await;
        assert_eq!(runner.calls(), 1);
        for expected in 2..=10 {
            tokio::time::advance(RETRY_INTERVAL).await;
            tokio::task::yield_now().await;
            assert_eq!(runner.calls(), expected);
        }
        task.await.unwrap();
        assert_eq!(tokio::time::Instant::now() - start, Duration::from_secs(18));
    }

    #[tokio::test(start_paused = true)]
    async fn established_connection_resets_budget_and_service_restart_recovers() {
        let mut script = vec![ScriptedOutcome::Fail; 9];
        script.push(ScriptedOutcome::Established);
        script.extend(vec![ScriptedOutcome::Fail; 9]);
        script.push(ScriptedOutcome::Wait);
        let runner = Arc::new(ScriptedRunner::new(script));
        let task_runner = Arc::clone(&runner);
        let (output, _rx) = mpsc::channel(8);
        let cancel = CancellationToken::new();
        let task_cancel = cancel.clone();
        let task = tokio::spawn(async move {
            run_connection_loop(
                task_runner.as_ref(),
                Url::parse("http://127.0.0.1:1/api/v1/events").unwrap(),
                output,
                task_cancel,
            )
            .await;
        });
        tokio::task::yield_now().await;
        for expected in 2..=20 {
            tokio::time::advance(RETRY_INTERVAL).await;
            tokio::task::yield_now().await;
            assert_eq!(runner.calls(), expected);
        }
        assert!(
            !task.is_finished(),
            "fresh post-success budget must remain active"
        );
        cancel.cancel();
        task.await.unwrap();
    }

    #[tokio::test]
    async fn cancellation_interrupts_peer_that_never_sends_headers() {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = Url::parse(&format!(
            "http://{}/api/v1/events",
            listener.local_addr().unwrap()
        ))
        .unwrap();
        let (accepted_tx, accepted_rx) = tokio::sync::oneshot::channel();
        let peer = tokio::spawn(async move {
            let (socket, _) = listener.accept().await.unwrap();
            let _socket = socket;
            let _ = accepted_tx.send(());
            futures::future::pending::<()>().await;
        });
        let (output, _rx) = mpsc::channel(8);
        let cancel = CancellationToken::new();
        let task_cancel = cancel.clone();
        let task = tokio::spawn(async move {
            connect_and_drain(&reqwest::Client::new(), &url, &output, &task_cancel).await
        });
        accepted_rx.await.unwrap();
        cancel.cancel();
        let outcome = tokio::time::timeout(Duration::from_secs(1), task)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(outcome, ConnectOutcome::Cancelled);
        peer.abort();
    }

    #[tokio::test]
    async fn cancellation_interrupts_bounded_channel_backpressure() {
        let (output, _rx) = mpsc::channel(1);
        output.send(SseMessage::Connected).await.unwrap();
        let cancel = CancellationToken::new();
        let task_cancel = cancel.clone();
        let task = tokio::spawn(async move {
            send_message(&output, SseMessage::Disconnected, &task_cancel).await
        });
        tokio::task::yield_now().await;
        assert!(!task.is_finished());
        cancel.cancel();
        assert_eq!(task.await.unwrap(), SendOutcome::Cancelled);
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    enum ScriptedOutcome {
        Fail,
        Established,
        Wait,
    }

    #[derive(Debug)]
    struct ScriptedRunner {
        script: Mutex<VecDeque<ScriptedOutcome>>,
        calls: AtomicUsize,
    }

    impl ScriptedRunner {
        fn new(script: Vec<ScriptedOutcome>) -> Self {
            Self {
                script: Mutex::new(script.into()),
                calls: AtomicUsize::new(0),
            }
        }

        fn calls(&self) -> usize {
            self.calls.load(Ordering::SeqCst)
        }
    }

    impl ConnectionRunner for ScriptedRunner {
        fn connect<'a>(
            &'a self,
            _url: &'a Url,
            _output: &'a mpsc::Sender<SseMessage>,
            cancel: &'a CancellationToken,
        ) -> BoxFuture<'a, ConnectOutcome> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            let outcome = self
                .script
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .pop_front()
                .unwrap_or(ScriptedOutcome::Wait);
            Box::pin(async move {
                match outcome {
                    ScriptedOutcome::Fail => ConnectOutcome::ConnectFailed("offline".to_owned()),
                    ScriptedOutcome::Established => {
                        ConnectOutcome::EstablishedEnded("service restart".to_owned())
                    }
                    ScriptedOutcome::Wait => {
                        cancel.cancelled().await;
                        ConnectOutcome::Cancelled
                    }
                }
            })
        }
    }

    async fn capture_two_sse_requests(listener: TcpListener, requests: mpsc::Sender<String>) {
        for _ in 0..2 {
            let (mut socket, _) = listener.accept().await.unwrap();
            let request = read_headers(&mut socket).await;
            requests.send(request).await.unwrap();
            socket
                .write_all(
                    b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: keep-alive\r\n\r\n",
                )
                .await
                .unwrap();
            let mut byte = [0_u8; 1];
            while socket.read(&mut byte).await.unwrap_or(0) != 0 {}
        }
    }

    async fn read_headers(socket: &mut tokio::net::TcpStream) -> String {
        let mut bytes = Vec::new();
        let mut chunk = [0_u8; 512];
        while !bytes.windows(4).any(|window| window == b"\r\n\r\n") {
            let read = socket.read(&mut chunk).await.unwrap();
            if read == 0 {
                break;
            }
            bytes.extend_from_slice(&chunk[..read]);
        }
        String::from_utf8_lossy(&bytes).into_owned()
    }

    fn assert_query(request: &str, mode: &str, pinned: Option<&str>) {
        let request_target = request
            .lines()
            .next()
            .unwrap()
            .split_whitespace()
            .nth(1)
            .unwrap();
        let url = Url::parse(&format!("http://localhost{request_target}")).unwrap();
        let pairs: Vec<_> = url.query_pairs().into_owned().collect();
        assert!(pairs.contains(&("mode".to_owned(), mode.to_owned())));
        match pinned {
            Some(pinned) => {
                assert!(pairs.contains(&("pinned".to_owned(), pinned.to_owned())));
            }
            None => assert!(!pairs.iter().any(|(key, _)| key == "pinned")),
        }
    }
}
