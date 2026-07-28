# TASK-001

Status: completed

Created the `xhm-desktop` workspace package with inherited package metadata, lib/bin targets, one `shell.slint` compilation root, and a bootstrap-only executable entrypoint. Default startup sets `SLINT_BACKEND=winit-software` only when the caller did not set it and emits an observable backend log.

Convergence evidence:

- `cargo metadata --no-deps --format-version 1`: `xhm-core`, `xhm-service`, and `xhm-desktop` present; desktop targets are lib/bin/custom-build.
- `cargo check -p xhm-desktop`: passed.
- `cargo run -p xhm-desktop` + Orca state: nonblank 260x72 window titled `xhm-desktop`; visible `winit-software`; closed after observation.
- `src/main.rs`: only calls `xhm_desktop::bootstrap()`.
- `cargo test -p xhm-desktop` passed three consecutive full-suite runs (55/55 each) after fixing shared `SLINT_BACKEND` test state with a global mutex and panic-safe exact-value restore guard.
