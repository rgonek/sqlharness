# Plan 085 implementation evidence

**Status:** All task reviews, whole-branch Sol review, and final scoped Sol re-review are approved on branch `plan-085`. Verified product tree `9adc27d` passed the final Windows gate. Local integration into `main` is authorized and pending; no push is authorized.

## MCP byte measurements

Measurements use the UTF-8 byte count of the serialized MCP tools or the single result text block. They are deterministic synthetic checks; no database connection or agent trial was used.

| Surface | Annotations off | Annotations on | Context |
|---|---:|---:|---|
| `tools/list` | 28,248 B | 31,109 B | Same fixed SQL Server scoped test profile; 9 versus 10 tools; on remains below 32,768 B |
| `sqlharness_capabilities` result | 969 B | 997 B | Same fixed SQL Server scope; the on response adds the true `operationAnnotations` member |
| Representative query result, 4,096 B budget | 285 B | 335 B | Synthetic result; on includes a 32-character `operationRef` |
| Representative query result, 65,536 B budget | 285 B | 335 B | Same synthetic result and reference |

The existing `unavailable-metrics` PostgreSQL workflow fixture measures `tools/list` at 23,462 B. The SQL Server synthetic workflow fixture measures 28,248 B. The difference comes from engine-scoped tool descriptions; the on/off comparison above uses the same SQL Server scope. Both fit the catalog limit.

Task 0's `modelVisibleUtf8Bytes` field was computed before trimming the newline absent from the stored `text`, causing a one-byte overcount. It now counts the exact normalized, trimmed string stored in each fixture record. Affected `OperationAnnotations/off/results` values and manifest hashes were updated. The workflow fixture check passes.

## Focused checks

- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config|FullyQualifiedName~Query_result_byte_measurements_are_recorded_with_annotations_off_and_on|FullyQualifiedName~Scripted_workflows_fit_4096_and_65536_and_freeze_portable_records" --logger "console;verbosity=detailed"` — 3 passed. The on-mode catalog and result sizes above were reported by the tests.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Post_hoc_annotation_call_is_journaled_without_target_or_reference" --logger "console;verbosity=minimal"` — 1 passed. It verifies the wrapper journals a target-free `annotation` row with a null operation reference.

## Remaining evidence limits

A focused capabilities serialization test was attempted from detached commit `3702ce1` in a temporary worktree. The test build stalled after Core, MCP, and WorkflowHost output; it was interrupted, and the temporary worktree was removed. Source review confirms the current nullable `operationAnnotations` capability is omitted when false, but the exact historical capabilities byte golden was not captured. This remains for whole-branch review to triage.

Optional paired agent and second-session reuse trials were not run. They were optional, and no authorization for those trials was provided. Linux verification and live-database activity are outside this task.

## Final gate

`just verify -LogDirectory D:/temp/sqlharness-plan085-formatted` passed all five stages on the product tree committed as `7c5686c`; the gate ran immediately before the formatting-only commit, with the same C# content. Build succeeded with 0 warnings and 0 errors. UI check passed 62 files / 329 tests in 3m44s. Core passed 4,561 tests with 11 skipped; MCP passed 460 with 4 skipped. The gate's working-tree formatting check passed 19 C# files. The UI log includes non-failing localhost:3000 connection refusals and Node `DEP0190`; these were diagnostics, and the stage passed.

Explicit branch formatting: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 3702ce1` passed for 39 branch-changed C# files after normalization. Direct output is retained at `D:/temp/sqlharness-plan085-format-3702ce1-postcommit.log`; the initial diagnostics and branch fix output are retained at `D:/temp/sqlharness-plan085-format-3702ce1.log` and `D:/temp/sqlharness-plan085-format-3702ce1-fix.log`.

The gate's UI check, Windows test suites and branch-format command passed. Linux, live database checks and actual-agent trials were not run.

## Final review fix wave

The whole-branch review rulings keep `keep` and owner star as markers subject to normal retention. Tool wording and MCP documentation now say those markers can expire; a focused journal-retention test proves both kept and ordinary operation annotations expire with their rows. Annotation batches now preflight the complete success envelope through the same JSON serializer and byte-budget path used by the MCP adapter while the SQLite transaction is open; oversized results throw a clear budget/split error and roll back. The complete envelope includes omitted-field echoes and JSON escaping.

The operation page now shows owner annotation request failures and clipboard copy failures, with focused UI coverage. `docs/mcp/tools.md` spells out matching caller/target provider tuples, resolved and unresolved caller rules, and that inferred membership never authorizes. The historical capabilities byte oracle remains unproven as recorded above; no historical build was repeated.

Focused validation before the final gate: `AnnotateToolTests` 10/10; `Normal_retention_expires_kept_and_unkept_operation_annotations` 1/1; `OperationPage.test.tsx` 28/28. Final direct branch formatting passed on committed code tree `9adc27d` with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 3702ce1`; it checked 40 branch C# files.

The first final gate attempt (`D:/temp/sqlharness-plan085-finalwave2`) passed build and UI but hit the existing timing-sensitive `DashboardHousekeepingTests.Matcher_commits_do_not_keep_an_idle_dashboard_up` assertion (null title). Its focused rerun passed 1/1. No code changed; the full gate was rerun with fresh logs. `just verify -LogDirectory D:/temp/sqlharness-plan085-finalwave3` passed all five stages on tree `9adc27d8cc1c935968ece5a4023acff8313f50a1`: build 0 warnings / 0 errors; UI 62 files / 331 tests passed; Core 4,562 passed / 11 skipped; MCP 462 passed / 4 skipped. Working-tree formatting was skipped because there were zero changed C# files; direct branch formatting above passed. The UI log has non-failing localhost connection refusals. Linux and live database checks were not run. The whole-branch and final scoped reviews are approved; local integration is authorized and pending.

## Final SDD rulings and review disposition

The following seven rulings are copied in order from the Plan 085 SDD ledger. Each retains its stated cost if the ruling is wrong.

1. Ruling: Off-mode golden uses current merged baseline 3702ce1, not a historical 077 checkout — later merged contracts must remain intact — if wrong historical byte comparisons need revision.
2. Ruling: Task 1 validates five gated tool schemas; the tenth tool and complete on-mode list budget land in Task 3 — avoids placeholder handlers — if wrong task gate timing needs revision.
3. Ruling: Proceed after baseline gate failed two unchanged dashboard lifecycle tests and focused rerun passed 2/2 — failures predate feature and local contention was observed; final full gate remains mandatory — if wrong final verification may require baseline defect diagnosis.
4. Ruling: keep/star are useful-operation markers subject to normal retention, not retention pins — spec requires annotations deleted with operation and no retention exemption — if wrong useful operations may expire instead of staying pinned.
5. Ruling: If complete annotation per-item response cannot fit requested byte budget, reject and roll back batch before commit with clear budget error — 20 maximum Unicode labels cannot fit4096 while echoing full values; preserve bounded output and atomic visibility — if wrong clients must increase budget or split batches that previously fit input validation.
6. Ruling: Historical capabilities exact-byte oracle remains a nonblocking evidence limitation — current off-member omission, unchanged MCP mapping and other goldens/measurements pass, final Sol agrees — if wrong a capabilities-only byte regression may remain undetected. The controller and final Sol accept this as a nonblocking evidence Minor.
7. Ruling: Accept shared-handler maximum-batch rollback regression without an outer MCP response-path test — exact adapter serializer helper and both tool paths share budget wiring, full gate passes, final Sol marks test gap Minor — if wrong a transport-wrapper budget regression could escape this focused test. The controller accepts this as a Minor coverage gap.

Final scoped Sol review: all Important findings, owner feedback, and scope documentation are addressed; no Critical or Important findings remain. The two documented evidence/test Minors are parked with the accepted rulings above. Local integration into `main` is authorized and pending; no push is authorized.
