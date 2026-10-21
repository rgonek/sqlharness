# SDD ledger — plan: plans/090-matcher-title-backfill-retention.md
Base: e6e74e8217fccf4e8d814a05ffa3d34dc06013e2
Spec: plan Fix contract plus docs/superpowers/specs/2026-10-17-agent-sessions-design.md read.
Preflight:
| Tasks | Produces / consumes | Finding |
| 1 / 2 | Matcher behavior and regression counts / verification evidence | Compatible; task 2 whole-branch review scheduled after its documentation task review. |
| 1 | lifetime inspection, RED regressions, private/internal seam, pruning / GREEN | Internally consistent; no schema expansion assumed. |
| 2 | task review, branch review, gate / closure docs | Consistent; evidence updated after final review as documentation-only closure. |
Task 1: pending
Task 2: pending
Ruling: Use actual Codex/OpenCode staging lifetime (in-flight Batch.Conversations plus retained operations); agent_tool_calls has a Claude-only writer, so no synthetic Codex staging query — source lifetime evidence governs contract — cost if wrong: a future Codex/OpenCode staging writer requires extending the live union.
Task 1: initial review spec compliant; formatting Important open; final Windows gate deferred to Task 2.
Task 1: minor (deferred): correct test file path in plan during Task 2.
Task 1: minor (deferred): align staged-only checklist with source-backed in-flight batch ruling during Task 2.
Task 1: fix round 1/5 dispatched; base 30fc671; EOF formatting only.
Task 1: fix round 1/5 (1 addressed, 0 open; commits 30fc671..1e6b0eb)
Task 1: complete (commits e6e74e8..1e6b0eb, review clean)
Task 2: started; base 1e6b0eb; Windows gate running; explicit branch format direct exit 0.

Task 2: docs committed as 902f125; full Windows gate passed on product 1e6b0eb (UI 361, Core 4599/11 skipped, MCP 489/4 skipped); Task 2 documentation review and Sol low whole-branch review pending.
Task 1: deferred documentation minors addressed by 902f125; final gate exit 0 on 1e6b0eb.
Task 2: minor (deferred): RED wording says five expected failures; clarify three defect failures plus two corrected assertions.
Task 2: complete (commits 1e6b0eb..902f125, review clean apart from deferred minor; final whole-branch review next)
Task 2: cannot-verify SHA caveat resolved: controller captured HEAD 1e6b0eb immediately after gate (same product head before invocation); only docs changed during run, task2 commit 902f125 followed completion. No rerun warranted.
Task 2: Sol whole-branch verdict approved; zero Critical/Important, two Minor documentation findings.
Task 2: fixed exact RED-count wording and changed staged-survivor checklist to in-flight survivors plus conditional persistent staging. Archived raw logs, all task/branch review reports, task reports and this ruling ledger under plans/evidence/090.
Task 2 final docs-only checks: git diff --check exit0; relative markdown links LOCAL_MARKDOWN_LINKS=OK; no tests/gate rerun. Scoped re-review of this docs/archive wave pending.
Task 2: final scoped re-review marked both prior wording findings ADDRESSED and found archive packaging Important because the ten raw .log files were ignored by Git.
Task 2: resolution is archival only: force-track the exact ten compact .log files and include final-rereview.md; no new ruling, behavior change, test, or gate rerun.
Task 2: final archive completion verified `git ls-files 'plans/evidence/090/*.log'` count 10; raw hash and indexed blob matched 10/10; no line-ending conversion. Staged diff check passed. Final scoped re-review approved both wording fixes and packaging fix. Feature branch done; no merge/push.
