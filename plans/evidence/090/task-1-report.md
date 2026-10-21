# Plan 090 Task 1 implementation report

## Lifetime and ownership evidence (recorded before source edits)

- `JournalSchema.cs` defines `agent_tool_calls(agent_kind, call_id, conversation, model, effort, seen_at)`. `conversation` references `agent_conversations(id)` with `ON DELETE CASCADE` (`src/SqlHarness.Core/Journal/JournalSchema.cs:282-289`). The schema permits kinds generally, but the matcher writer is Claude-only: `Collect` emits staged tool calls from Claude lines, and `Commit` inserts the literal `'claude'` at `TranscriptMatcher.cs:473-505`.
- `TranscriptMatcher.Commit` removes a staged `agent_tool_calls` row once its call ID joins a retained Claude operation and a persisted operation link, or when `seen_at < now - StagingLifetime` (`TranscriptMatcher.cs:524-530`). `StagingLifetime` is seven days (`TranscriptMatcher.cs:24`). Journal retention separately removes orphan `agent_conversations` only when neither an operation link nor an `agent_tool_calls` row references it (`JournalRetention.cs:211-218`). Thus a live staged Claude row can keep its conversation row alive through retention, but no production path stages Codex/OpenCode calls.
- Codex/OpenCode title bookkeeping is process-local and kind-qualified: `_codexTitleBackfillPending` and `_codexTitleBackfillAttempted` are `HashSet<(string Kind, string Id)>`; the resumable cursor is `_codexTitleBackfillCursor`, with `_restartCodexTitleBackfill` controlling a restart (`TranscriptMatcher.cs:20-29, 329-330, 347-381`). `Clear` resets both sets and cursor (`TranscriptMatcher.cs:166-169`). Full sweeps prune against live operation and current-batch identities; targeted cycles retain their selected-only lookup behavior (`TranscriptMatcher.cs:306-345`).
- Controller ruling on the staged-only case: do not synthesize Codex/OpenCode `agent_tool_calls` rows or add a dead query for a production-impossible path. Keep identities in the current `Batch.Conversations` snapshot live through that sweep, together with retained unresolved operations. The regression deletes a Codex operation after `AddCodexIdentities` captured it and verifies the in-flight batch survives until the next full sweep. This covers the real synchronous in-flight boundary; the persistent staged-reference table remains Claude-only. A future Codex/OpenCode staging writer must extend the live-union query.

## Implementation

- Full sweeps now derive the unresolved live identity set from retained Codex/OpenCode operations and the current batch snapshot. Attempted and pending sets are pruned against that kind-qualified set; orphan null-title rows alone no longer keep historical state alive.
- Targeted cycles still use selected operation identities and never prune or consume unrelated pending sweep work. Existing resumable cursor behavior is unchanged.
- Added an internal identity-count seam for deterministic assertions, plus regressions for orphan eviction/reintroduction, kind separation, in-flight batch retention, interrupted scan/cursor behavior, and repeated retention cycles.

## Verification and TDD evidence

- Baseline from the controller: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcherTests"` — 22/22 passed (see `baseline.log`).
- RED: the same command after adding the regression cases and before the pruning change — 26 tests, 21 passed and 5 failed. The meaningful failures showed the defect: an orphan identity expected to drop from 1 to 0 remained at 1; the kind-scoped case expected 1 and retained 2; and the first of three 20-identity retention cycles expected 0 after deletion but retained 20. Two additional first-run assertions were over-specific and were corrected before the implementation: targeted processing also leaves its newly selected unresolved identity pending (so the state count is 2), and a deleted operation correctly has no persisted operation link even though its captured batch conversation is committed.

  ```text
  Full_sweep_evicts_orphan_null_title_state_and_allows_reintroduction: Expected 0, Actual 1
  Full_sweep_prunes_kind_qualified_identity_independently: Expected 1, Actual 2
  Repeated_retention_cycles_bound_backfill_state_to_current_operations: Expected 0, Actual 20
  Failed: 5, Passed: 21, Skipped: 0
  ```
- GREEN: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcherTests"` — final rerun 26/26 passed, exit 0, output pristine. One intermediate rerun exposed a compile error because the new helper accessed instance state while declared static; making it an instance helper fixed it, and the final focused run passed.
- Deterministic counts: orphan null-title state 1 → 0 after its operation is removed; the same identity is reintroduced and gets its title on a later retained operation. Same conversation ID across kinds starts at 2 and drops to 1 when only OpenCode work is removed. An operation deleted after the batch snapshot leaves count 1 for that sweep and drops to 0 on the next full sweep. Three repeated cycles each start with 20 live identities and return to 0 after their operations are deleted.
- The existing interrupted Codex index scan test now asserts one tracked identity across the initial budget-limited scan; targeted lookup preserves sweep state (two pending identities after the target itself is queued); completing the subsequent full sweep resolves the old title and returns the count to zero.

## Files changed

- `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs`
- `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs`
- `plans/090-matcher-title-backfill-retention.md` (Task 1 checklist)
- `.superpowers/sdd/090-matcher-title-backfill-retention/task-1-report.md`

## Self-review and concerns

- The live union is kind-qualified and bounded by candidate identities already in the tracked sets/current batch. SQL matching uses chunks of 400 identities (800 bind parameters per statement) rather than enumerating every conversation row. Pruning is full-sweep-only. The helper avoids treating a null-title conversation row without live operation/batch evidence as pending work. No schema or public matcher interface changed.
- Existing pending-set cap remains 500; the work adds no arbitrary cap or unbounded transcript output. The added count seam is internal and only reports the distinct tracked identity count.
- Staged-only persistent references for Codex/OpenCode do not exist in the current writer. The in-flight batch regression covers the real current lifetime; a future writer must be added to the live-union query and tests.

## Review fix round 1

- Review finding: the branch formatter reported `FINALNEWLINE` for `TranscriptMatcher.cs` and `TranscriptMatcherTests.cs`. Ran `just fix`; it exited 0 and reported `format: skipped (0 changed C# files)` because the implementation was already committed and the task's branch files are not working-tree changes.
- Ran `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef main -Fix`. It reported `format: 2 changed C# file(s) (branch)` and `format: OK`. Inspection confirmed the only changes are the two final newline markers (the formatter removes the terminal CRLF from each file).
- Reran `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef main`: `format: 2 changed C# file(s) (branch)`, `format: OK`, direct exit code 0. Raw output: `.superpowers/sdd/090-matcher-title-backfill-retention/task-1-fix-format.log`.
- Reran `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcherTests"`: 26/26 passed, direct exit code 0. Raw output: `.superpowers/sdd/090-matcher-title-backfill-retention/task-1-fix-tests.log`.
- `git diff --check` passed. The fix changes only EOF formatting in the two C# files; no product or test logic changed.
