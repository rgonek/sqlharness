# Audit recommendations and Plan segments

Branch/worktree: `dashboard-shadcn-fixes`. Base: `2d0e107c`; product candidate: `bafcd2a5`. Latest operator screenshots supersede the earlier visible-radio decision: Plan versions use the same joined SegmentedRadioGroup presentation as Side by side / Unified, with differentiated A/B Badge labels. No generated shadcn primitives or backend/storage changes.

## Implemented recommendations

| Item | Result |
|---|---|
| Blank optimization metadata | Trim/filter each value before inserting separators. No leading, trailing or adjacent dots, no synthetic `(no database)` field. |
| Plan version picker | Shared joined radio segments, A/B labels retained; keyboard/focus behavior remains radio selection. |
| Query Columns / mutation Rows affected and Estimated | Removed unsupported placeholder-only KPIs. Actual duration/output/approval-wait metrics remain. |
| Results retention explanation | Already confined to the empty Results tab; verified rather than duplicating it elsewhere. |
| Missing plan evidence | A visible count of affected fragments replaces repeated warning boxes. A shadcn Collapsible provides full document/statement reasons. Manifest and source-map omissions deduplicate by document/statement without dropping either reason. Actual errors and other evidence limitations remain visible. |
| Settings verification description | Describes deliberate approve/reject clicks; removed implementation-plan/WebAuthn wording. No policy behavior changes. |
| Profile dimensions | Numeric coverage/totals remain visible. Explanations of visible/full totals and target identity are available through focusable shadcn tooltips. Empty states and unavailable-duration warning remain. |
| Full identifiers/hashes | Session/conversation IDs and operation SQL hash are complete in Details. Usage SQL hash previews, selected plan document and exact occurrence selectors expose full hashes in shadcn tooltips, with stable triggers and explicit accessible descriptions. Short labels include document/statement identity. |

## Verification status

TypeScript/lint/build passed. [Focused checks](focused-tests.log): 7 files / 92 tests passed. [Browser QA](results.json): ten screenshot states passed, including blank metadata, joined radios with arrow-key selection/focus retained, deduplicated count (four source notices → two affected fragments), initially collapsed details with both reasons retained, full hash tooltip and stable Select focus, 390px Plan and Usage document width, query/mutation placeholders, Results-only retention note, Settings wording and focusable Usage/target/totals explanations. Screenshots inspected visually; no page errors. [Gallery](index.html).

Read-only review identified missing explicit tooltip accessibility relationships; fixed at composition sites with useId, aria-describedby and role=tooltip. Final scoped review, including additional Usage/Operation hashes, has no findings. Original component sources remain unchanged. Browser fixtures replace API responses only in the browser; no journals or databases are written.

Final `just verify -LogDirectory D:/temp/sqlharness-audit-recommendations-final-gate` passed on bafcd2a5: 5 stages / 17m19s. UI 388 (68 files), Core 4722 (11 skipped), MCP 535 (4 skipped), TypeScript/lint passed; build zero warnings/errors. Format skipped correctly for zero changed C# files. [Gate logs](gate/ui-check.log). Published locally from bafcd2a5 and dashboard restarted at port 47800 / PID 40684. Installed EXE and 20 assets match; ten installed browser states passed. [Receipt](delivery.json), [asset hashes](installed-assets.json), [installed QA](installed/results.json). The operator rejected the visual quality of the cramped Plan A/B control despite these functional checks; [close-up follow-up](plan-control/verification.md) records the correction. No merge/push.

The first focused run overlapped edits to two tests and a conditional trigger wrapper; the final rerun of those two files passed 13 tests after stabilizing the trigger and correcting the blank-metadata assertion. This intermediate run is not final gate evidence.
