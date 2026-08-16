//! Native network adapter throughput collection and selection.

use std::{
    collections::HashMap,
    fmt,
    time::{Duration, Instant},
};

const BYTES_PER_MIB: f64 = 1024.0 * 1024.0;
const MIN_SAMPLE_INTERVAL: Duration = Duration::from_millis(100);
const IF_TYPE_ETHERNET_CSMACD: u32 = 6;
const IF_TYPE_IEEE80211: u32 = 71;
// MIB_IF_ROW2 InterfaceAndOperStatusFlags bit positions defined by netioapi.h.
const MIB_IF_ROW2_FILTER_INTERFACE_MASK: u8 = 1 << 1;
const MIB_IF_ROW2_NOT_MEDIA_CONNECTED_MASK: u8 = 1 << 4;
const VIRTUAL_ADAPTER_KEYWORDS: &[&str] = &[
    "virtual",
    "vethernet",
    "hyper-v",
    "vmware",
    "tap-",
    "vpn",
    "radmin",
    "loopback",
    "pseudo",
    "wireguard",
    "openvpn",
    "tun",
    "fortinet",
    "cisco anyconnect",
    "teamviewer",
    "anydesk",
    "kernel",
    "wi-fi direct",
    "wan miniport",
    "wsl",
    "docker",
    "tailscale",
    "zerotier",
    "easytier",
    "wintun",
    "teredo",
    "6to4",
    "ip-https",
];

/// Upload and download rates in MiB/s for the service wire payload.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct NetworkThroughput {
    pub upload_mib_per_second: f64,
    pub download_mib_per_second: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AdapterCategory {
    Physical,
    Virtual,
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AggregationMode {
    Sum,
    Max,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
struct AdapterIdentity {
    interface_index: u32,
    interface_guid: u128,
}

impl fmt::Display for AdapterIdentity {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "{}:{:032x}",
            self.interface_index, self.interface_guid
        )
    }
}

#[derive(Debug, Clone)]
struct AdapterCounters {
    identity: AdapterIdentity,
    name: String,
    description: String,
    interface_type: u32,
    eligible: bool,
    in_octets: u64,
    out_octets: u64,
}

#[derive(Debug, Clone, Copy)]
struct PreviousCounters {
    observed_at: Instant,
    in_octets: u64,
    out_octets: u64,
}

#[derive(Debug, Clone)]
struct AdapterRate {
    identity: AdapterIdentity,
    name: String,
    category: AdapterCategory,
    upload_bytes_per_second: f64,
    download_bytes_per_second: f64,
}

impl AdapterRate {
    fn total_bytes_per_second(&self) -> f64 {
        self.upload_bytes_per_second + self.download_bytes_per_second
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct SelectionKey {
    category: AdapterCategory,
    adapter_ids: Vec<AdapterIdentity>,
}

#[derive(Debug)]
struct NetworkSelection {
    key: SelectionKey,
    aggregation: AggregationMode,
    adapter_names: Vec<String>,
    upload_bytes_per_second: f64,
    download_bytes_per_second: f64,
}

impl NetworkSelection {
    fn throughput(&self) -> NetworkThroughput {
        NetworkThroughput {
            upload_mib_per_second: self.upload_bytes_per_second / BYTES_PER_MIB,
            download_mib_per_second: self.download_bytes_per_second / BYTES_PER_MIB,
        }
    }

    fn reason(&self) -> &'static str {
        match (self.key.category, self.aggregation) {
            (AdapterCategory::Physical, AggregationMode::Sum) => {
                "positive-rate physical adapters have priority and are summed"
            }
            (AdapterCategory::Virtual, AggregationMode::Max) => {
                "no positive-rate physical adapter; highest-total virtual adapter selected"
            }
            (AdapterCategory::Unknown, AggregationMode::Sum) => {
                "no positive-rate physical or virtual adapter; unknown adapters summed"
            }
            _ => "network adapter selection changed",
        }
    }
}

/// Stateful sampler that derives rates from cumulative native interface counters.
#[derive(Debug)]
pub struct NetworkMonitor {
    previous: HashMap<AdapterIdentity, PreviousCounters>,
    selection_state: SelectionState,
    enumeration_failure_logged: bool,
}

#[derive(Debug, Default)]
struct SelectionState {
    previous_selection: Option<SelectionKey>,
    no_eligible_logged: bool,
}

#[derive(Debug, PartialEq, Eq)]
enum SelectionTransition {
    InitialSelection,
    SelectionChanged { previous: SelectionKey },
    NoEligible,
    Unchanged,
}

impl SelectionState {
    fn transition(
        &mut self,
        eligible_count: usize,
        selection: Option<&NetworkSelection>,
    ) -> SelectionTransition {
        if eligible_count == 0 {
            self.previous_selection = None;
            if self.no_eligible_logged {
                return SelectionTransition::Unchanged;
            }
            self.no_eligible_logged = true;
            return SelectionTransition::NoEligible;
        }

        self.no_eligible_logged = false;
        let Some(selected) = selection else {
            return SelectionTransition::Unchanged;
        };
        let current = selected.key.clone();
        match self.previous_selection.replace(current.clone()) {
            None => SelectionTransition::InitialSelection,
            Some(previous) if previous != current => {
                SelectionTransition::SelectionChanged { previous }
            }
            Some(_) => SelectionTransition::Unchanged,
        }
    }
}

impl Default for NetworkMonitor {
    fn default() -> Self {
        Self::new()
    }
}

impl NetworkMonitor {
    /// Creates a monitor, seeds its counter baseline, and logs the startup inventory.
    pub fn new() -> Self {
        let observed_at = Instant::now();
        let mut previous = HashMap::new();
        let mut selection_state = SelectionState::default();
        let mut enumeration_failure_logged = false;
        match platform::enumerate_adapters() {
            Ok(adapters) => {
                previous = capture_previous_counters(&adapters, observed_at);
                let inventory = adapters
                    .iter()
                    .map(|adapter| {
                        (
                            adapter.identity.to_string(),
                            adapter.name.as_str(),
                            adapter.description.as_str(),
                            adapter.interface_type,
                            adapter.eligible,
                            classify_adapter_metadata(
                                &adapter.name,
                                &adapter.description,
                                adapter.interface_type,
                            ),
                        )
                    })
                    .collect::<Vec<_>>();
                tracing::info!(
                    adapter_count = inventory.len(),
                    adapters = ?inventory,
                    selection_basis = "positive-rate physical(sum) > virtual(max-total) > unknown(sum)",
                    "native network adapter inventory"
                );
                if selection_state.transition(previous.len(), None)
                    == SelectionTransition::NoEligible
                {
                    tracing::warn!("no eligible native network adapter found");
                }
            }
            Err(error) => {
                enumeration_failure_logged = true;
                tracing::warn!(%error, "failed to enumerate native network adapters");
            }
        }

        Self {
            previous,
            selection_state,
            enumeration_failure_logged,
        }
    }

    /// Samples cumulative counters and returns the selected aggregate in MiB/s.
    pub fn sample(&mut self) -> NetworkThroughput {
        let observed_at = Instant::now();
        let adapters = match platform::enumerate_adapters() {
            Ok(adapters) => {
                self.enumeration_failure_logged = false;
                adapters
            }
            Err(error) => {
                if !self.enumeration_failure_logged {
                    tracing::warn!(%error, "failed to enumerate native network adapters");
                    self.enumeration_failure_logged = true;
                }
                return NetworkThroughput::default();
            }
        };
        let rates = calculate_rates(&self.previous, &adapters, observed_at);
        self.previous = capture_previous_counters(&adapters, observed_at);

        let selection = select_throughput(&rates);
        match self
            .selection_state
            .transition(rates.len(), selection.as_ref())
        {
            SelectionTransition::InitialSelection => {
                let selected = selection
                    .as_ref()
                    .expect("selection transition requires selection");
                tracing::info!(
                    current = ?selected.key,
                    reason = selected.reason(),
                    adapters = ?selected.adapter_names,
                    "native network adapter selected"
                );
            }
            SelectionTransition::SelectionChanged { previous } => {
                let selected = selection
                    .as_ref()
                    .expect("selection transition requires selection");
                tracing::info!(
                    ?previous,
                    current = ?selected.key,
                    reason = selected.reason(),
                    adapters = ?selected.adapter_names,
                    "native network adapter selection changed"
                );
            }
            SelectionTransition::NoEligible => {
                tracing::warn!("no eligible native network adapter found");
            }
            SelectionTransition::Unchanged => {}
        }

        selection
            .map(|selected| selected.throughput())
            .unwrap_or_default()
    }
}

fn should_include_interface_row(flags: u8) -> bool {
    flags & MIB_IF_ROW2_FILTER_INTERFACE_MASK == 0
}

fn is_adapter_eligible(is_loopback: bool, oper_status_is_up: bool, flags: u8) -> bool {
    !is_loopback && oper_status_is_up && flags & MIB_IF_ROW2_NOT_MEDIA_CONNECTED_MASK == 0
}

fn classify_adapter(name: &str, interface_type: u32) -> AdapterCategory {
    if VIRTUAL_ADAPTER_KEYWORDS
        .iter()
        .any(|keyword| contains_ascii_case_insensitive(name, keyword))
    {
        return AdapterCategory::Virtual;
    }

    if matches!(interface_type, IF_TYPE_ETHERNET_CSMACD | IF_TYPE_IEEE80211) {
        AdapterCategory::Physical
    } else {
        AdapterCategory::Unknown
    }
}

fn classify_adapter_metadata(
    name: &str,
    description: &str,
    interface_type: u32,
) -> AdapterCategory {
    if VIRTUAL_ADAPTER_KEYWORDS
        .iter()
        .any(|keyword| contains_ascii_case_insensitive(description, keyword))
    {
        AdapterCategory::Virtual
    } else {
        classify_adapter(name, interface_type)
    }
}

fn contains_ascii_case_insensitive(haystack: &str, needle: &str) -> bool {
    haystack
        .as_bytes()
        .windows(needle.len())
        .any(|candidate| candidate.eq_ignore_ascii_case(needle.as_bytes()))
}

fn rate_from_counters(
    previous: Option<&PreviousCounters>,
    current_in_octets: u64,
    current_out_octets: u64,
    observed_at: Instant,
) -> (f64, f64) {
    let Some(previous) = previous else {
        return (0.0, 0.0);
    };
    let elapsed = observed_at.saturating_duration_since(previous.observed_at);
    if elapsed < MIN_SAMPLE_INTERVAL
        || current_in_octets < previous.in_octets
        || current_out_octets < previous.out_octets
    {
        return (0.0, 0.0);
    }

    let elapsed_seconds = elapsed.as_secs_f64();
    (
        (current_out_octets - previous.out_octets) as f64 / elapsed_seconds,
        (current_in_octets - previous.in_octets) as f64 / elapsed_seconds,
    )
}
fn capture_previous_counters(
    adapters: &[AdapterCounters],
    observed_at: Instant,
) -> HashMap<AdapterIdentity, PreviousCounters> {
    adapters
        .iter()
        .filter(|adapter| adapter.eligible)
        .map(|adapter| {
            (
                adapter.identity,
                PreviousCounters {
                    observed_at,
                    in_octets: adapter.in_octets,
                    out_octets: adapter.out_octets,
                },
            )
        })
        .collect()
}

fn calculate_rates(
    previous: &HashMap<AdapterIdentity, PreviousCounters>,
    adapters: &[AdapterCounters],
    observed_at: Instant,
) -> Vec<AdapterRate> {
    adapters
        .iter()
        .filter(|adapter| adapter.eligible)
        .map(|adapter| {
            let (upload_bytes_per_second, download_bytes_per_second) = rate_from_counters(
                previous.get(&adapter.identity),
                adapter.in_octets,
                adapter.out_octets,
                observed_at,
            );
            AdapterRate {
                identity: adapter.identity,
                name: adapter.name.clone(),
                category: classify_adapter_metadata(
                    &adapter.name,
                    &adapter.description,
                    adapter.interface_type,
                ),
                upload_bytes_per_second,
                download_bytes_per_second,
            }
        })
        .collect()
}

fn has_positive_rate(rate: &AdapterRate) -> bool {
    rate.upload_bytes_per_second.is_finite()
        && rate.download_bytes_per_second.is_finite()
        && rate.total_bytes_per_second() > 0.0
}

fn select_throughput(rates: &[AdapterRate]) -> Option<NetworkSelection> {
    if rates
        .iter()
        .any(|rate| rate.category == AdapterCategory::Physical && has_positive_rate(rate))
    {
        return Some(sum_selection(
            AdapterCategory::Physical,
            rates
                .iter()
                .filter(|rate| rate.category == AdapterCategory::Physical)
                .collect(),
        ));
    }

    if let Some(best) = rates
        .iter()
        .filter(|rate| rate.category == AdapterCategory::Virtual && has_positive_rate(rate))
        .max_by(|left, right| {
            left.total_bytes_per_second()
                .total_cmp(&right.total_bytes_per_second())
                .then_with(|| right.identity.cmp(&left.identity))
        })
    {
        return Some(NetworkSelection {
            key: SelectionKey {
                category: AdapterCategory::Virtual,
                adapter_ids: vec![best.identity],
            },
            aggregation: AggregationMode::Max,
            adapter_names: vec![best.name.clone()],
            upload_bytes_per_second: best.upload_bytes_per_second,
            download_bytes_per_second: best.download_bytes_per_second,
        });
    }

    if rates
        .iter()
        .any(|rate| rate.category == AdapterCategory::Unknown && has_positive_rate(rate))
    {
        Some(sum_selection(
            AdapterCategory::Unknown,
            rates
                .iter()
                .filter(|rate| rate.category == AdapterCategory::Unknown)
                .collect(),
        ))
    } else {
        None
    }
}

fn sum_selection(category: AdapterCategory, adapters: Vec<&AdapterRate>) -> NetworkSelection {
    let mut adapter_ids = adapters
        .iter()
        .map(|adapter| adapter.identity)
        .collect::<Vec<_>>();
    adapter_ids.sort_unstable();
    NetworkSelection {
        key: SelectionKey {
            category,
            adapter_ids,
        },
        aggregation: AggregationMode::Sum,
        adapter_names: adapters
            .iter()
            .map(|adapter| adapter.name.clone())
            .collect(),
        upload_bytes_per_second: adapters
            .iter()
            .filter(|adapter| has_positive_rate(adapter))
            .map(|adapter| adapter.upload_bytes_per_second)
            .sum(),
        download_bytes_per_second: adapters
            .iter()
            .filter(|adapter| has_positive_rate(adapter))
            .map(|adapter| adapter.download_bytes_per_second)
            .sum(),
    }
}

#[cfg(windows)]
mod platform {
    use std::{io, ptr, slice};

    use windows_sys::{
        core::GUID,
        Win32::{
            Foundation::ERROR_SUCCESS,
            NetworkManagement::{
                IpHelper::{
                    FreeMibTable, GetIfTable2, IF_TYPE_SOFTWARE_LOOPBACK, MIB_IF_ROW2,
                    MIB_IF_TABLE2,
                },
                Ndis::NET_IF_OPER_STATUS_UP,
            },
        },
    };

    use super::{
        is_adapter_eligible, should_include_interface_row, AdapterCounters, AdapterIdentity,
    };

    struct MibIfTable(*mut MIB_IF_TABLE2);

    impl MibIfTable {
        fn rows(&self) -> &[MIB_IF_ROW2] {
            // SAFETY: the guard only exists for a non-null table returned by GetIfTable2,
            // and it keeps that allocation alive for the borrowed slice.
            let table = unsafe { &*self.0 };
            let count = table.NumEntries as usize;
            let first = table.Table.as_ptr();
            // SAFETY: GetIfTable2 allocates one contiguous MIB_IF_TABLE2 whose Table
            // storage contains NumEntries rows and remains owned by this guard.
            unsafe { slice::from_raw_parts(first, count) }
        }
    }

    impl Drop for MibIfTable {
        fn drop(&mut self) {
            // SAFETY: this is the exact allocation returned by a successful GetIfTable2
            // call and the guard is its sole owner.
            unsafe { FreeMibTable(self.0.cast()) };
        }
    }

    pub(super) fn enumerate_adapters() -> io::Result<Vec<AdapterCounters>> {
        let mut table = ptr::null_mut();
        // SAFETY: GetIfTable2 initializes table on success. The returned allocation is
        // immediately transferred to MibIfTable and released with FreeMibTable.
        let status = unsafe { GetIfTable2(&mut table) };
        if status != ERROR_SUCCESS {
            if !table.is_null() {
                // SAFETY: defensive cleanup if the API supplied an allocation with an
                // error status; FreeMibTable is the required matching deallocator.
                unsafe { FreeMibTable(table.cast()) };
            }
            return Err(io::Error::from_raw_os_error(status as i32));
        }
        if table.is_null() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "GetIfTable2 returned a null table",
            ));
        }
        let table = MibIfTable(table);

        Ok(table
            .rows()
            .iter()
            .filter(|row| should_include_interface_row(row.InterfaceAndOperStatusFlags._bitfield))
            .map(|row| {
                let alias = decode_utf16(&row.Alias);
                let description = decode_utf16(&row.Description);
                let name = if alias.is_empty() {
                    description.clone()
                } else {
                    alias
                };
                AdapterCounters {
                    identity: AdapterIdentity {
                        interface_index: row.InterfaceIndex,
                        interface_guid: guid_to_u128(row.InterfaceGuid),
                    },
                    name,
                    description,
                    interface_type: row.Type,
                    eligible: is_adapter_eligible(
                        row.Type == IF_TYPE_SOFTWARE_LOOPBACK,
                        row.OperStatus == NET_IF_OPER_STATUS_UP,
                        row.InterfaceAndOperStatusFlags._bitfield,
                    ),
                    in_octets: row.InOctets,
                    out_octets: row.OutOctets,
                }
            })
            .collect())
    }

    fn decode_utf16(buffer: &[u16]) -> String {
        let length = buffer
            .iter()
            .position(|character| *character == 0)
            .unwrap_or(buffer.len());
        String::from_utf16_lossy(&buffer[..length])
    }

    fn guid_to_u128(guid: GUID) -> u128 {
        ((guid.data1 as u128) << 96)
            | ((guid.data2 as u128) << 80)
            | ((guid.data3 as u128) << 64)
            | u64::from_be_bytes(guid.data4) as u128
    }
}

#[cfg(not(windows))]
mod platform {
    use std::io;

    use super::AdapterCounters;

    pub(super) fn enumerate_adapters() -> io::Result<Vec<AdapterCounters>> {
        Ok(Vec::new())
    }
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;

    fn identity(index: u32) -> AdapterIdentity {
        AdapterIdentity {
            interface_index: index,
            interface_guid: u128::from(index),
        }
    }

    fn rate(
        index: u32,
        name: &str,
        category: AdapterCategory,
        upload: f64,
        download: f64,
    ) -> AdapterRate {
        AdapterRate {
            identity: identity(index),
            name: name.to_owned(),
            category,
            upload_bytes_per_second: upload,
            download_bytes_per_second: download,
        }
    }
    #[test]
    fn idle_or_empty_eligible_rates_have_no_throughput_selection() {
        assert!(select_throughput(&[]).is_none());
        assert!(select_throughput(&[
            rate(1, "Ethernet", AdapterCategory::Physical, 0.0, 0.0),
            rate(2, "VPN", AdapterCategory::Virtual, 0.0, 0.0),
            rate(3, "Other", AdapterCategory::Unknown, 0.0, 0.0),
        ])
        .is_none());
    }

    #[test]
    fn selection_state_logs_initial_hit_then_retains_selection_while_idle() {
        let mut state = SelectionState::default();
        assert_eq!(state.transition(1, None), SelectionTransition::Unchanged);

        let selected = select_throughput(&[
            rate(1, "Ethernet", AdapterCategory::Physical, 10.0, 20.0),
            rate(2, "Wi-Fi", AdapterCategory::Physical, 0.0, 0.0),
        ])
        .unwrap();
        assert_eq!(
            state.transition(2, Some(&selected)),
            SelectionTransition::InitialSelection
        );
        let selected_key = selected.key.clone();

        assert_eq!(state.transition(2, None), SelectionTransition::Unchanged);
        assert_eq!(state.previous_selection, Some(selected_key.clone()));
        assert_eq!(
            state.transition(2, Some(&selected)),
            SelectionTransition::Unchanged
        );

        let changed = select_throughput(&[
            rate(1, "Ethernet", AdapterCategory::Physical, 0.0, 0.0),
            rate(3, "VPN", AdapterCategory::Virtual, 30.0, 40.0),
        ])
        .unwrap();
        assert_eq!(
            state.transition(2, Some(&changed)),
            SelectionTransition::SelectionChanged {
                previous: selected_key
            }
        );
    }

    #[test]
    fn no_eligible_warning_guard_resets_after_eligible_recovery() {
        let mut state = SelectionState::default();
        assert_eq!(state.transition(0, None), SelectionTransition::NoEligible);
        assert_eq!(state.transition(0, None), SelectionTransition::Unchanged);

        assert_eq!(state.transition(1, None), SelectionTransition::Unchanged);
        assert_eq!(state.transition(0, None), SelectionTransition::NoEligible);
        assert!(state.previous_selection.is_none());
    }

    #[test]
    fn sum_selection_keys_include_idle_members_without_adding_throughput() {
        for category in [AdapterCategory::Physical, AdapterCategory::Unknown] {
            let first = select_throughput(&[
                rate(1, "First", category, 10.0, 20.0),
                rate(2, "Second", category, 0.0, 0.0),
            ])
            .unwrap();
            let second = select_throughput(&[
                rate(1, "First", category, 0.0, 0.0),
                rate(2, "Second", category, 30.0, 40.0),
            ])
            .unwrap();

            assert_eq!(first.key, second.key);
            assert_eq!(first.key.adapter_ids, [identity(1), identity(2)]);
            assert_eq!(first.upload_bytes_per_second, 10.0);
            assert_eq!(first.download_bytes_per_second, 20.0);
            assert_eq!(second.upload_bytes_per_second, 30.0);
            assert_eq!(second.download_bytes_per_second, 40.0);
        }
    }

    #[test]
    fn startup_counter_capture_seeds_only_eligible_adapters() {
        let observed_at = Instant::now();
        let eligible_id = identity(1);
        let adapters = [
            AdapterCounters {
                identity: eligible_id,
                name: "Ethernet".to_owned(),
                description: "Physical adapter".to_owned(),
                interface_type: IF_TYPE_ETHERNET_CSMACD,
                eligible: true,
                in_octets: 1_000,
                out_octets: 2_000,
            },
            AdapterCounters {
                identity: identity(2),
                name: "Disconnected".to_owned(),
                description: "Disconnected adapter".to_owned(),
                interface_type: IF_TYPE_ETHERNET_CSMACD,
                eligible: false,
                in_octets: 3_000,
                out_octets: 4_000,
            },
        ];

        let previous = capture_previous_counters(&adapters, observed_at);
        assert_eq!(previous.len(), 1);
        let captured = previous.get(&eligible_id).unwrap();
        assert_eq!(captured.observed_at, observed_at);
        assert_eq!(captured.in_octets, 1_000);
        assert_eq!(captured.out_octets, 2_000);
    }

    #[test]
    fn active_virtual_is_not_shadowed_by_idle_physical() {
        let selected = select_throughput(&[
            rate(1, "Ethernet", AdapterCategory::Physical, 0.0, 0.0),
            rate(2, "vEthernet", AdapterCategory::Virtual, 40.0, 60.0),
        ])
        .unwrap();

        assert_eq!(selected.key.category, AdapterCategory::Virtual);
        assert_eq!(selected.upload_bytes_per_second, 40.0);
        assert_eq!(selected.download_bytes_per_second, 60.0);
    }

    #[test]
    fn positive_physical_adapters_have_priority_and_are_summed() {
        let selected = select_throughput(&[
            rate(1, "Ethernet", AdapterCategory::Physical, 10.0, 20.0),
            rate(2, "Wi-Fi", AdapterCategory::Physical, 30.0, 40.0),
            rate(3, "VPN", AdapterCategory::Virtual, 500.0, 600.0),
        ])
        .unwrap();

        assert_eq!(selected.key.category, AdapterCategory::Physical);
        assert_eq!(selected.aggregation, AggregationMode::Sum);
        assert_eq!(selected.upload_bytes_per_second, 40.0);
        assert_eq!(selected.download_bytes_per_second, 60.0);
    }

    #[test]
    fn virtual_selection_uses_single_highest_total_adapter() {
        let selected = select_throughput(&[
            rate(1, "VPN A", AdapterCategory::Virtual, 90.0, 10.0),
            rate(2, "VPN B", AdapterCategory::Virtual, 30.0, 80.0),
        ])
        .unwrap();

        assert_eq!(selected.aggregation, AggregationMode::Max);
        assert_eq!(selected.adapter_names, ["VPN B"]);
        assert_eq!(selected.upload_bytes_per_second, 30.0);
        assert_eq!(selected.download_bytes_per_second, 80.0);
    }

    #[test]
    fn unknown_adapters_are_summed_as_last_fallback() {
        let selected = select_throughput(&[
            rate(1, "Other A", AdapterCategory::Unknown, 5.0, 6.0),
            rate(2, "Other B", AdapterCategory::Unknown, 7.0, 8.0),
        ])
        .unwrap();

        assert_eq!(selected.key.category, AdapterCategory::Unknown);
        assert_eq!(selected.aggregation, AggregationMode::Sum);
        assert_eq!(selected.upload_bytes_per_second, 12.0);
        assert_eq!(selected.download_bytes_per_second, 14.0);
    }

    #[test]
    fn filter_interface_rows_are_excluded_before_inventory_creation() {
        assert!(should_include_interface_row(0));
        assert!(should_include_interface_row(
            MIB_IF_ROW2_NOT_MEDIA_CONNECTED_MASK
        ));
        assert!(!should_include_interface_row(
            MIB_IF_ROW2_FILTER_INTERFACE_MASK
        ));
        assert!(!should_include_interface_row(
            MIB_IF_ROW2_FILTER_INTERFACE_MASK | MIB_IF_ROW2_NOT_MEDIA_CONNECTED_MASK
        ));
    }

    #[test]
    fn eligibility_requires_up_connected_non_loopback_interface() {
        assert!(is_adapter_eligible(false, true, 0));
        assert!(!is_adapter_eligible(
            false,
            true,
            MIB_IF_ROW2_NOT_MEDIA_CONNECTED_MASK
        ));
        assert!(!is_adapter_eligible(false, false, 0));
        assert!(!is_adapter_eligible(true, true, 0));
    }

    #[test]
    fn virtual_keywords_override_physical_interface_types() {
        for name in [
            "Contoso Virtual Ethernet Adapter",
            "Microsoft Wi-Fi Direct Adapter",
            "WAN Miniport (IP)",
            "WSL",
            "Docker Desktop",
            "Tailscale Tunnel",
            "ZeroTier One",
            "EasyTier",
            "Wintun Userspace Tunnel",
            "Teredo Tunneling Pseudo-Interface",
            "Microsoft 6to4 Adapter",
            "Microsoft IP-HTTPS Platform Adapter",
        ] {
            assert_eq!(
                classify_adapter(name, IF_TYPE_ETHERNET_CSMACD),
                AdapterCategory::Virtual,
                "{name}"
            );
        }
        assert_eq!(
            classify_adapter_metadata(
                "Ethernet 2",
                "WAN Miniport (IKEv2)",
                IF_TYPE_ETHERNET_CSMACD,
            ),
            AdapterCategory::Virtual
        );
    }

    #[test]
    fn physical_ethernet_and_wifi_remain_physical() {
        assert_eq!(
            classify_adapter_metadata(
                "Ethernet",
                "Intel(R) Ethernet Controller I226-V",
                IF_TYPE_ETHERNET_CSMACD,
            ),
            AdapterCategory::Physical
        );
        assert_eq!(
            classify_adapter_metadata("Wi-Fi", "Intel(R) Wi-Fi 7 BE200 320MHz", IF_TYPE_IEEE80211,),
            AdapterCategory::Physical
        );
    }

    #[test]
    fn first_counter_observation_has_zero_rate() {
        let observed_at = Instant::now();
        assert_eq!(
            rate_from_counters(None, 10_000, 20_000, observed_at),
            (0.0, 0.0)
        );
    }

    #[test]
    fn ineligible_adapter_is_excluded_from_rate_candidates() {
        let observed_at = Instant::now();
        let adapter_id = identity(1);
        let previous = HashMap::from([(
            adapter_id,
            PreviousCounters {
                observed_at: observed_at - Duration::from_secs(1),
                in_octets: 1_000,
                out_octets: 2_000,
            },
        )]);
        let adapters = [AdapterCounters {
            identity: adapter_id,
            name: "Disconnected Ethernet".to_owned(),
            description: "Ethernet adapter".to_owned(),
            interface_type: IF_TYPE_ETHERNET_CSMACD,
            eligible: false,
            in_octets: 2_000,
            out_octets: 3_000,
        }];

        assert!(calculate_rates(&previous, &adapters, observed_at).is_empty());
    }

    #[test]
    fn counter_rollback_has_zero_rate() {
        let observed_at = Instant::now();
        let previous = PreviousCounters {
            observed_at: observed_at - Duration::from_secs(1),
            in_octets: 10_000,
            out_octets: 20_000,
        };

        assert_eq!(
            rate_from_counters(Some(&previous), 9_999, 20_100, observed_at),
            (0.0, 0.0)
        );
    }

    #[test]
    fn counter_changes_before_minimum_sample_interval_have_zero_rate() {
        let observed_at = Instant::now();
        let previous = PreviousCounters {
            observed_at: observed_at - (MIN_SAMPLE_INTERVAL - Duration::from_millis(1)),
            in_octets: 1_000,
            out_octets: 2_000,
        };

        assert_eq!(
            rate_from_counters(Some(&previous), 2_000, 3_000, observed_at),
            (0.0, 0.0)
        );
    }

    #[test]
    fn rates_use_actual_monotonic_elapsed_time_and_convert_to_mib() {
        let observed_at = Instant::now();
        let previous = PreviousCounters {
            observed_at: observed_at - Duration::from_millis(500),
            in_octets: 1_000,
            out_octets: 2_000,
        };
        let (upload, download) = rate_from_counters(
            Some(&previous),
            1_000 + 1_048_576,
            2_000 + 524_288,
            observed_at,
        );
        let selected = NetworkSelection {
            key: SelectionKey {
                category: AdapterCategory::Physical,
                adapter_ids: vec![identity(1)],
            },
            aggregation: AggregationMode::Sum,
            adapter_names: vec!["Ethernet".to_owned()],
            upload_bytes_per_second: upload,
            download_bytes_per_second: download,
        }
        .throughput();

        assert_eq!(upload, 1_048_576.0);
        assert_eq!(download, 2_097_152.0);
        assert_eq!(selected.upload_mib_per_second, 1.0);
        assert_eq!(selected.download_mib_per_second, 2.0);
    }
}
