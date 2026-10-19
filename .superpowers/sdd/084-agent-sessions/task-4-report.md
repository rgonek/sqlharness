# Task 4 report: Optimization per agent session with frozen labels

Status: DONE

## Implemented

- Added `ContextKey` and context-aware compare-history and optimization-summary builders. Agent context identity keeps `agent:{kind}` and the conversation id in separate fields; MCP session IDs use invariant decimal encoding. Frozen labels take precedence, and a provisional suffix starts after the highest frozen version.
- Added exact-MCP-only agent Optimization evidence. The aggregate joins exact `operation_agent_links` (and Codex/OpenCode direct session IDs) across child MCP sessions. It excludes inferred operations and CLI rows. Existing child-session Optimization endpoints continue to show their own histories, including CLI and inferred compares/measurements.
- Added `GET /api/agent-sessions/{kind}/{id}/optimization` and `/optimization/compare`, using the existing matching-off 404 behavior and compare validation. Both return `versionLabelsProvisional`; while labels are provisional, `versionLabelNote` is exactly `versions may renumber while matching catches up`.
- Added matcher-cycle label freezing. Labels use the existing `compare_version_labels` table, persisted SQL hashes, chronological baseline-before-candidate ordering, and the `(agent kind, conversation id, subject, scope)` context. GET endpoints do not write labels; `Clear matched data` already deletes them.
- The controller ruled that any earlier unmatched MCP operation with a plausible exact identifier in the same agent kind anywhere in retained journal history remains unresolved until exact-linked or retention removes it. No timeout, completed transcript scan, or absent link proves finality. The barrier is not limited to already-known conversations or child sessions. No-ID operations and CLI rows are final-unassigned for the exact-only aggregate. This allows unrelated old pending calls to keep labels provisional indefinitely.

## TDD evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Version_labels_are_frozen_before_a_later_link_can_change_chronology" -p:IsTestProject=true` — failed as expected: 0 persisted labels where the regression expected 3.
- GREEN after implementation and formatting: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~CompareHistoryBuilderTests|FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~AgentSessionsTests|FullyQualifiedName~TranscriptMatcherTests" -p:IsTestProject=true` — 89 passed, 0 failed, 0 skipped.
- `just fix` — `format: 11 changed C# file(s) (working-tree)`.

New regression coverage includes chronological history across two MCP child sessions; frozen labels surviving a later exact link; a pending earlier operation blocking only the unfrozen suffix until it is linked; CLI and inferred compares staying in child history but out of aggregate history; provisional summary and compare response note fields; GET no-write behavior; context-key component separation; and matching-off 404 responses.

## UI contract

Both optimization summary and compare response JSON expose:

- `versionLabelsProvisional: boolean`
- `versionLabelNote: string | null`

The UI should show `versionLabelNote` when `versionLabelsProvisional` is true. The exact note is `versions may renumber while matching catches up`.

## Files changed

- `src/SqlHarness.Dashboard/CompareHistoryBuilder.cs`
- `src/SqlHarness.Dashboard/DashboardServer.cs`
- `src/SqlHarness.Dashboard/JournalReader.cs`
- `src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs`
- `src/SqlHarness.Dashboard/Optimization/OptimizationSummaryBuilder.cs`
- `src/SqlHarness.Dashboard/Transcripts/TranscriptMatcher.cs`
- `tests/SqlHarness.Tests/Dashboard/AgentSessionsTests.cs`
- `tests/SqlHarness.Tests/Dashboard/CompareHistoryBuilderTests.cs`
- `tests/SqlHarness.Tests/Dashboard/JournalSeed.cs`
- `tests/SqlHarness.Tests/Dashboard/OptimizationSummaryBuilderTests.cs`
- `tests/SqlHarness.Tests/Dashboard/Transcripts/TranscriptMatcherTests.cs`
- `plans/084-agent-sessions.md`
- `plans/README.md`

## Self-review and limits

- Frozen labels are written only within the transcript matcher write transaction; read paths use a read-only journal snapshot.
- The whole retained-journal barrier is intentionally conservative. If an earlier unmatched MCP call never gains an exact link and remains retained, later versions can remain provisional. The UI note makes this state explicit.
- The integrated `just verify` gate remains Task 6's responsibility.

## Review fix 1: preserve ordinals across retention gaps

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Context_scoped_build_reserves_ordinals|FullyQualifiedName~Pending_call_in_another_mcp_child" -p:IsTestProject=true` — 1 passed and 1 failed as expected; the context-scoped builder returned `v0` instead of `v6` when only persisted `v4`/`v5` labels remained.
- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Provisional_labels_reserve_ordinals_for_frozen_hashes_absent" -p:IsTestProject=true` — failed as expected; OptimizationSummaryBuilder returned `v0` instead of `v6`.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Context_scoped_build_reserves_ordinals|FullyQualifiedName~Provisional_labels_reserve_ordinals|FullyQualifiedName~Matcher_assigns_after_highest_persisted_label|FullyQualifiedName~Pending_call_in_another_mcp_child" -p:IsTestProject=true` — 4 passed, 0 failed, 0 skipped.
- Focused classes: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~CompareHistoryBuilderTests|FullyQualifiedName~OptimizationSummaryBuilderTests|FullyQualifiedName~TranscriptMatcherTests" -p:IsTestProject=true` — 48 passed, 0 failed, 0 skipped.
- The builders now seed each subject/scope counter from every frozen label, even when its hash no longer appears in retained evidence. Added matcher coverage confirms persisted `v4`/`v5` labels reserve those ordinals when old operations are absent. Added cross-child coverage confirms an earlier pending same-kind MCP call blocks freeze, while no-ID and CLI rows do not.
- `just fix` — `format: 5 changed C# file(s) (working-tree)`; `git diff --check` — no whitespace errors.
