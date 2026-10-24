# SDD ledger — plan: plans/113-partial-publication-follow-up.md
Base: 51ee23bcc1dcc44355c1a364fbfd6584c0a06dcd; branch plan-113; isolated D:/Dev/sqlharness/.worktrees/plan-113.
Original checkout dirty README.md, plans/README.md and untracked plans 111/112/113 preserved. Plan 113 copied verbatim into isolated tree.
Spec: plan Contract and task assertions.
Preflight:
| Tasks | Producer / consumer | Finding |
| 1 | Source inspection / evidence | Consistent; no production edit |
| 2 | Existing focused tests / evidence | MCP filter may discover zero tests; inspect discovery before claiming coverage |
| 3 | Evidence / diagnostic decision | Consistent; no live causality claim |
| 1,2 | Source and coverage / confirmation | Sequential, shared evidence only |
| 1,3 | Verified historical state / disposition | Preserve original unknown cause |
| 2,3 | Focused outcomes / final decision | No recurrence inferred from synthetic tests |
Ruling: MCP zero-test run is recorded as unavailable evidence; locate applicable MCP tests and run if present, never claim zero discovery as pass — plan requires intended tests discovered — risk is incomplete MCP coverage, explicitly disclosed.
Ruling: Existing test baseline runs as Task 2 because Task 1 is inspection-only — no executable change precedes it — risk is delayed discovery of baseline failures.
Drift scan: mandated five paths unchanged from planned revision to base.
Task 1 BASE: 51ee23bcc1dcc44355c1a364fbfd6584c0a06dcd
Task 1 implementation: 49f4ea96; historical binary/cause concerns are diagnostic limitations, not correctness blockers; proceed to review.
Task 1: complete (commits 51ee23bc..49f4ea96, review clean)
Task 2 BASE: 49f4ea96
Ruling: Revise plan-mandated MCP publisher/journal test-discovery criterion to N/A for those Core-owned behaviors; retain exact zero-discovery log and separately report two MCP projection checks — no such MCP suite exists and Contract permits existing offline Core coverage — cost if wrong is an undiscovered MCP integration gap, not proven by these fixtures.
Task 2: fix round 1/5 (2 addressed, 0 open — MCP criterion N/A explicit; observed audit wording; commits b244823a..10b27690)
Task 2: complete (commits 49f4ea96..10b27690, review clean)
Task 3 BASE: 10b27690
