# SDD ledger — plan: docs/superpowers/plans/2026-09-26-audit-03-agent-interface.md
Base: 10c44f8
Spec: docs/superpowers/specs/2026-09-26-project-audit.md

| Scope | Producer / consumer | Preflight finding |
|---|---|---|
| T1 | Error and output envelope | Internally consistent; legacy JSON success retained. |
| T2 | Offline commands and validation | Internally consistent; depends on T1 and merged 01/02 safety analysis. |
| T3 | Bounded agent projection | Internally consistent; depends on T1 envelope and T2 capabilities limits. |
| T4 | Gain and workflows | Internally consistent; compare artifact detail depends on future 06/T2. |
| T1/T2 | SqlHarnessCli, commands | T2 consumes T1 output mode and error contract. |
| T1/T3 | Renderer, commands, AgentOutputTests | T3 consumes T1 envelope and extends only agent projection. |
| T1/T4 | Renderer, SqlHarnessModule | T4 adds gain fields without changing T1 error contract. |
| T2/T3 | Capabilities, output flags | T3 must add its limits to T2 capability document. |
| T2/T4 | Capabilities workflow | T4 exercises T2 discovery and offline validation. |
| T3/T4 | Agent projection workflow | T4 byte scenarios consume T3 projection. |

Ruling: T4 compare to artifact detail is documented as future 06/T2 contract and tested only where current artifacts exist — 06/T2 is outside this plan; if wrong, that scenario needs revisiting after plan 06.
Task 1: fix round 1/5 (1 addressed, 0 open; commits 8f83448..bd1d72d)
Task 1: complete (commits 10c44f8..bd1d72d, review clean)
Task 2: fix round 1/5 (1 important and 2 minor addressed, 0 open; commits c798c1a..0d67946)
Task 2: complete (commits bd1d72d..0d67946, review clean)
Task 3: complete with one unrelated full-suite process-tree timeout; see task-3-report.md
