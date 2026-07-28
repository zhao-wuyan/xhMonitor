//! UI-neutral Desktop state and deterministic reducer (TASK-004).
//!
//! The reducer owns no I/O or Slint type. Each future desktop window owns an
//! independent state instance and applies lifecycle/data messages received from SSE.

use std::collections::{BTreeMap, HashSet};

use xhm_core::wire::{
    DiskUsagePayload, HardwareLimitsPayload, ProcessMetadataPayload, ProcessMetricSnapshot,
    ProcessSnapshotPayload, PushEvent, SubscriptionMode, SystemUsagePayload,
};

use crate::service_client::SseMessage;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PanelState {
    #[default]
    Collapsed,
    Expanded,
    Locked,
    Clickthrough,
}

impl PanelState {
    /// C# parity: Expanded/Locked request Full; Collapsed/Clickthrough request Lite.
    pub fn subscription_mode(self) -> SubscriptionMode {
        match self {
            Self::Expanded | Self::Locked => SubscriptionMode::Full,
            Self::Collapsed | Self::Clickthrough => SubscriptionMode::Lite,
        }
    }

    pub fn is_details_visible(self) -> bool {
        matches!(self, Self::Expanded | Self::Locked)
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct ProcessRow {
    pub process_id: i32,
    pub process_name: String,
    pub command_line: Option<String>,
    pub display_name: Option<String>,
    pub metrics: BTreeMap<String, f64>,
    pub has_meta: bool,
    pub is_pinned: bool,
}

impl ProcessRow {
    fn from_metric(snapshot: &ProcessMetricSnapshot, is_pinned: bool) -> Self {
        Self {
            process_id: snapshot.process_id,
            process_name: snapshot.process_name.clone(),
            command_line: snapshot.command_line.clone(),
            display_name: snapshot.display_name.clone(),
            metrics: snapshot.metrics.clone(),
            has_meta: snapshot.has_meta,
            is_pinned,
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct DesktopState {
    pub limits: Option<HardwareLimitsPayload>,
    pub usage: Option<SystemUsagePayload>,
    pub panel: PanelState,
    pub connected: bool,
    /// Current PID -> row index for the latest Full or Lite snapshot.
    pub processes: BTreeMap<i32, ProcessRow>,
    /// User-pinned PIDs that still exist in the latest process snapshot.
    pub pinned: Vec<i32>,
}

const TOP_N: usize = 5;

impl DesktopState {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn apply_message(&mut self, message: &SseMessage) {
        match message {
            SseMessage::Connected => self.connected = true,
            SseMessage::Disconnected => self.connected = false,
            SseMessage::Event(event) => self.apply_event(event),
            SseMessage::UnknownEvent { event } => {
                tracing::debug!(event, "skipping unknown SSE event in reducer");
            }
            SseMessage::BadJson { event, error } => {
                tracing::warn!(event, %error, "dropping undecodable SSE frame in reducer");
            }
        }
    }

    pub fn apply_event(&mut self, event: &PushEvent) {
        match event {
            PushEvent::HardwareLimits(payload) => self.limits = Some(payload.clone()),
            PushEvent::SystemUsage(payload) => self.apply_system_usage(payload),
            PushEvent::ProcessMetrics(payload) | PushEvent::ProcessMetricsLite(payload) => {
                self.apply_process_metrics(payload);
            }
            PushEvent::ProcessMetadata(payload) => self.apply_process_metadata(payload),
        }
    }

    fn apply_system_usage(&mut self, payload: &SystemUsagePayload) {
        let mut next = payload.clone();
        if let Some(previous) = &self.usage {
            if next.max_memory <= 0.0 {
                next.max_memory = previous.max_memory;
            }
            if next.max_vram <= 0.0 {
                next.max_vram = previous.max_vram;
            }
        }
        if let Some(limits) = &mut self.limits {
            if next.max_memory > 0.0 {
                limits.max_memory = next.max_memory;
            }
            if next.max_vram > 0.0 {
                limits.max_vram = next.max_vram;
            }
        }
        self.usage = Some(next);
    }

    fn apply_process_metrics(&mut self, payload: &ProcessSnapshotPayload) {
        let pinned: HashSet<i32> = self.pinned.iter().copied().collect();
        let seen: HashSet<i32> = payload
            .processes
            .iter()
            .map(|process| process.process_id)
            .collect();

        for snapshot in &payload.processes {
            let is_pinned = pinned.contains(&snapshot.process_id);
            match self.processes.get_mut(&snapshot.process_id) {
                Some(row) => {
                    let identity_changed = row.process_name != snapshot.process_name;
                    if identity_changed || !snapshot.has_meta {
                        row.command_line = None;
                        row.display_name = None;
                        row.has_meta = false;
                    }
                    row.process_name = snapshot.process_name.clone();
                    row.metrics = snapshot.metrics.clone();
                    if let Some(command_line) = &snapshot.command_line {
                        row.command_line = Some(command_line.clone());
                    }
                    if let Some(display_name) = &snapshot.display_name {
                        row.display_name = Some(display_name.clone());
                    }
                    row.has_meta |= snapshot.has_meta;
                    row.is_pinned = is_pinned;
                }
                None => {
                    self.processes.insert(
                        snapshot.process_id,
                        ProcessRow::from_metric(snapshot, is_pinned),
                    );
                }
            }
        }

        // Full and Lite payloads are both complete for their current routed subset.
        self.processes
            .retain(|process_id, _| seen.contains(process_id));
        self.pinned.retain(|process_id| seen.contains(process_id));
    }

    fn apply_process_metadata(&mut self, payload: &ProcessMetadataPayload) {
        let pinned: HashSet<i32> = self.pinned.iter().copied().collect();
        for snapshot in &payload.processes {
            let is_pinned = pinned.contains(&snapshot.process_id);
            match self.processes.get_mut(&snapshot.process_id) {
                Some(row) => {
                    if row.process_name != snapshot.process_name {
                        row.metrics.clear();
                    }
                    row.process_name = snapshot.process_name.clone();
                    row.command_line = Some(snapshot.command_line.clone());
                    row.display_name = Some(snapshot.display_name.clone());
                    row.has_meta = true;
                    row.is_pinned = is_pinned;
                }
                None => {
                    self.processes.insert(
                        snapshot.process_id,
                        ProcessRow {
                            process_id: snapshot.process_id,
                            process_name: snapshot.process_name.clone(),
                            command_line: Some(snapshot.command_line.clone()),
                            display_name: Some(snapshot.display_name.clone()),
                            metrics: BTreeMap::new(),
                            has_meta: true,
                            is_pinned,
                        },
                    );
                }
            }
        }
    }

    pub fn pin(&mut self, process_id: i32) {
        if process_id <= 0 {
            return;
        }
        if !self.pinned.contains(&process_id) {
            self.pinned.push(process_id);
        }
        if let Some(row) = self.processes.get_mut(&process_id) {
            row.is_pinned = true;
        }
    }

    pub fn unpin(&mut self, process_id: i32) {
        self.pinned.retain(|id| *id != process_id);
        if let Some(row) = self.processes.get_mut(&process_id) {
            row.is_pinned = false;
        }
    }

    pub fn normalized_pinned(&self) -> Vec<i32> {
        xhm_core::wire::normalize_pinned_ids(Some(&self.pinned))
    }

    pub fn top_processes(&self) -> Vec<&ProcessRow> {
        let mut rows: Vec<&ProcessRow> = self.processes.values().collect();
        rows.sort_by(|left, right| {
            let left_memory = left.metrics.get("memory").copied().unwrap_or(0.0);
            let right_memory = right.metrics.get("memory").copied().unwrap_or(0.0);
            right_memory
                .partial_cmp(&left_memory)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        rows.truncate(TOP_N);
        rows
    }

    pub fn pinned_rows(&self) -> Vec<&ProcessRow> {
        self.pinned
            .iter()
            .filter_map(|id| self.processes.get(id))
            .collect()
    }

    pub fn disks(&self) -> &[DiskUsagePayload] {
        self.usage
            .as_ref()
            .map(|usage| usage.disks.as_slice())
            .unwrap_or(&[])
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use xhm_core::wire::{
        ProcessMetadataPayload, ProcessMetadataSnapshot, ProcessMetricSnapshot,
        ProcessSnapshotPayload,
    };

    fn hardware_limits() -> HardwareLimitsPayload {
        HardwareLimitsPayload {
            timestamp: chrono::Local::now(),
            max_memory: 16_384.0,
            max_vram: 8_192.0,
        }
    }

    fn usage(cpu: f64, memory: f64) -> SystemUsagePayload {
        SystemUsagePayload {
            timestamp: chrono::Local::now(),
            total_cpu: cpu,
            total_gpu: 0.0,
            cpu_temperature: None,
            gpu_temperature: None,
            total_memory: memory,
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
        }
    }

    fn process(pid: i32, name: &str, memory: f64) -> ProcessMetricSnapshot {
        ProcessMetricSnapshot {
            process_id: pid,
            process_name: name.to_owned(),
            has_meta: false,
            command_line: None,
            display_name: None,
            metrics: BTreeMap::from([("memory".to_owned(), memory)]),
        }
    }

    fn snapshot(processes: Vec<ProcessMetricSnapshot>) -> ProcessSnapshotPayload {
        ProcessSnapshotPayload {
            timestamp: chrono::Local::now(),
            process_count: processes.len() as i32,
            processes,
        }
    }

    fn metadata(pid: i32, name: &str) -> ProcessMetadataSnapshot {
        ProcessMetadataSnapshot {
            process_id: pid,
            process_name: name.to_owned(),
            command_line: format!("/{name}"),
            display_name: name.to_uppercase(),
        }
    }

    #[test]
    fn default_panel_is_collapsed_lite() {
        let state = DesktopState::new();
        assert_eq!(state.panel, PanelState::Collapsed);
        assert_eq!(state.panel.subscription_mode(), SubscriptionMode::Lite);
        assert!(!state.panel.is_details_visible());
        assert_eq!(
            PanelState::Expanded.subscription_mode(),
            SubscriptionMode::Full
        );
        assert_eq!(
            PanelState::Locked.subscription_mode(),
            SubscriptionMode::Full
        );
        assert_eq!(
            PanelState::Clickthrough.subscription_mode(),
            SubscriptionMode::Lite
        );
    }

    #[test]
    fn connection_messages_toggle_state() {
        let mut state = DesktopState::new();
        state.apply_message(&SseMessage::Connected);
        assert!(state.connected);
        state.apply_message(&SseMessage::Disconnected);
        assert!(!state.connected);
    }

    #[test]
    fn hardware_limits_update_state() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::HardwareLimits(hardware_limits()));
        assert_eq!(state.limits.as_ref().unwrap().max_memory, 16_384.0);
    }

    #[test]
    fn zero_usage_limits_preserve_prior_maxima() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::HardwareLimits(hardware_limits()));
        let mut first = usage(10.0, 8_000.0);
        first.max_memory = 32_768.0;
        first.max_vram = 16_384.0;
        state.apply_event(&PushEvent::SystemUsage(first));
        state.apply_event(&PushEvent::SystemUsage(usage(20.0, 9_000.0)));
        let current = state.usage.as_ref().unwrap();
        assert_eq!(current.total_cpu, 20.0);
        assert_eq!(current.max_memory, 32_768.0);
        assert_eq!(current.max_vram, 16_384.0);
        assert_eq!(state.limits.as_ref().unwrap().max_memory, 32_768.0);
    }

    #[test]
    fn positive_usage_limits_replace_prior_maxima() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::HardwareLimits(hardware_limits()));
        let mut current = usage(10.0, 8_000.0);
        current.max_memory = 40_000.0;
        current.max_vram = 20_000.0;
        state.apply_event(&PushEvent::SystemUsage(current));
        assert_eq!(state.limits.as_ref().unwrap().max_memory, 40_000.0);
    }

    #[test]
    fn full_snapshot_removes_disappeared_rows() {
        let mut state = DesktopState::new();
        state.panel = PanelState::Expanded;
        state.apply_event(&PushEvent::ProcessMetrics(snapshot(vec![
            process(1, "a", 100.0),
            process(2, "b", 50.0),
        ])));
        state.apply_event(&PushEvent::ProcessMetrics(snapshot(vec![process(
            1, "a", 120.0,
        )])));
        assert_eq!(state.processes.len(), 1);
        assert_eq!(state.processes[&1].metrics["memory"], 120.0);
    }

    #[test]
    fn lite_snapshot_removes_stale_rows_and_pins() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetricsLite(snapshot(vec![process(
            1, "a", 100.0,
        )])));
        state.pin(1);
        state.apply_event(&PushEvent::ProcessMetricsLite(snapshot(vec![process(
            2, "b", 50.0,
        )])));
        assert!(!state.processes.contains_key(&1));
        assert!(state.processes.contains_key(&2));
        assert!(state.pinned.is_empty());
    }

    #[test]
    fn metadata_before_metrics_is_preserved_when_has_meta_is_true() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetadata(ProcessMetadataPayload {
            timestamp: chrono::Local::now(),
            process_count: 1,
            processes: vec![metadata(42, "app")],
        }));
        let mut metric = process(42, "app", 10.0);
        metric.has_meta = true;
        state.apply_event(&PushEvent::ProcessMetrics(snapshot(vec![metric])));
        let row = &state.processes[&42];
        assert_eq!(row.display_name.as_deref(), Some("APP"));
        assert!(row.has_meta);
    }

    #[test]
    fn pid_reuse_clears_old_metadata() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetadata(ProcessMetadataPayload {
            timestamp: chrono::Local::now(),
            process_count: 1,
            processes: vec![metadata(42, "old-app")],
        }));
        state.apply_event(&PushEvent::ProcessMetrics(snapshot(vec![process(
            42, "new-app", 20.0,
        )])));
        let row = &state.processes[&42];
        assert_eq!(row.process_name, "new-app");
        assert_eq!(row.command_line, None);
        assert_eq!(row.display_name, None);
        assert!(!row.has_meta);
    }

    #[test]
    fn metadata_pid_reuse_clears_old_metrics() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetrics(snapshot(vec![process(
            42, "old-app", 20.0,
        )])));
        state.apply_event(&PushEvent::ProcessMetadata(ProcessMetadataPayload {
            timestamp: chrono::Local::now(),
            process_count: 1,
            processes: vec![metadata(42, "new-app")],
        }));
        assert!(state.processes[&42].metrics.is_empty());
    }

    #[test]
    fn pin_unpin_and_normalization_are_deterministic() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetrics(snapshot(vec![
            process(3, "a", 10.0),
            process(7, "b", 20.0),
        ])));
        state.pin(7);
        state.pin(3);
        state.pin(7);
        state.pin(-1);
        assert_eq!(state.normalized_pinned(), vec![3, 7]);
        assert_eq!(
            state
                .pinned_rows()
                .iter()
                .map(|row| row.process_id)
                .collect::<Vec<_>>(),
            vec![7, 3]
        );
        state.unpin(7);
        assert!(!state.processes[&7].is_pinned);
    }

    #[test]
    fn top_processes_sort_by_memory_and_limit_five() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::ProcessMetrics(snapshot(
            (1..=7).map(|pid| process(pid, "app", pid as f64)).collect(),
        )));
        let top = state.top_processes();
        assert_eq!(top.len(), 5);
        assert_eq!(top[0].process_id, 7);
        assert_eq!(top[4].process_id, 3);
    }

    #[test]
    fn unknown_and_bad_json_do_not_change_state() {
        let mut state = DesktopState::new();
        state.apply_event(&PushEvent::HardwareLimits(hardware_limits()));
        let before = state.clone();
        state.apply_message(&SseMessage::UnknownEvent {
            event: "Future".to_owned(),
        });
        state.apply_message(&SseMessage::BadJson {
            event: "ReceiveSystemUsage".to_owned(),
            error: "schema".to_owned(),
        });
        assert_eq!(state, before);
    }

    #[test]
    fn event_message_routes_and_disks_default_empty() {
        let mut state = DesktopState::new();
        assert!(state.disks().is_empty());
        state.apply_message(&SseMessage::Event(PushEvent::HardwareLimits(
            hardware_limits(),
        )));
        assert!(state.limits.is_some());
    }
}
