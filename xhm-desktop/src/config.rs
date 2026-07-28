//! exe-relative Service discovery (TASK-003).
//!
//! The production reader resolves `service-endpoints.json` beside the executable,
//! parses the existing PascalCase wrapper, normalizes `ApiBaseUrl` to an origin,
//! and probes the preferred port through preferred + 10. Missing/unreadable/invalid
//! configuration falls back to port 35181 without panicking.

use std::future::Future;
use std::path::Path;
use std::time::Duration;

use futures::future::BoxFuture;
use serde::Deserialize;
use url::Url;

/// P1 frozen service port (`xhm-service/src/state.rs:14`).
pub const DEFAULT_SERVICE_PORT: u16 = 35_181;
/// P1 frozen SSE path (`xhm-service/src/state.rs:16`).
pub const DEFAULT_SSE_PATH: &str = "/api/v1/events";

const HEALTH_CHECK_PATH: &str = "/api/v1/config/health";
const SERVICE_PROBE_TIMEOUT: Duration = Duration::from_millis(300);
const PROBE_PORT_RANGE: u16 = 10;
const CONFIG_FILE_NAME: &str = "service-endpoints.json";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Config {
    /// Normalized origin without a trailing slash, query, fragment, or non-root path.
    pub api_base: String,
    /// Absolute SSE URL derived only from the normalized API origin.
    pub sse_url: String,
    pub resolved_port: u16,
    pub source: ConfigSource,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConfigSource {
    Default,
    File,
    Probe,
}

impl Config {
    /// Load `service-endpoints.json` from `current_exe().parent()`.
    pub async fn load() -> Self {
        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|path| path.parent().map(Path::to_path_buf));
        let Some(dir) = exe_dir else {
            tracing::warn!("current_exe().parent() unavailable; using default config");
            return Self::default();
        };
        Self::from_dir(&dir).await
    }

    /// Injectable directory entry used by production and tests.
    pub async fn from_dir(dir: &Path) -> Self {
        let reader = FileConfigReader;
        let endpoint = match read_endpoint(dir, &reader) {
            Ok(endpoint) => endpoint,
            Err(error) => return fallback_after_load_error(dir, error),
        };
        let probe = match HttpPortProbe::new() {
            Ok(probe) => probe,
            Err(error) => {
                tracing::warn!(%error, "health probe client construction failed; using file port");
                let port = endpoint
                    .port_or_known_default()
                    .unwrap_or(DEFAULT_SERVICE_PORT);
                return Self::from_url(endpoint, port, ConfigSource::File);
            }
        };
        Self::resolve(endpoint, &probe).await
    }

    #[cfg(test)]
    async fn from_dir_with(dir: &Path, reader: &dyn ConfigReader, probe: &dyn PortProbe) -> Self {
        match read_endpoint(dir, reader) {
            Ok(endpoint) => Self::resolve(endpoint, probe).await,
            Err(error) => fallback_after_load_error(dir, error),
        }
    }

    async fn resolve(endpoint: Url, probe: &dyn PortProbe) -> Self {
        let preferred = endpoint
            .port_or_known_default()
            .unwrap_or(DEFAULT_SERVICE_PORT);
        match first_matching_port(preferred, |candidate| probe.check(&endpoint, candidate)).await {
            Some(port) => Self::from_url(endpoint, port, ConfigSource::Probe),
            None => {
                tracing::warn!(preferred, "health probe all failed; using configured port");
                Self::from_url(endpoint, preferred, ConfigSource::File)
            }
        }
    }

    fn default_config(source: ConfigSource) -> Self {
        let api_base = format!("http://localhost:{DEFAULT_SERVICE_PORT}");
        let sse_url = format!("{api_base}{DEFAULT_SSE_PATH}");
        Self {
            api_base,
            sse_url,
            resolved_port: DEFAULT_SERVICE_PORT,
            source,
        }
    }

    fn from_url(endpoint: Url, port: u16, source: ConfigSource) -> Self {
        let Some(origin) = normalize_origin(endpoint, port) else {
            return Self::default_config(ConfigSource::Default);
        };
        let api_base = origin_string(&origin);
        let sse_url = endpoint_string(&origin, DEFAULT_SSE_PATH);
        Self {
            api_base,
            sse_url,
            resolved_port: port,
            source,
        }
    }
}

impl Default for Config {
    fn default() -> Self {
        Self::default_config(ConfigSource::Default)
    }
}

fn normalize_origin(mut endpoint: Url, port: u16) -> Option<Url> {
    if endpoint.set_port(Some(port)).is_err() {
        return None;
    }
    endpoint.set_path("");
    endpoint.set_query(None);
    endpoint.set_fragment(None);
    Some(endpoint)
}

fn origin_string(origin: &Url) -> String {
    origin.as_str().trim_end_matches('/').to_owned()
}

fn endpoint_string(origin: &Url, path: &str) -> String {
    let mut endpoint = origin.clone();
    endpoint.set_path(path);
    endpoint.set_query(None);
    endpoint.set_fragment(None);
    endpoint.to_string()
}

#[derive(Debug, Default, Deserialize)]
struct EndpointsFile {
    #[serde(default, rename = "ServiceEndpoints")]
    service_endpoints: Option<ServiceEndpoints>,
}

#[derive(Debug, Default, Deserialize)]
#[allow(non_snake_case)]
struct ServiceEndpoints {
    #[serde(default)]
    ApiBaseUrl: Option<String>,
    #[serde(default)]
    SignalRUrl: Option<String>,
}

trait ConfigReader: Send + Sync {
    fn read(&self, path: &Path) -> std::io::Result<Vec<u8>>;
}

#[derive(Debug, Clone, Copy)]
struct FileConfigReader;

impl ConfigReader for FileConfigReader {
    fn read(&self, path: &Path) -> std::io::Result<Vec<u8>> {
        std::fs::read(path)
    }
}

trait PortProbe: Send + Sync {
    fn check<'a>(&'a self, base: &'a Url, port: u16) -> BoxFuture<'a, bool>;
}

#[derive(Debug, Clone)]
struct HttpPortProbe {
    client: reqwest::Client,
}

impl HttpPortProbe {
    fn new() -> Result<Self, reqwest::Error> {
        let client = reqwest::Client::builder()
            .timeout(SERVICE_PROBE_TIMEOUT)
            .redirect(reqwest::redirect::Policy::none())
            .build()?;
        Ok(Self { client })
    }
}

impl PortProbe for HttpPortProbe {
    fn check<'a>(&'a self, base: &'a Url, port: u16) -> BoxFuture<'a, bool> {
        Box::pin(is_xhmonitor_service(&self.client, base, port))
    }
}

fn read_endpoint(dir: &Path, reader: &dyn ConfigReader) -> Result<Url, ConfigLoadError> {
    let path = dir.join(CONFIG_FILE_NAME);
    let bytes = reader.read(&path).map_err(ConfigLoadError::Io)?;
    let parsed: EndpointsFile = serde_json::from_slice(&bytes).map_err(ConfigLoadError::Json)?;
    let endpoints = parsed
        .service_endpoints
        .ok_or(ConfigLoadError::MissingWrapper)?;
    if let Some(signalr) = endpoints.SignalRUrl.as_deref() {
        tracing::debug!(signalr, "SignalRUrl is compatibility-only");
    }
    let raw = endpoints
        .ApiBaseUrl
        .as_deref()
        .ok_or(ConfigLoadError::InvalidApiUrl)?;
    parse_url(raw).ok_or(ConfigLoadError::InvalidApiUrl)
}

fn fallback_after_load_error(dir: &Path, error: ConfigLoadError) -> Config {
    tracing::warn!(
        path = %dir.join(CONFIG_FILE_NAME).display(),
        %error,
        "service endpoints config fallback to default 35181"
    );
    Config::default()
}

fn parse_url(raw: &str) -> Option<Url> {
    Url::parse(raw.trim())
        .ok()
        .filter(|url| matches!(url.scheme(), "http" | "https") && url.has_host())
}

#[derive(Debug, thiserror::Error)]
enum ConfigLoadError {
    #[error("io: {0}")]
    Io(#[source] std::io::Error),
    #[error("json: {0}")]
    Json(#[source] serde_json::Error),
    #[error("missing ServiceEndpoints wrapper")]
    MissingWrapper,
    #[error("invalid ApiBaseUrl")]
    InvalidApiUrl,
}

async fn first_matching_port<F, Fut>(preferred: u16, mut probe: F) -> Option<u16>
where
    F: FnMut(u16) -> Fut,
    Fut: Future<Output = bool>,
{
    for candidate in preferred..=preferred.saturating_add(PROBE_PORT_RANGE) {
        if probe(candidate).await {
            return Some(candidate);
        }
    }
    None
}

#[derive(Debug, Deserialize)]
struct HealthProbeResponse {
    status: String,
}

async fn is_xhmonitor_service(client: &reqwest::Client, base: &Url, port: u16) -> bool {
    let Some(mut probe) = normalize_origin(base.clone(), port) else {
        return false;
    };
    probe.set_path(HEALTH_CHECK_PATH);

    let response = match client.get(probe).send().await {
        Ok(response) => response,
        Err(_) => return false,
    };
    if response.status() != reqwest::StatusCode::OK
        && response.status() != reqwest::StatusCode::SERVICE_UNAVAILABLE
    {
        return false;
    }
    match response.json::<HealthProbeResponse>().await {
        Ok(body) => {
            body.status.eq_ignore_ascii_case("Healthy")
                || body.status.eq_ignore_ascii_case("Unhealthy")
        }
        Err(_) => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;
    use wiremock::matchers::{method, path};
    use wiremock::{Mock, MockServer, ResponseTemplate};

    static DIR_SEQ: AtomicUsize = AtomicUsize::new(0);

    struct TestDir {
        path: PathBuf,
    }

    impl TestDir {
        fn new() -> Self {
            let seq = DIR_SEQ.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "xhm-desktop-config-{}-{}-{}",
                std::process::id(),
                seq,
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
            std::fs::create_dir_all(&path).unwrap();
            Self { path }
        }

        fn write(&self, body: &str) {
            std::fs::write(self.path.join(CONFIG_FILE_NAME), body).unwrap();
        }
    }

    impl Drop for TestDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.path);
        }
    }

    #[derive(Debug)]
    struct StaticProbe {
        hit: Option<u16>,
        calls: AtomicUsize,
    }

    impl StaticProbe {
        fn none() -> Self {
            Self {
                hit: None,
                calls: AtomicUsize::new(0),
            }
        }
    }

    impl PortProbe for StaticProbe {
        fn check<'a>(&'a self, _base: &'a Url, port: u16) -> BoxFuture<'a, bool> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            Box::pin(async move { self.hit == Some(port) })
        }
    }

    #[derive(Debug)]
    struct DeniedReader;

    impl ConfigReader for DeniedReader {
        fn read(&self, _path: &Path) -> std::io::Result<Vec<u8>> {
            Err(std::io::Error::new(
                std::io::ErrorKind::PermissionDenied,
                "fixture permission denied",
            ))
        }
    }

    #[tokio::test]
    async fn missing_file_falls_back_to_default_without_panic() {
        let dir = TestDir::new();
        let config =
            Config::from_dir_with(&dir.path, &FileConfigReader, &StaticProbe::none()).await;
        assert_eq!(config, Config::default());
    }

    #[tokio::test]
    async fn permission_denied_reader_falls_back_without_probe_or_panic() {
        let dir = TestDir::new();
        let probe = StaticProbe::none();
        let config = Config::from_dir_with(&dir.path, &DeniedReader, &probe).await;
        assert_eq!(config, Config::default());
        assert_eq!(probe.calls.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn bad_json_missing_wrapper_and_bad_url_fall_back() {
        for body in [
            "{ not json",
            "{\"Other\":{}}",
            "{\"ServiceEndpoints\":{\"ApiBaseUrl\":\"not a url\"}}",
            "{\"ServiceEndpoints\":{\"ApiBaseUrl\":\"ftp://localhost/x\"}}",
        ] {
            let dir = TestDir::new();
            dir.write(body);
            let config =
                Config::from_dir_with(&dir.path, &FileConfigReader, &StaticProbe::none()).await;
            assert_eq!(config, Config::default(), "fixture: {body}");
        }
    }

    #[tokio::test]
    async fn all_failed_probe_uses_isolated_configured_port() {
        let dir = TestDir::new();
        dir.write("{\"ServiceEndpoints\":{\"ApiBaseUrl\":\"http://127.0.0.1:42123\"}}");
        let probe = StaticProbe::none();
        let config = Config::from_dir_with(&dir.path, &FileConfigReader, &probe).await;
        assert_eq!(config.source, ConfigSource::File);
        assert_eq!(config.resolved_port, 42_123);
        assert_eq!(probe.calls.load(Ordering::SeqCst), 11);
    }

    #[tokio::test]
    async fn preferred_and_unhealthy_503_are_probe_hits() {
        for status in [200, 503] {
            let server = MockServer::start().await;
            let label = if status == 200 {
                "Healthy"
            } else {
                "Unhealthy"
            };
            Mock::given(method("GET"))
                .and(path(HEALTH_CHECK_PATH))
                .respond_with(
                    ResponseTemplate::new(status)
                        .set_body_json(serde_json::json!({"status": label})),
                )
                .mount(&server)
                .await;
            let base = Url::parse(&server.uri()).unwrap();
            let probe = HttpPortProbe::new().unwrap();
            assert!(probe.check(&base, server.address().port()).await);
        }
    }

    #[tokio::test]
    async fn non_xhmonitor_200_body_is_not_a_hit() {
        let server = MockServer::start().await;
        Mock::given(method("GET"))
            .and(path(HEALTH_CHECK_PATH))
            .respond_with(ResponseTemplate::new(200).set_body_string("not xhMonitor"))
            .mount(&server)
            .await;
        let base = Url::parse(&server.uri()).unwrap();
        let probe = HttpPortProbe::new().unwrap();
        assert!(!probe.check(&base, server.address().port()).await);
    }

    #[test]
    fn endpoint_urls_normalize_query_fragment_and_non_root_path() {
        let endpoint = Url::parse("http://localhost:35179/tenant/path?x=1#section").unwrap();
        let config = Config::from_url(endpoint, 35_181, ConfigSource::File);
        assert_eq!(config.api_base, "http://localhost:35181");
        assert_eq!(config.sse_url, "http://localhost:35181/api/v1/events");
    }

    #[test]
    fn default_is_exact_35181_and_signalr_never_drives_sse() {
        let config = Config::default();
        assert_eq!(config.api_base, "http://localhost:35181");
        assert_eq!(config.sse_url, "http://localhost:35181/api/v1/events");
    }

    #[tokio::test]
    async fn real_http_probe_discovers_preferred_plus_two() {
        let (first, second, health, preferred) = bind_consecutive_listeners().await;
        let health_port = preferred + 2;
        let first_task = tokio::spawn(drop_one_connection(first));
        let second_task = tokio::spawn(drop_one_connection(second));
        let (request_tx, request_rx) = tokio::sync::oneshot::channel();
        let health_task = tokio::spawn(serve_health_once(health, request_tx));

        let dir = TestDir::new();
        dir.write(&format!(
            "{{\"ServiceEndpoints\":{{\"ApiBaseUrl\":\"http://127.0.0.1:{preferred}/legacy?x=1#f\",\"SignalRUrl\":\"http://ignored.invalid/hubs/metrics\"}}}}"
        ));
        let config = Config::from_dir(&dir.path).await;

        assert_eq!(config.source, ConfigSource::Probe);
        assert_eq!(config.resolved_port, health_port);
        assert_eq!(config.api_base, format!("http://127.0.0.1:{health_port}"));
        assert_eq!(
            config.sse_url,
            format!("http://127.0.0.1:{health_port}/api/v1/events")
        );
        let request = request_rx.await.unwrap();
        assert!(request.starts_with("GET /api/v1/config/health "));

        first_task.await.unwrap();
        second_task.await.unwrap();
        health_task.await.unwrap();
    }

    async fn bind_consecutive_listeners() -> (TcpListener, TcpListener, TcpListener, u16) {
        for _ in 0..100 {
            let seed = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let preferred = seed.local_addr().unwrap().port();
            drop(seed);
            if preferred > u16::MAX - 2 {
                continue;
            }
            let Ok(first) = TcpListener::bind(("127.0.0.1", preferred)).await else {
                continue;
            };
            let Ok(second) = TcpListener::bind(("127.0.0.1", preferred + 1)).await else {
                continue;
            };
            let Ok(third) = TcpListener::bind(("127.0.0.1", preferred + 2)).await else {
                continue;
            };
            return (first, second, third, preferred);
        }
        panic!("could not reserve three consecutive loopback ports");
    }

    async fn drop_one_connection(listener: TcpListener) {
        if let Ok((socket, _)) = listener.accept().await {
            drop(socket);
        }
    }

    async fn serve_health_once(
        listener: TcpListener,
        request_tx: tokio::sync::oneshot::Sender<String>,
    ) {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut bytes = vec![0_u8; 2048];
        let count = socket.read(&mut bytes).await.unwrap();
        let request = String::from_utf8_lossy(&bytes[..count]).into_owned();
        let _ = request_tx.send(request);
        let body = r#"{"status":"Healthy"}"#;
        let response = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        socket.write_all(response.as_bytes()).await.unwrap();
    }

    #[tokio::test]
    async fn candidate_order_stops_at_plus_two() {
        let calls = Arc::new(tokio::sync::Mutex::new(Vec::new()));
        let recorded = Arc::clone(&calls);
        let found = first_matching_port(35_179, move |candidate| {
            let recorded = Arc::clone(&recorded);
            async move {
                recorded.lock().await.push(candidate);
                candidate == 35_181
            }
        })
        .await;
        assert_eq!(found, Some(35_181));
        assert_eq!(*calls.lock().await, vec![35_179, 35_180, 35_181]);
    }
}
