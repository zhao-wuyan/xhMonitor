---
kind: report
schema: report/1.0
run_id: 20260728-014-test
session_id: maestro-rust-migration-p2-desktop-20260727-021446
target: xhm-desktop
verdict: pass
summary: "xhm-desktop Cargo test suite passed: 128 passed, 0 failed."
smoke_tests_run: 1
smoke_tests_passed: 1
uat_tests_total: 128
uat_tests_passed: 128
uat_tests_issues: 0
uat_tests_skipped: 0
coverage_percentage: 100.0
details:
  evidence_source: current-execution+latest-review+latest-debug+direct-test-execution
needs:
  - latest-test
---

# 20260728-014-test — xhm-desktop Test Report

## Scope

Test-only validation of the `xhm-desktop` crate. Product source remained read-only; artifacts are confined to this Run's output directory.

## Results

=== UAT RESULTS ===
Target:      xhm-desktop
Smoke Tests: 1 run, 1 passed
UAT Tests:   128 total
  Passed:    128
  Issues:    0 (0 blockers, 0 major)
  Skipped:   0
Diagnosis:   0/0 gaps diagnosed

Primary command: `cargo test -p xhm-desktop`
Observed result: 128 passed, 0 failed, 0 ignored across 3 test binaries.

## Coverage

All mapped sources have corresponding scenarios: current execution, latest review findings, and latest debug diagnosis. Scenario coverage is 100%; no requirements are uncovered.

## Gates

- `coverage-met`: pass; 3/3 mapped scenario sources, 100% coverage.
- `pass-rate-met`: pass; 128 passed, 0 issues, `needs_retry: false`.

## Handoff

Verdict: pass. The test artifacts are complete and the Run remains unsealed for the caller's next decision.
