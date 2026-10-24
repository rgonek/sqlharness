# SDD ledger — plan: plans/106-bounded-error-diagnostics.md

Workspace: C:/Users/rgone/.codex/worktrees/39fb/sqlharness; branch plan-106.
Base: 6d543e509734a374caf882bc8c05ffe5ccaa98b2 (fast-forwarded managed checkout to main before implementation).
Models: Luna high implementers; Luna xhigh task reviewers; Sol low final reviewer. No merge/push/install/live DB authorized.
Source checkout has unrelated README/index edits and untracked plans; preserved. Plan 106 copied only. Worktree inventory inspected. Drift scan of named source paths is empty.
Spec: plan Contract and task assertions.

## Preflight
| Tasks | Producer / consumer or internal consistency | Finding |
|---|---|---|
| 1 | error regressions and narrow fix | Overlaps shared policy extraction in Task 2 |
| 2 | shared policy and edge regressions | Consistent with Contract |
| 3 | docs and full verification | Generic RED clause needs contract-level test |
| 1 / 2 | same MCP/CLI errors | Task 1 pins basic behavior with narrow local change; Task 2 centralizes and adds edges |
| 1 / 3 | observable semantics / documented semantics | Docs follow tested behavior |
| 2 / 3 | policy / schemas and runtime docs | Docs describe shared policy |

Ruling: Task 1 may make the minimal local floor change needed for GREEN; Task 2 extracts it to Core and extends edge coverage — both tasks demand GREEN and Task 2 owns shared policy — cost if wrong is small refactoring rework.
Ruling: Task 3 RED requirement is satisfied by a focused contract-description regression where feasible, without inventing runtime behavior — task is documentation/verification — cost if wrong is an extra narrow test.

## Todo
- Task 1 complete on base `450ffd5d`; commits `db343382`, `354b4667`. Fix round 1/5 adds minimum-budget coverage; Core/CLI 92/92 and MCP 50/50 green, `git diff --check` clean. Report: `task-1-report.md`; durable evidence: `plans/106-evidence/task-1.md`.
- Task 2 pending.
- Task 3 pending.
- Final formatting, Windows gate, Sol low review pending.

Task 1 started: implementer /root/task1_impl, BASE 450ffd5d; initial plan commit 450ffd5d.
Baseline Core AgentOutputTests: exit 0, 87 passed, 0 skipped; baseline-core.log. Fresh-checkout npm ci/build completed; existing Vite chunk-size warning recorded. Task 1 may proceed with RED.
Task 1 implementation db343382; RED Core 2 failed/89 passed, MCP 2 failed/47 passed; GREEN Core 91/91 MCP 49/49; reviewer /root/task1_review started against 450ffd5d..db343382.
Task 1 review needs fixes: Important CLI minimum budget loses original code/phase; MCP tiny-budget gap confirmed as missing assertion. Fix round 1/5 dispatched to original implementer, FIX_BASE db343382; add minimum-budget coverage and narrow preservation fix before shared extraction.
Task 1 fix round 1 implementation 354b4667; RED reproduced original code loss in both renderers; GREEN Core 92/92 MCP 50/50; scoped re-review dispatched db343382..354b4667.
Task 1: fix round 1/5 (2 addressed, 1 open — new Important distinct truncation undercount; commits db343382..354b4667). Fix round 2 dispatched original implementer, BASE 354b4667.
Task 1 fix round 2: exact union of already-clipped and budget-clipped fields added to CLI/MCP fallbacks, with exact-count regressions (CLI 4, MCP 3). RED Core 1/92 and MCP 1/50. Initial CLI check also found 4097/4096 due CRLF; fit check now reserves writer newline UTF-8 bytes and test asserts newline. GREEN Core 92/92, MCP 50/50; diff check clean. Report/evidence appended. Commit `3b42ebfc` on top of `354b4667`.
Task 1 fix round 2 implementation 3b42ebfc; exact omission RED Core expected4 actual3, MCP expected3 actual2; additionally fixed CRLF 4097/4096-byte reservation. GREEN Core92 MCP50. Scoped re-review started 354b4667..3b42ebfc.
Task 1: fix round 2/5 (1 addressed, 0 open; commits 354b4667..3b42ebfc)
Task 1: complete (commits 450ffd5d..3b42ebfc, review clean)
Task 2 BASE 3b42ebfc; dispatch fresh Luna high implementer.
Task 2 implementer /root/task2_impl started; BASE 3b42ebfc.
Ruling: Task 2 uses baseline GREEN then shared-policy extraction then GREEN when its additional edge tests already pass — Task 1 supplied behavior RED and fixed the Contract; inventing new semantics solely for RED would violate the bounded spec — cost if wrong is weaker incremental regression evidence, mitigated by existing RED and before/after edge runs.
Task 2 implementer DONE commit 2c0d9b36; baseline edge CLI5 MCP1; after extraction Core93 MCP51; diff-check clean. Reviewer /root/task2_review reviewing 3b42ebfc..2c0d9b36.
Task 2: complete (commits 3b42ebfc..2c0d9b36, review clean)
Task 3 BASE 2c0d9b36; pending fresh implementer. Task 3 controller owns direct branch formatting, full gate and final review after task commit/review.
Task 3 /root/task3_impl started BASE2c0d9b36. Task review then controller direct formatting/gate and Sol low whole-branch review.
Task 3: implementation complete in worktree at BASE 2c0d9b36. Contract-description RED observed in MCP and CLI; GREEN MCP contract 1/1 and CLI help 1/1. Focused CLI 93/93, MCP 51/51; MCP schema 64/64; tools/list final fixed/request annotations-on 32,286/32,768 and 32,729/32,768. `git diff --check` exit 0. Full report `.superpowers/sdd/106-bounded-error-diagnostics/task-3-report.md`; evidence `plans/106-evidence/task-3.md`. Task review pending; controller owns direct branch format, Windows gate and final Sol low review.
Task 3 DONE b3a404f9; Core93 MCP51 schema64 help1; catalogs annotations fixed32286 request32729/32768; reviewer /root/task3_review started. Branch formatting started against recorded base6d543e50.
Task 3 review needs fixes: Important capabilities missing tiny-budget code/phase/truncation guarantee. Fix round1 dispatched original implementer BASEb3a404f9, combined with direct branch FINALNEWLINE correction (initial format exit2). No full gate run before correction.
Resumed after worker usage limit; original worker completed fix1 d78eac9d. Schema64 tools-list1 green, just fix0 branch-fix0; controller final branch-check running. Fix re-review pending.
Task 3: fix round 1/5 (1 addressed, 0 open; commits b3a404f9..d78eac9d)
Task 3: minor (deferred): capabilities says timeouts rather than enumerating connectionTimeoutSeconds; less discoverable, final review to triage.
Task 3: complete (commits 2c0d9b36..d78eac9d, review clean; one Minor deferred)
Final branch format d78eac9d: exit0, 13 C# files, direct log branch-format-final.log. Windows just verify started at d78eac9d, logs D:/temp/sqlharness-plan106-final-verify.
Whole Sol low review found Important: long Location.Path can prevent minimal-budget original error identity in both renderers. Initial gate d78eac9d canceled for code fix after ui-install (own verified process tree only); not completion evidence. Final fix wave pending full report.
Ruling: Permit byte-budget fallback to shorten or omit optional Location.Path while keeping its normal caller cell cap — final review demonstrates path alone prevents required error identity under accepted minimum budget, satisfying the Contract exception for a demonstrated defect — cost if wrong is less path detail in tiny envelopes, explicitly reported as truncation.
Single final fix wave complete 43d0a189: optional path fits byte budget, exact truncation regressions, timeout field restored. GREEN Core95 MCP53 schema64, postformat combined117; branch-format exit0 (13files). Scoped Sol review and final gate pending.
Final scoped Sol low review43d0a189: Important path budget and Minor timeout discoverability ADDRESSED, no new breakage. Declined-to-judge items accepted as out-of-scope verification boundaries (Linux/liveDB/loaded binary/deployment/token savings, unchanged artifact continuations); final Windows gate pending. Relative Markdown file links OK9 files, diff-check0.
Gate43d0a189 failed test stage exit1: Core4659pass/2fail/11skip, MCP516pass/4skip, UI372pass build0warnings. Failures new CLI help exact description assertion and existing ProcessRunner pid-publication timeout. Archived logs; gate-debug Luna high investigating root causes before scoped remediation.
Gate remediation a5ec831f: new help test exact source-string assertion failed due rendered whitespace/final punctuation; semantic clauses retained and focused test1pass. Existing ProcessRunner exact focused rerun1pass, no product change, recurrence unproven. Direct branch format0/diffcheck0. Full gate rerun required; product tree remains43d0a189, test-only new head.
FINAL: just verify a5ec831f exited0 five stages11m18s: build0warnings/errors, UI372, Core4661pass11skip, MCP516pass4skip; branch-format0. All reviews approved, all findings closed. Documentation/evidence closeout only after tested revision. No merge/push/install.
