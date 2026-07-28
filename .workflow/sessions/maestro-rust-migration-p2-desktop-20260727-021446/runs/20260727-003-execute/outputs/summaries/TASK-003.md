# TASK-003

Status: completed

Implemented executable-directory `service-endpoints.json` discovery with PascalCase wrapper parsing, canonical 35181 fallback, `/api/v1/events` derivation, and 300 ms health probing across the preferred port through `+10`. `SignalRUrl` is read for compatibility only. Added the existing config, power, and widget REST routes with preserved non-2xx status/body errors.

Convergence evidence:

- `cargo test -p xhm-desktop config`: 13 passed.
- `cargo test -p xhm-desktop rest`: 9 passed.
- Preferred `+2` discovery fixture: passed.
- Invalid/missing config paths return canonical defaults without panic.
- Wiremock request capture verifies method/path/body casing and shapes.
