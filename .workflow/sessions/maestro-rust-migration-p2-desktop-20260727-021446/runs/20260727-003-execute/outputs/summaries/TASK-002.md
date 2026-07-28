# TASK-002

Status: completed with manual concern

The G1 spike compiles `tray-icon` 0.24.1 and uses only its official `TrayIconBuilder`, `MenuEvent::set_event_handler`, and `TrayIconEvent::set_event_handler` APIs. Seven stable command IDs flow through an `mpsc` bridge; callbacks never touch Slint.

Convergence evidence:

- `cargo test -p xhm-desktop tray`: 6 passed.
- `Cargo.lock`: `tray-icon` 0.24.1.
- Source inspection: official builder/handlers; no `Shell_NotifyIcon` implementation or competing tray path.

Decision: GO for the crate API and active-loop queue boundary. The real Windows menu/checked/double-click/notification/exit matrix remains a G2 manual concern because this Run explicitly excludes production tray UI; it is not claimed as observed here.
