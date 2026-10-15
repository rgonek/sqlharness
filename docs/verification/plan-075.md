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

Pending final review and sequential Windows/Linux verification. This evidence is for the feature branch, not an integrated main tree.
