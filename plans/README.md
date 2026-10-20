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
| [083](083-table-and-list-parameters.md) | Stage D table-valued parameters (inline rows or rows from a read-only query, catalog-resolved types, Structured binding) and Dapper list expansion across read-only tools; conditions, labels and SSMS scripts; mutation rejects them | 077, 079 merged | IMPLEMENTATION VERIFIED; locally integrated into `main` at `55d23ba`; final scoped Sol review approved; live UAT pending; Linux waived ([evidence](083-verification-evidence.md)) |
| [090](090-matcher-title-backfill-retention.md) | Bound title-backfill state by retained operations and live staged references without losing retry/cursor state | 084 merged | TODO — plan only |
| [091](091-ui-test-network-isolation.md) | Diagnose and remove unintended UI-test network requests; retain explicit error-path coverage | 084 merged | TODO — plan only |
| [092](092-default-execution-timeout-setting.md) | Configurable default SQL execution timeout in config.json and Dashboard Settings; fallback for CLI commands and MCP tools | None | TODO — plan only |
| [093](093-identity-safe-output-clipping.md) | Preserve full metric identity keys under cell clipping; avoid post-execution rendering failures | Existing projection | TODO — P1, plan only |
| [094](094-compact-annotation-receipts.md) | Compact annotation receipts that fit minimum budgets; explicit full detail and one effective budget across commit/render | 085 DONE; execute before 095 | TODO — P1, plan only |
| [095](095-budget-aware-artifact-pages.md) | Explicit artifact response budgets and bounded configurable source pages with complete continuation | 093, 094 implementations merged | TODO — P2, plan only |
| [096](096-bounded-large-parameter-files.md) | Practical typed-parameter files: bounded 16 MiB/file and 32 MiB aggregate raw ingress; retain materialization safeguards | 083 implementation present | TODO — P2, plan only |
| [097](097-singleton-parameter-batches.md) | Accept a one-case named/file/matrix remainder without duplicate work; keep 32-case execution cap | Existing parameters; coordinate 096 | TODO — P2, plan only |
| [098](098-session-operation-pagination.md) | Reach ordinary-session operations beyond the initial 500 through bounded cursor pages | Coordinate 089; no membership changes | TODO — P2, plan only |
| [101](101-dashboard-operation-annotations-setting.md) | Enable or disable agent operation annotations in Dashboard Settings; valid paired journal saves and MCP restart guidance | 085 merged; coordinate 092 | TODO — plan only |

## Limit-economics execution notes (2026-10-20)

Recommended order: **093 → 094 → 095 → 096 → 097**, then **089 → 098** for both session views. The arrows are scheduling advice except for 095's hard implementation predecessors 093 and 094: confirm actual implementations on main, not ancestry alone. 096/097 and 089/098 edit shared files and should not run concurrently; neither pair introduces an artificial semantic dependency.

Use [077's new limit-economics acceptance appendix](077-agent-sql-optimization-evidence.md#limit-economics-acceptance-appendix-2026-10-20) to freeze a baseline before each affected behavior changes and to evaluate whole-task cost afterward. Deterministic fixtures do not require completion of unavailable paid-agent trials, and product implementation does not authorize those trials.

These plans keep finite response/process ceilings, frame/input-root safety, materialization/SQL-parameter bounds and execution deadlines. No blanket limit increase, retention pins or automatic DB retries are proposed. The 300-second command ceiling is a separate operator-policy decision; 092 changes defaults while deliberately preserving it. Do not duplicate 089's agent-session scope or 090's retention-based matcher bookkeeping.

Plans 063, 070, 071, 072, 073, 074, 076, 079, 080, 081, 085, and 087 are DONE; see their evidence in [history](history.md). Plan 081 is locally integrated into `main` at `e88c7fd`; its integrated gate passed on the exact product tree before commit, and all task, security, whole-branch, and integration reviews are approved ([evidence](081-verification-evidence.md)). Plan 088 is DONE and locally integrated into `main`; all task reviews, the Sol low whole-branch review, and the final scoped re-review are approved. Its Windows gate passed on `f503c22`, and the merge retained that product tree without a separate integrated-tree gate ([history](history.md), [evidence](088-verification-evidence.md)). Plan 071 uses Monaco behind a provider-neutral SQL editor contract, with direct Monaco API lifecycle management for the diff widget. Plan 080's scoped re-review approved all findings, and local integration completed at `42a1e365` with the verified product unchanged; the redundant gate was stopped at the owner's request. Plan 085 is locally integrated at `e41c990`; its verified product tree is unchanged by the docs-only Plan 092 merge ([history](history.md), [evidence](085-verification-evidence.md)). Coordinate work that changes shared MCP, journal, and dashboard contracts through the selected plan.


Plan 082 is DONE and locally merged into `main` at `dd37512`. All task reviews and the final Sol low scoped re-review are approved. The Windows gate and branch formatting passed on `b9f6092`; the merge preserves that verified product tree. Synthetic UAT confirmed the focused verdict. Linux was explicitly skipped; see [verification evidence](082-verification-evidence.md) and [history](history.md).

Plan 084 is DONE and locally integrated into main at `67bdd6b` with the owner-accepted detail-cap limitation; the Sol dissent remains recorded. Follow-up plans 089, 090 and 091 cover the parked detail limit and both deferred Minors. See [084 evidence](084-verification-evidence.md) and [history](history.md).

## History

| Plan | Scope | Branch status |
|---|---|---|
| [089](089-agent-session-operation-pagination.md) | Bounded agent-session operation pages and Load more; old exact members remain reachable | DONE on `plan-089`; Windows verified at `75d841d`; all reviews approved; not merged ([evidence](089-verification-evidence.md)) |
| [099](099-shadcn-cli-restoration.md) | Stable shadcn CLI devDependency, supported stylesheet import and contributor workflow | DONE; locally integrated into `main` at `c479210`; Windows-verified product unchanged; all reviews approved ([evidence](099-verification-evidence.md)) |
| [100](100-shadcn-primitive-adoption.md) | Complete dashboard control inventory, accessible shadcn composition, disclosures and standard tables | DONE; locally integrated into `main` at `74f248c`; Windows-verified product revision `3caacc3` unchanged; all task and whole-branch reviews approved ([evidence](100-verification-evidence.md)) |

Evidence from completed plans, former queues, rejected proposals and audit decisions are in the [history](history.md). Read it when investigating earlier decisions or evidence, not before every task.

When a plan is completed, update its status, move the entry from the queue to the history and keep the link to the evidence.
