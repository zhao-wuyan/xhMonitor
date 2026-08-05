use std::{
    collections::{HashMap, HashSet},
    fmt, io,
    path::{Path, PathBuf},
    sync::Arc,
};

use tokio::sync::{broadcast, RwLock};
use xhm_core::{
    traits::{Clock, LhmReader, MetricStore, RyzenAdjClient},
    wire::{normalize_pinned_ids, SubscriptionMode},
};

pub const DEFAULT_SERVICE_PORT: u16 = 35_179;
pub const DEFAULT_HUB_PATH: &str = "/hubs/metrics";
pub const DEFAULT_SSE_PATH: &str = "/api/v1/events";
pub const DEFAULT_ALLOWED_ORIGINS: [&str; 4] = [
    "http://localhost:3000",
    "http://localhost:5173",
    "http://localhost:35180",
    "app://.",
];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RuntimeConfig {
    pub interval_seconds: u64,
    pub process_keywords: Vec<String>,
    pub plugin_directory: PathBuf,
    pub port: u16,
    pub hub_path: String,
    pub sse_path: String,
    pub allowed_origins: Vec<String>,
}

impl Default for RuntimeConfig {
    fn default() -> Self {
        Self {
            interval_seconds: 1,
            process_keywords: Vec::new(),
            plugin_directory: PathBuf::from("plugins"),
            port: DEFAULT_SERVICE_PORT,
            hub_path: DEFAULT_HUB_PATH.to_owned(),
            sse_path: DEFAULT_SSE_PATH.to_owned(),
            allowed_origins: DEFAULT_ALLOWED_ORIGINS
                .iter()
                .map(|origin| (*origin).to_owned())
                .collect(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServicePaths {
    pub exe_dir: PathBuf,
    pub db_path: PathBuf,
    pub widget_config_path: PathBuf,
    pub lhm_bridge_path: PathBuf,
    pub wwwroot_path: PathBuf,
    pub ryzenadj_dll_path: PathBuf,
    pub ryzenadj_exe_path: PathBuf,
}

impl ServicePaths {
    pub fn new() -> io::Result<Self> {
        let executable = std::env::current_exe()?;
        let exe_dir = executable.parent().ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "current executable has no parent directory",
            )
        })?;

        let mut paths = Self::for_exe_dir(exe_dir);
        let web_root_configured = if let Some(configured) = std::env::var_os("XHM_WEB_ROOT") {
            paths.wwwroot_path = PathBuf::from(configured);
            true
        } else {
            false
        };

        if let Ok(project_root) = std::env::current_dir() {
            let is_source_checkout = project_root.join("Cargo.toml").is_file()
                && project_root.join("lhm-bridge/lhm-bridge.csproj").is_file();
            if is_source_checkout {
                if !web_root_configured {
                    let development_web_root = project_root.join("xhmonitor-web").join("dist");
                    if development_web_root.join("index.html").is_file() {
                        paths.wwwroot_path = development_web_root;
                    }
                }

                let preferred_configurations = if exe_dir
                    .file_name()
                    .is_some_and(|name| name.eq_ignore_ascii_case("release"))
                {
                    ["Release", "Debug"]
                } else {
                    ["Debug", "Release"]
                };
                for configuration in preferred_configurations {
                    let candidate = project_root
                        .join("lhm-bridge")
                        .join("bin")
                        .join(configuration)
                        .join("net8.0")
                        .join("win-x64")
                        .join("lhm-bridge.exe");
                    if candidate.is_file() {
                        paths.lhm_bridge_path = candidate;
                        break;
                    }
                }

                let development_ryzenadj = project_root.join("tools").join("RyzenAdj");
                paths.ryzenadj_dll_path = development_ryzenadj.join("libryzenadj.dll");
                paths.ryzenadj_exe_path = development_ryzenadj.join("ryzenadj.exe");
            }
        }
        Ok(paths)
    }

    pub fn for_exe_dir(exe_dir: impl AsRef<Path>) -> Self {
        let exe_dir = exe_dir.as_ref().to_path_buf();
        let ryzenadj_dir = exe_dir.join("tools").join("RyzenAdj");

        Self {
            db_path: exe_dir.join("xhmonitor.db"),
            widget_config_path: exe_dir.join("data").join("widget-settings.json"),
            lhm_bridge_path: exe_dir.join("lhm-bridge.exe"),
            wwwroot_path: exe_dir.join("wwwroot"),
            ryzenadj_dll_path: ryzenadj_dir.join("libryzenadj.dll"),
            ryzenadj_exe_path: ryzenadj_dir.join("ryzenadj.exe"),
            exe_dir,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PushTarget {
    All,
    Full,
    Connection(String),
}

#[derive(Debug, Clone)]
pub struct RoutedPushEvent {
    pub target: PushTarget,
    pub event: xhm_core::wire::PushEvent,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessSubscription {
    pub mode: SubscriptionMode,
    pub pinned_process_ids: Vec<i32>,
}

impl Default for ProcessSubscription {
    fn default() -> Self {
        Self {
            mode: SubscriptionMode::Full,
            pinned_process_ids: Vec::new(),
        }
    }
}

impl ProcessSubscription {
    pub fn set(&mut self, mode: SubscriptionMode, raw_pinned: Option<&[i32]>) {
        self.mode = mode;
        self.pinned_process_ids = match mode {
            SubscriptionMode::Full => Vec::new(),
            SubscriptionMode::Lite => normalize_pinned_ids(raw_pinned),
        };
    }
}

#[derive(Default, Debug)]
pub struct RealtimeRegistry {
    pending_connections: HashSet<String>,
    subscriptions: HashMap<String, ProcessSubscription>,
}

impl RealtimeRegistry {
    pub fn add_pending(&mut self, token: String) -> bool {
        self.pending_connections.insert(token)
    }

    pub fn consume_and_register(&mut self, token: &str, connection_id: String) -> bool {
        if !self.pending_connections.remove(token) {
            return false;
        }

        self.subscriptions
            .insert(connection_id, ProcessSubscription::default());
        true
    }

    pub fn register_direct(&mut self, connection_id: String) {
        self.subscriptions
            .insert(connection_id, ProcessSubscription::default());
    }

    pub fn set_subscription(
        &mut self,
        connection_id: &str,
        mode: SubscriptionMode,
        raw_pinned: Option<&[i32]>,
    ) -> bool {
        let Some(subscription) = self.subscriptions.get_mut(connection_id) else {
            return false;
        };

        subscription.set(mode, raw_pinned);
        true
    }

    pub fn disconnect(&mut self, connection_id: &str) -> bool {
        self.subscriptions.remove(connection_id).is_some()
    }

    pub fn subscription(&self, connection_id: &str) -> Option<&ProcessSubscription> {
        self.subscriptions.get(connection_id)
    }
    pub fn subscriptions_snapshot(&self) -> Vec<(String, ProcessSubscription)> {
        self.subscriptions
            .iter()
            .map(|(connection_id, subscription)| (connection_id.clone(), subscription.clone()))
            .collect()
    }
}

#[derive(Clone)]
pub struct AppState {
    pub store: Arc<dyn MetricStore>,
    pub clock: Arc<dyn Clock>,
    pub lhm: Arc<dyn LhmReader>,
    pub ryzenadj: Arc<dyn RyzenAdjClient>,
    pub paths: Arc<ServicePaths>,
    pub runtime: Arc<RwLock<RuntimeConfig>>,
    pub push_tx: broadcast::Sender<RoutedPushEvent>,
    pub realtime: Arc<RwLock<RealtimeRegistry>>,
}

impl AppState {
    pub fn new(
        store: Arc<dyn MetricStore>,
        clock: Arc<dyn Clock>,
        lhm: Arc<dyn LhmReader>,
        ryzenadj: Arc<dyn RyzenAdjClient>,
        paths: ServicePaths,
        runtime: RuntimeConfig,
    ) -> Self {
        let (push_tx, _) = broadcast::channel(256);

        Self {
            store,
            clock,
            lhm,
            ryzenadj,
            paths: Arc::new(paths),
            runtime: Arc::new(RwLock::new(runtime)),
            push_tx,
            realtime: Arc::new(RwLock::new(RealtimeRegistry::default())),
        }
    }
}

impl fmt::Debug for AppState {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("AppState")
            .field("store", &"<dyn MetricStore>")
            .field("clock", &"<dyn Clock>")
            .field("lhm", &"<dyn LhmReader>")
            .field("ryzenadj", &"<dyn RyzenAdjClient>")
            .field("paths", &self.paths)
            .field("runtime", &self.runtime)
            .field("push_tx", &"<broadcast::Sender<RoutedPushEvent>>")
            .field("realtime", &"<RwLock<RealtimeRegistry>>")
            .finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn process_subscription_defaults_to_full_without_pinned_processes() {
        let subscription = ProcessSubscription::default();

        assert_eq!(subscription.mode, SubscriptionMode::Full);
        assert!(subscription.pinned_process_ids.is_empty());
    }

    #[test]
    fn lite_process_subscription_normalizes_pinned_processes() {
        let mut subscription = ProcessSubscription::default();

        subscription.set(SubscriptionMode::Lite, Some(&[7, -1, 3, 7, 0, 2]));

        assert_eq!(subscription.mode, SubscriptionMode::Lite);
        assert_eq!(subscription.pinned_process_ids, [2, 3, 7]);
    }

    #[test]
    fn switching_process_subscription_to_full_clears_pinned_processes() {
        let mut subscription = ProcessSubscription::default();
        subscription.set(SubscriptionMode::Lite, Some(&[2, 3, 7]));

        subscription.set(SubscriptionMode::Full, Some(&[11, 13]));

        assert_eq!(subscription.mode, SubscriptionMode::Full);
        assert!(subscription.pinned_process_ids.is_empty());
    }

    #[test]
    fn runtime_config_uses_service_defaults() {
        let config = RuntimeConfig::default();

        assert_eq!(config.interval_seconds, 1);
        assert!(config.process_keywords.is_empty());
        assert_eq!(config.plugin_directory, PathBuf::from("plugins"));
        assert_eq!(config.port, 35_179);
        assert_eq!(config.hub_path, "/hubs/metrics");
        assert_eq!(config.sse_path, "/api/v1/events");
        assert_eq!(
            config.allowed_origins,
            [
                "http://localhost:3000",
                "http://localhost:5173",
                "http://localhost:35180",
                "app://.",
            ]
        );
    }

    #[test]
    fn service_paths_are_relative_to_injected_exe_dir() {
        let exe_dir = PathBuf::from("injected-service-directory");
        let paths = ServicePaths::for_exe_dir(&exe_dir);

        assert_eq!(paths.exe_dir, exe_dir);
        assert_eq!(paths.db_path, exe_dir.join("xhmonitor.db"));
        assert_eq!(
            paths.widget_config_path,
            exe_dir.join("data").join("widget-settings.json")
        );
        assert_eq!(paths.lhm_bridge_path, exe_dir.join("lhm-bridge.exe"));
        assert_eq!(paths.wwwroot_path, exe_dir.join("wwwroot"));
        assert_eq!(
            paths.ryzenadj_dll_path,
            exe_dir
                .join("tools")
                .join("RyzenAdj")
                .join("libryzenadj.dll")
        );
        assert_eq!(
            paths.ryzenadj_exe_path,
            exe_dir.join("tools").join("RyzenAdj").join("ryzenadj.exe")
        );

        for derived_path in [
            &paths.db_path,
            &paths.widget_config_path,
            &paths.lhm_bridge_path,
            &paths.wwwroot_path,
            &paths.ryzenadj_dll_path,
            &paths.ryzenadj_exe_path,
        ] {
            assert!(derived_path.starts_with(&paths.exe_dir));
        }
    }

    #[test]
    fn pending_token_can_only_be_consumed_once() {
        let mut registry = RealtimeRegistry::default();
        assert!(registry.add_pending("token".to_owned()));

        assert!(registry.consume_and_register("token", "first".to_owned()));
        assert!(!registry.consume_and_register("token", "second".to_owned()));
        assert!(registry.subscription("second").is_none());
    }

    #[test]
    fn consuming_pending_token_registers_default_full_subscription() {
        let mut registry = RealtimeRegistry::default();
        registry.add_pending("token".to_owned());

        assert!(registry.consume_and_register("token", "connection".to_owned()));

        let subscription = registry.subscription("connection").unwrap();
        assert_eq!(subscription.mode, SubscriptionMode::Full);
        assert!(subscription.pinned_process_ids.is_empty());
    }

    #[test]
    fn direct_connection_registers_default_subscription() {
        let mut registry = RealtimeRegistry::default();

        registry.register_direct("connection".to_owned());

        assert_eq!(
            registry.subscription("connection"),
            Some(&ProcessSubscription::default())
        );
    }

    #[test]
    fn registry_normalizes_lite_subscription_updates() {
        let mut registry = RealtimeRegistry::default();
        registry.register_direct("connection".to_owned());

        assert!(registry.set_subscription(
            "connection",
            SubscriptionMode::Lite,
            Some(&[7, -1, 3, 7, 0, 2]),
        ));

        let subscription = registry.subscription("connection").unwrap();
        assert_eq!(subscription.mode, SubscriptionMode::Lite);
        assert_eq!(subscription.pinned_process_ids, [2, 3, 7]);
    }

    #[test]
    fn disconnect_removes_registered_subscription() {
        let mut registry = RealtimeRegistry::default();
        registry.register_direct("connection".to_owned());

        assert!(registry.disconnect("connection"));
        assert!(registry.subscription("connection").is_none());
        assert!(!registry.disconnect("connection"));
    }

    #[test]
    fn unknown_connection_subscription_update_returns_false() {
        let mut registry = RealtimeRegistry::default();

        assert!(!registry.set_subscription("missing", SubscriptionMode::Lite, Some(&[2, 3, 7]),));
    }
}
