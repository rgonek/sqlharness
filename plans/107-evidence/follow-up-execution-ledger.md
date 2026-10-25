# SDD ledger — plan: plans/107-evidence/107-list-runtime-source-follow-up.md

Base: 81d0fa59. Worktree: D:/Dev/sqlharness/.worktrees/plan-107-followup. Branch: plan-107-followup.
Models: implementation gpt-6-luna high; task review gpt-6-luna xhigh; whole-branch gpt-6-sol low (explicit user routing).

## Preflight
| Task/pair | Produces/consumes | Result |
|---|---|---|
| Task 1 | Compare list classification plus Core and real-SDK MCP contract tests | Follow-up scope and coverage agree; preserve independent caller collision rejection, zero connections and historical diagnostic evidence. |

Ruling: Execute the unnumbered follow-up as one coherent Task 1, using its complete text as the task brief — production repair and expected-contract tests share the same interface — if wrong, task granularity may require rework.
Ruling: The follow-up document is the binding specification; no separate spec is linked — all required coverage and scope are explicit — if wrong, omitted external requirements would need another pass.

Task 1: IMPLEMENTED in `a8aded98`; final gate and branch format passed; Luna xhigh task review approved; Sol low whole-branch review pending. BASE=81d0fa59.

Source diagnosis: `PrepareFixed` preflight lacked set-level list inputs; per-case validation treated repeated setup/baseline/candidate generated bindings as independent parameters. The caller @Ids1 collision remains independently rejected.

Core RED: 4/4 expected-success contract cases failed with Safety before the repair. MCP RED: 12 real-SDK requests showed 11 ordinary cases rejected and the caller collision rejected before its intended collision check; zero connections.

Core GREEN: 5 passed (list forms, singleton/multi/empty named sets, setup reuse, caller collision, zero factory opens). MCP GREEN: 1 passed across 12 served requests; ordinary cases succeeded, caller @Ids1 collision remained rejected, zero connections.

See `task-1-report.md` for exact commands, gate logs, and completion report. The first post-fix gate recorded one unrelated process-marker timeout; its unchanged isolated rerun and the final full gate passed. Do not claim a proven root cause for that observed timeout.

Task 1: Luna xhigh review a8aded98 approved spec and quality; no findings. Cannot-verify items: execution evidence and final gate pending; controller will inspect non-empty stage logs before completing task.

Task 1: complete (commits 81d0fa59..6d6afada, review clean). Controller inspected final2 logs: Core4718/11skip, MCP535/4skip, UI372; just verify exit0 stages5 and branch format base81d0fa59 format OK. Cannot-verify items resolved by non-empty logs. Final whole-branch review pending.

Whole-branch review: Sol low approved 6d6afada, no findings.
Implementer DONE_WITH_CONCERNS adjudication: prior process-marker timeout is an observation preserved in raw evidence; unchanged focused rerun and final full gate passed; no source correctness/scope concern remains.
Ruling: Live-database execution remains outside acceptance — the follow-up requires offline dry-runs and forbids database use — if wrong, live behavior needs separate validation.
Ruling: Historical loaded-build attribution remains unverified — this repair proves current-source behavior only — if wrong, deployed-runtime attribution needs another investigation.
Ruling: Do not diagnose or modify the unrelated process-marker timeout in this follow-up — unchanged isolated rerun and final full gate passed; root cause remains unproven — if wrong, the intermittent failure may recur and require separate work.
