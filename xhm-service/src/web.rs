use std::{borrow::Cow, net::IpAddr, path::Path, sync::Arc};

use axum::{
    body::Body,
    extract::{ConnectInfo, Request, State},
    http::{header::AUTHORIZATION, Method, StatusCode},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    Router,
};
use thiserror::Error;
use tower_http::services::{ServeDir, ServeFile};
use xhm_core::traits::MetricStore;

use crate::{routes, AppState};

pub const DEFAULT_WEB_PORT: u16 = 35_180;

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SecurityConfig {
    pub enable_lan_access: bool,
    pub enable_access_key: bool,
    pub access_key: String,
    pub ip_whitelist: String,
}

impl SecurityConfig {
    pub fn load(store: &dyn MetricStore) -> Result<Self, SecurityConfigError> {
        let settings = store
            .list_settings()
            .map_err(|error| SecurityConfigError::Store(error.to_string()))?;
        let mut config = Self::default();
        for setting in settings {
            if setting.category != "System" {
                continue;
            }
            match setting.key.as_str() {
                "EnableLanAccess" => {
                    config.enable_lan_access = parse_bool(&setting.value);
                }
                "EnableAccessKey" => {
                    config.enable_access_key = parse_bool(&setting.value);
                }
                "AccessKey" => config.access_key = setting.value,
                "IpWhitelist" => config.ip_whitelist = setting.value,
                _ => {}
            }
        }
        Ok(config)
    }
}

fn parse_bool(value: &str) -> bool {
    value.trim().eq_ignore_ascii_case("true")
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum SecurityConfigError {
    #[error("安全配置读取失败: {0}")]
    Store(String),
    #[error("IP 白名单规则无效: {0}")]
    InvalidIpRule(String),
    #[error("启用 LAN 访问时必须配置有效 IP 白名单或非空访问密钥")]
    UnsafeLanConfiguration,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IpWhitelist {
    rules: Vec<IpRule>,
}

impl IpWhitelist {
    pub fn parse(raw: &str) -> Result<Self, SecurityConfigError> {
        let mut rules = Vec::new();
        for value in raw
            .split([',', '\n', '\r'])
            .map(str::trim)
            .filter(|value| !value.is_empty())
        {
            rules.push(IpRule::parse(value)?);
        }
        Ok(Self { rules })
    }

    fn has_rules(&self) -> bool {
        !self.rules.is_empty()
    }

    fn is_allowed(&self, address: IpAddr) -> bool {
        self.rules.iter().any(|rule| rule.matches(address))
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum IpRule {
    V4 { network: u32, mask: u32 },
    V6 { network: u128, mask: u128 },
}

impl IpRule {
    fn parse(raw: &str) -> Result<Self, SecurityConfigError> {
        let (address, prefix) = match raw.split_once('/') {
            Some((address, prefix)) => {
                let address = address
                    .parse::<IpAddr>()
                    .map_err(|_| SecurityConfigError::InvalidIpRule(raw.to_string()))?;
                let prefix = prefix
                    .parse::<u8>()
                    .map_err(|_| SecurityConfigError::InvalidIpRule(raw.to_string()))?;
                (address, prefix)
            }
            None => {
                let address = raw
                    .parse::<IpAddr>()
                    .map_err(|_| SecurityConfigError::InvalidIpRule(raw.to_string()))?;
                let prefix = if address.is_ipv4() { 32 } else { 128 };
                (address, prefix)
            }
        };

        match normalize_ip(address) {
            IpAddr::V4(address) if prefix <= 32 => {
                let mask = prefix_mask_v4(prefix);
                Ok(Self::V4 {
                    network: u32::from(address) & mask,
                    mask,
                })
            }
            IpAddr::V6(address) if prefix <= 128 => {
                let mask = prefix_mask_v6(prefix);
                Ok(Self::V6 {
                    network: u128::from(address) & mask,
                    mask,
                })
            }
            _ => Err(SecurityConfigError::InvalidIpRule(raw.to_string())),
        }
    }

    fn matches(&self, address: IpAddr) -> bool {
        match (self, normalize_ip(address)) {
            (Self::V4 { network, mask }, IpAddr::V4(address)) => {
                u32::from(address) & mask == *network
            }
            (Self::V6 { network, mask }, IpAddr::V6(address)) => {
                u128::from(address) & mask == *network
            }
            _ => false,
        }
    }
}

fn prefix_mask_v4(prefix: u8) -> u32 {
    if prefix == 0 {
        0
    } else {
        u32::MAX << (32 - u32::from(prefix))
    }
}

fn prefix_mask_v6(prefix: u8) -> u128 {
    if prefix == 0 {
        0
    } else {
        u128::MAX << (128 - u32::from(prefix))
    }
}

fn normalize_ip(address: IpAddr) -> IpAddr {
    match address {
        IpAddr::V6(address) => address
            .to_ipv4_mapped()
            .map(IpAddr::V4)
            .unwrap_or(IpAddr::V6(address)),
        address => address,
    }
}

#[derive(Debug, Clone)]
struct SecurityState {
    config: Arc<SecurityConfig>,
    whitelist: Arc<IpWhitelist>,
}

pub fn web_app(
    state: AppState,
    config: SecurityConfig,
    wwwroot: impl AsRef<Path>,
) -> Result<Router, SecurityConfigError> {
    let whitelist = IpWhitelist::parse(&config.ip_whitelist)?;
    validate_lan_security(&config, &whitelist)?;
    let wwwroot = wwwroot.as_ref();
    let static_files =
        ServeDir::new(wwwroot).not_found_service(ServeFile::new(wwwroot.join("index.html")));
    let security = SecurityState {
        config: Arc::new(config),
        whitelist: Arc::new(whitelist),
    };

    Ok(routes(state)
        .fallback_service(static_files)
        .layer(middleware::from_fn_with_state(security, authorize_request)))
}

fn validate_lan_security(
    config: &SecurityConfig,
    whitelist: &IpWhitelist,
) -> Result<(), SecurityConfigError> {
    if config.enable_lan_access
        && !whitelist.has_rules()
        && (!config.enable_access_key || config.access_key.trim().is_empty())
    {
        return Err(SecurityConfigError::UnsafeLanConfiguration);
    }
    Ok(())
}

async fn authorize_request(
    State(security): State<SecurityState>,
    ConnectInfo(peer): ConnectInfo<std::net::SocketAddr>,
    request: Request,
    next: Next,
) -> Response {
    let client_ip = normalize_ip(peer.ip());
    if client_ip.is_loopback() {
        return next.run(request).await;
    }

    if security.whitelist.has_rules() && !security.whitelist.is_allowed(client_ip) {
        return (StatusCode::FORBIDDEN, "Access denied: IP not in whitelist").into_response();
    }

    if is_protected_path(request.uri().path())
        && security.config.enable_access_key
        && request.method() != Method::OPTIONS
    {
        if security.config.access_key.trim().is_empty() {
            return (
                StatusCode::SERVICE_UNAVAILABLE,
                "Access denied: Access key is enabled but not configured",
            )
                .into_response();
        }

        let provided = provided_access_key(&request);
        if !provided.is_some_and(|provided| {
            fixed_time_equals(provided.as_bytes(), security.config.access_key.as_bytes())
        }) {
            return (
                StatusCode::UNAUTHORIZED,
                "Access denied: Invalid access key",
            )
                .into_response();
        }
    }

    next.run(request).await
}

fn is_protected_path(path: &str) -> bool {
    path == "/api" || path.starts_with("/api/") || path == "/hubs" || path.starts_with("/hubs/")
}

fn provided_access_key(request: &Request<Body>) -> Option<Cow<'_, str>> {
    if let Some(value) = request
        .headers()
        .get("x-access-key")
        .and_then(|value| value.to_str().ok())
        .map(str::trim)
        .filter(|value| !value.is_empty())
    {
        return Some(Cow::Borrowed(value));
    }

    if let Some(value) = request
        .headers()
        .get(AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| {
            let bytes = value.as_bytes();
            (bytes.len() >= 7 && bytes[..7].eq_ignore_ascii_case(b"bearer "))
                .then(|| value[7..].trim())
        })
        .filter(|value| !value.is_empty())
    {
        return Some(Cow::Borrowed(value));
    }

    request.uri().query().and_then(|query| {
        url::form_urlencoded::parse(query.as_bytes())
            .find_map(|(key, value)| (key == "access_token").then_some(value))
    })
}

fn fixed_time_equals(left: &[u8], right: &[u8]) -> bool {
    let max_len = left.len().max(right.len());
    let mut difference = left.len() ^ right.len();
    for index in 0..max_len {
        let left_byte = left.get(index).copied().unwrap_or(0);
        let right_byte = right.get(index).copied().unwrap_or(0);
        difference |= usize::from(left_byte ^ right_byte);
    }
    difference == 0
}

#[cfg(test)]
mod tests {
    use std::net::SocketAddr;

    use axum::{
        body::Body,
        http::{Method, Request},
        routing::get,
        Router,
    };
    use http_body_util::BodyExt;
    use tower::ServiceExt;

    use super::*;

    fn secured_router(config: SecurityConfig) -> Router {
        let security = SecurityState {
            whitelist: Arc::new(IpWhitelist::parse(&config.ip_whitelist).unwrap()),
            config: Arc::new(config),
        };
        Router::new()
            .route("/api/check", get(|| async { "ok" }))
            .route("/hubs/check", get(|| async { "ok" }))
            .route("/index.html", get(|| async { "index" }))
            .layer(middleware::from_fn_with_state(security, authorize_request))
    }

    fn request(method: Method, path: &str, peer: &str) -> Request<Body> {
        let peer: SocketAddr = peer.parse().unwrap();
        let mut request = Request::builder()
            .method(method)
            .uri(path)
            .body(Body::empty())
            .unwrap();
        request.extensions_mut().insert(ConnectInfo(peer));
        request
    }

    #[test]
    fn whitelist_matches_exact_cidr_and_mapped_ipv4() {
        let whitelist = IpWhitelist::parse("192.168.1.12,10.0.0.0/8,2001:db8::/32").unwrap();

        assert!(whitelist.is_allowed("192.168.1.12".parse().unwrap()));
        assert!(whitelist.is_allowed("10.22.3.4".parse().unwrap()));
        assert!(whitelist.is_allowed("2001:db8::42".parse().unwrap()));
        assert!(whitelist.is_allowed("::ffff:10.3.4.5".parse().unwrap()));
        assert!(!whitelist.is_allowed("192.168.1.13".parse().unwrap()));
    }

    #[test]
    fn whitelist_rejects_invalid_rules_prefixes_and_empty_input() {
        assert_eq!(
            IpWhitelist::parse("192.168.1.0/40"),
            Err(SecurityConfigError::InvalidIpRule(
                "192.168.1.0/40".to_string()
            ))
        );
        assert!(IpWhitelist::parse("not-an-ip").is_err());

        let empty = IpWhitelist::parse("").unwrap();
        assert!(!empty.is_allowed("192.168.1.8".parse().unwrap()));
    }

    #[test]
    fn lan_requires_a_whitelist_or_access_key() {
        let whitelist = IpWhitelist::parse("").unwrap();
        let unsafe_config = SecurityConfig {
            enable_lan_access: true,
            ..SecurityConfig::default()
        };
        assert_eq!(
            validate_lan_security(&unsafe_config, &whitelist),
            Err(SecurityConfigError::UnsafeLanConfiguration)
        );

        let access_key_config = SecurityConfig {
            enable_lan_access: true,
            enable_access_key: true,
            access_key: "secret".to_string(),
            ..SecurityConfig::default()
        };
        assert_eq!(
            validate_lan_security(&access_key_config, &whitelist),
            Ok(())
        );
    }

    #[tokio::test]
    async fn loopback_bypasses_access_key() {
        let app = secured_router(SecurityConfig {
            enable_access_key: true,
            access_key: "secret".to_string(),
            ..SecurityConfig::default()
        });

        let response = app
            .oneshot(request(Method::GET, "/api/check", "127.0.0.1:50000"))
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn remote_api_requires_access_key_but_static_files_do_not() {
        let app = secured_router(SecurityConfig {
            enable_access_key: true,
            access_key: "s3cret".to_string(),
            ..SecurityConfig::default()
        });

        let unauthorized = app
            .clone()
            .oneshot(request(Method::GET, "/api/check", "192.168.1.8:50000"))
            .await
            .unwrap();
        assert_eq!(unauthorized.status(), StatusCode::UNAUTHORIZED);

        let mut authorized = request(Method::GET, "/api/check", "192.168.1.8:50000");
        authorized
            .headers_mut()
            .insert("x-access-key", "s3cret".parse().unwrap());
        let authorized = app.clone().oneshot(authorized).await.unwrap();
        assert_eq!(authorized.status(), StatusCode::OK);

        let static_file = app
            .oneshot(request(Method::GET, "/index.html", "192.168.1.8:50000"))
            .await
            .unwrap();
        assert_eq!(static_file.status(), StatusCode::OK);
        assert_eq!(
            static_file.into_body().collect().await.unwrap().to_bytes(),
            "index"
        );
    }

    #[tokio::test]
    async fn remote_api_accepts_bearer_and_signalr_query_tokens() {
        let app = secured_router(SecurityConfig {
            enable_access_key: true,
            access_key: "s3cret".to_string(),
            ..SecurityConfig::default()
        });

        let mut bearer = request(Method::GET, "/api/check", "192.168.1.8:50000");
        bearer
            .headers_mut()
            .insert(AUTHORIZATION, "bearer s3cret".parse().unwrap());
        assert_eq!(
            app.clone().oneshot(bearer).await.unwrap().status(),
            StatusCode::OK
        );

        let query = request(
            Method::GET,
            "/hubs/check?access_token=s3cret",
            "192.168.1.8:50000",
        );
        assert_eq!(app.oneshot(query).await.unwrap().status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn remote_whitelist_denies_before_access_key() {
        let app = secured_router(SecurityConfig {
            enable_access_key: true,
            access_key: "secret".to_string(),
            ip_whitelist: "10.0.0.0/8".to_string(),
            ..SecurityConfig::default()
        });
        let mut request = request(Method::GET, "/api/check", "192.168.1.8:50000");
        request
            .headers_mut()
            .insert("x-access-key", "secret".parse().unwrap());

        let response = app.oneshot(request).await.unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }
}
