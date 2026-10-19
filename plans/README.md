# SQLHarness plan queue

Read the selected plan and its spec before executing. A queue entry is not approval for implementation, publication or a live DB. We execute each assigned plan in full; general work and SQL Server take priority over PostgreSQL-only work.

## In progress

| Plan | Scope | Dependencies | Status |
|---|---|---|---|
| [086](086-windows-gate-speedup.md) | Shortening the Windows gate: restore built into the build, `ui-check` in parallel with the build, parallel test projects, faster journal seeding in read tests, diagnosable flaky housekeeping, removal of the xunit attributes test | 076 DONE | IMPLEMENTED on `opt-windows-gate` — see [evidence](086-verification-evidence.md) |

## Pending

| Plan | Scope | Dependencies | Status |
|---|---|---|---|
| [036](036-live-database-ci-lane.md) | Direction: opt-in CI lane against throwaway PG/SQL Server containers | 016 (028 for pins) | TODO |
| [077](077-agent-sql-optimization-evidence.md) | Lean MCP JSON and actionable SQL optimization evidence; portable real-MCP fixture requests and paired agent trials verify the complete workflow | 055–061/065–067/076 DONE; coordinate 063/073 | IN PROGRESS — implementation merged locally into main (`4bd57e4`); actual-agent acceptance unverified (nine provider HTTP 402 errors) |
| [078](078-mutation-approval-webauthn.md) | Deferred Windows Hello/WebAuthn verification, enrollment, credential lifecycle and recovery for mutation approvals | 073 DONE | TODO |
| [082](082-statement-io-attribution.md) | Stage C per-statement IO from actual plans reconciled with STATISTICS IO; shared vs changed statements; verdicts on changed statements when confirmed; by-statement view and hidden-work flags | 079, 080, 081, 087 merged; post-081 UAT spike | IN PROGRESS — Task 0 spike complete; Worktable attribution is partial and fail-closed ([spike evidence](082-spike-evidence.md)) |
| [083](083-table-and-list-parameters.md) | Stage D table-valued parameters (inline rows or rows from a read-only query, catalog-resolved types, Structured binding) and Dapper list expansion across read-only tools; conditions, labels and SSMS scripts; mutation rejects them | 077, 079 merged | IMPLEMENTATION VERIFIED; locally integrated into `main` at `55d23ba`; final scoped Sol review approved; live UAT pending; Linux waived ([evidence](083-verification-evidence.md)) |
| [084](084-agent-sessions.md) | Agent sessions across MCP/CLI sessions (matching on): per-operation membership exact/inferred/unassigned, immediate Codex linking + cursor fix, event-driven Claude matching, agent-session list/page/annotations, Optimization per agent session with frozen version labels, subagents folded into parent | 079, 080, 087 merged | TODO |
| [085](085-operation-annotations.md) | Opt-in agent operation labels/keep at call time + post-hoc annotation tool by operationRef scoped to the agent session; owner operation star/note and Named operations view | 077, 079, 084 merged | TODO |

Plans 063, 070, 071, 072, 073, 074, 076, 079, 080, 081, and 087 are DONE; see their evidence in [history](history.md). Plan 081 is locally integrated into `main` at `e88c7fd`; its integrated gate passed on the exact product tree before commit, and all task, security, whole-branch, and integration reviews are approved ([evidence](081-verification-evidence.md)). Plan 088 is DONE and locally integrated into `main`; all task reviews, the Sol low whole-branch review, and the final scoped re-review are approved. Its Windows gate passed on `f503c22`, and the merge retained that product tree without a separate integrated-tree gate ([history](history.md), [evidence](088-verification-evidence.md)). Plan 071 uses Monaco behind a provider-neutral SQL editor contract, with direct Monaco API lifecycle management for the diff widget. Plan 080's scoped re-review approved all findings, and local integration completed at `42a1e365` with the verified product unchanged; the redundant gate was stopped at the owner's request. Coordinate work that changes shared MCP, journal, and dashboard contracts through the selected plan.

## History

Evidence from completed plans, former queues, rejected proposals and audit decisions are in the [history](history.md). Read it when investigating earlier decisions or evidence, not before every task.

When a plan is completed, update its status, move the entry from the queue to the history and keep the link to the evidence.
