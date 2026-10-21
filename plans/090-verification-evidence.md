# Plan 090 verification evidence

## Current status — feature branch

Plan 090 is implemented on `plan-090`. Product revision `1e6b0eb03330099d69495d4e59d478498ca27097` contains the matcher change and formatting repair; the focused `TranscriptMatcherTests` suite passed 26/26 after the repair. Task 1 review and its scoped re-review are approved. Windows `just verify` passed on that exact product revision. Task 2 documentation review and the Sol low whole-branch review remain pending. This is feature-branch evidence, not an integrated-tree claim.

## Deterministic retention evidence

The internal count seam and focused regressions measure the tracked kind-qualified live identity set directly. An orphan null-title identity changes from 1 to 0 after its operation is removed; it can then be reintroduced and enriched when retained work returns. Two identities sharing a conversation ID across Codex and OpenCode shrink from 2 to 1 when only OpenCode work is removed. When an operation disappears after the current batch captured it, the identity remains at 1 for that in-flight sweep and becomes 0 on the next full sweep. Three repeated cycles each begin with 20 live identities and return to 0 after their operations are removed. These deterministic fixture counts establish the set’s retention behavior; they do not make a process-memory or performance measurement claim.

The interrupted index-scan regression keeps one tracked identity while the scan is incomplete. A targeted lookup preserves that sweep state and queues only its own selected identity (two pending identities at that point); completing the later full sweep resolves the old title and returns the count to zero.

## Staging lifetime ruling

[`JournalSchema.cs`](../src/SqlHarness.Core/Journal/JournalSchema.cs#L282) defines `agent_tool_calls` with `agent_kind`, `call_id`, `conversation`, `model`, `effort`, and `seen_at`; the conversation foreign key cascades on conversation deletion. The production staging writer inserts only the literal `claude` kind ([`TranscriptMatcher.cs`](../src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs#L500)). `Commit` removes staged rows after a retained Claude operation joins the call ID and persisted operation link, or when `seen_at` is older than the seven-day `StagingLifetime` ([lifetime](../src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs#L24), [expiry and cleanup](../src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs#L524)). Journal retention keeps an orphan conversation while either an operation link or a staged tool-call row references it ([`JournalRetention.cs`](../src/SqlHarness.Core/Journal/JournalRetention.cs#L211)). Thus the seven-day persistent staged-call lifetime currently applies to Claude; no production Codex/OpenCode staged-only row exists to preserve.

For Codex/OpenCode backfill state, the real transient lifetime boundary is the current `Batch.Conversations` snapshot: it keeps an identity live if its operation is removed during that in-flight sweep, and the next full sweep prunes it if no retained operation has returned. If a future Codex/OpenCode writer adds persistent staging, its references must be added to the live union and covered by regression tests. No synthetic staging row or dead query was added.

## Verification and review

- The pre-pruning focused suite passed 22/22. The regression-first run produced 5 expected failures among 26 tests: orphan eviction stayed at 1 instead of 0, kind-scoped eviction retained 2 instead of 1, and a 20-identity retention cycle stayed at 20 instead of returning to 0 (plus two over-specific assertions corrected before implementation). After the fix and formatting repair, the focused suite passed 26/26. These before/after outcomes and the meaningful RED failure values are retained here.
- Focused command: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~TranscriptMatcherTests"` — output `Passed! - Failed: 0, Passed: 26, Skipped: 0, Total: 26`; direct exit code 0.
- Branch formatting: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef main` — output `format: 2 changed C# file(s) (branch)` and `format: OK`; direct exit code 0.
- Task 1 review found no Critical or Important behavior issues; its one Important branch-format finding was addressed and approved in scoped re-review. Two Minor documentation findings (incorrect test path and overbroad staged-only wording) are addressed in this plan update.
- Windows `just verify` on product revision `1e6b0eb03330099d69495d4e59d478498ca27097`: direct exit code 0; `verify: OK stages=5 9m04s`. UI: 64 files / 361 tests passed. Core: 4,599 passed / 11 skipped. MCP: 489 passed / 4 skipped. Build: 0 warnings and 0 errors. Gate formatting skipped because no C# files were changed in the task-2 working tree; the separate direct branch formatter had already passed for the two changed C# files. The UI build logged the existing >500 kB chunk-size warning, and UI checks logged Node `DEP0190`; neither failed a stage.
- Task 2 documentation review and final Sol low whole-branch review: pending.
- Linux, live transcript scans, merge, and push were not run or authorized by Plan 090.

The deferred Plan 084 title-backfill memory Minor is covered by the passing eviction/retry regressions above. This file retains the material test and review outcomes so verification does not depend on ignored SDD scratch files.
