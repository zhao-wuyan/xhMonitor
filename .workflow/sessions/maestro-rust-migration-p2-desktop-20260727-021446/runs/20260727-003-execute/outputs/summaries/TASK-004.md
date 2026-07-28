# TASK-004

Status: completed

Implemented a Slint-independent SSE client for `/api/v1/events` with full/lite and normalized pinned queries, incremental SSE framing, exact decoding of the five xhm-core `PushEvent` names, bounded-channel delivery, retry, and cancellation. Added a pure `DesktopState` reducer for panel subscription mode, limits, usage/disks, process index/top-five, pins, metadata, and connection-ready state.

Convergence evidence:

- `cargo test -p xhm-desktop sse`: 13 passed.
- `cargo test -p xhm-desktop desktop_state`: 15 passed.
- Tests cover split chunks, comments, unknown events, bad JSON, exact five variants, URL normalization, mock end-to-end stream, cancellation, metadata-before-metrics, process disappearance, pin/unpin, and deterministic top-five ordering.

Concern: retry uses the fixed 10 x 2-second contract and cancellation is exercised; a paused-clock request-count test was not added in this Run.
