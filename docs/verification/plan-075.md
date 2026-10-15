# Plan 075 verification

Feature branch: `feat/plan-075-dashboard-lifecycle`, based on `d78ee8f`.
All process tests use disposable SQLHARNESS_HOME directories. No operator dashboard was stopped, no installation/publication/push was performed.

## Focused evidence

- Task 1: RED exposed missing shutdown route/host cleanup; GREEN 50 passed. Missing-cookie review fix: 33 server tests passed (`163eac9`, `61ec10b`).
- Task 2: RED missing client API; GREEN lifecycle/lock/host 54 passed, 3 Unix-only skips. Added stale-start and lost-response cases: 17 lifecycle tests passed. Reused-port regression RED returned 202 instead of 409; fixed receiver-side captured PID/start validation and retained stale metadata tests: 90 passed, 3 Unix-only skips (`034d280`, `71ba532`, `3224ff5`).
- Task 3: RED --stop returned 2 instead of 0; GREEN CLI/autostart 23 passed, including real isolated-home restart and authenticated replacement API; MCP autostart 3 passed. Deadline regression RED accepted a late endpoint; fixed monotonic deadline and added failed-stop restart case: 47 passed, 3 Unix-only skips (`254f814`, `acfaf19`).

## Review decision

Lifecycle shutdown carries captured PID/start metadata validated by the receiving server before stopping. Existing authenticated direct shutdown requests remain valid without that metadata. This prevents a same-port replacement sharing the persistent home token from accepting an old lifecycle request. Cost if wrong: protocol/test rework or a conservative refusal to stop an instance.

## Full gates

Final product revision: `1524b4425b1d8384ffd774e0b5e8cadab05e153e`.

- Whole-branch review: Sol low; final scoped review approved after cancellation guards and deterministic deadline regression (`1524b44`). Both cancellation boundary regressions were RED before the fix; final focused run 49 passed, 3 Unix-only skips.
- Windows `just verify -LogDirectory D:/temp/sqlharness-plan075-verify`: PASS, exit 0, six stages in 5m14s. UI 178 passed; Core/CLI 3587 passed, 11 skipped; MCP 386 passed, 4 skipped; build 0 warnings/errors. Format skipped because there were no uncommitted C# changes.
- Linux `just verify-linux`: explicitly waived by the operator on 2026-10-15 while in progress. The task-owned gate process tree was stopped. Sync, SDK, Node, UI install/check, restore and build passed before interruption; no completed test/format result and no Linux gate pass are claimed. This follows `docs/agents/verification.md`, which currently requires the local Windows gate only. Partial logs: `D:/temp/sqlharness-plan075-verify-linux`.

This is feature-branch evidence, not verification of an integrated main tree. No merge, push, installation or publication was performed.

## Remaining scope decisions

- macOS external-PID shutdown remains fail-closed; the acceptance test explicitly skips platforms without an external-process reader. Cost if wrong: macOS stop/restart support needs a later reader implementation.
- Publishing scripts, MCP tools, dashboard UI and journal schema remain outside this plan as explicitly required. Cost if wrong: a desired workflow extension needs a separate change.
