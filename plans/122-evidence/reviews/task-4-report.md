# Task 4 report

Status: implemented backend only; no UI, live DB, approval, push or full verification gate. The controller owns the combined branch review and Windows gate.

## Design and provenance

- Schema 23 adds nullable `operations.records_affected` and `mutation_requests.operation_id`, plus a partial unique index. No historical data is rewritten. Exact `sessions.dashboard_evidence_version = 1` gates linking, result writes and both reader projections.
- The preparing CLI/MCP journals an operation before broker submission; only a successfully journaled operation supplies its generated operation reference as optional correlation metadata. The endpoint validates/re-prepares the request, retains this metadata separately, and links the request through that exact unique reference. It does not match SQL, timestamps or mutation session key. Old clients send no correlation and get null evidence. This metadata is excluded from the digest and policy.
- Broker acceptance callback links before publishing the pending request or starting automatic execution. Identical joined submissions retain broker behavior but do not acquire their own link, recorded approval or provider result. Callback exceptions are swallowed so journaling cannot alter decisions.
- The canonical executor writes the confirmed provider result for its linked request; nonnegative values including zero are retained, negative provider values are null. This happens independently of the HTTP long poll surviving execution. Timeout, denial and unknown outcome do not generate affected-row evidence. The provider scalar describes the command/batch, not individual statements or unique rows.
- Reader returns `operation.recordsAffected`, nullable `mutationRequestId` and `mutationRequest` containing only existing lifecycle state, status, verification and timestamps. No current setting is represented as historical policy or execution origin. No SQL or parameter material is newly retained. Detail lookup uses the new operation index. Existing `vars_json` filtering uses `GetOrdinal` unchanged.

## TDD and checks

- Initial RED: `MutationEvidenceTests` failed to compile because relation, result API and projections were missing. GREEN introduced those seams.
- RED: throwing acceptance callback changed broker behavior; fixed with best-effort callback guard. RED: executor did not record provider result; fixed by recording in the canonical executor (also handles disconnected waiter).
- Focused synthetic coverage includes 0, positive, -1, multi-statement batch scalar, denied, approval timeout and unknown outcome, operator and automatic, no link, duplicate joined callers, old and future session versions, migration from v22 without rewriting rows, and existing journal migration/reader/filter coverage. Unknown/timeout lifecycle checks are journal-level; separate existing executor tests cover connection loss. The multi-statement HTTP test supplies a fake provider batch total; it is not a live-provider acceptance test.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter ... --no-restore`: 232 passed, 1 Unix-only skipped after indexed schema adjustment.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~McpMutationToolTests --no-restore`: 14 passed before indexed schema adjustment.
- `git diff --check`: clean on final diff before commit.

## Scope / remaining integration concerns

No `ui/` edits, no change to policy, SQL classification or operator approval. Endpoint, broker, CLI, MCP, dashboard model and journal interface changes exceed original brief file list because mutation execution and journaling are separate and the exact relation must cross the broker. API UI integration is deliberately deferred to the parent scope. Best-effort journaling can leave evidence null when its writes fail. Full Windows gate, independent review and live acceptance are not claimed.


## Exact command evidence

The focused Core/dashboard/CLI command on final product tree before commit 604b1705 was:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter 'FullyQualifiedName~MutationEvidenceTests|FullyQualifiedName~MutationBrokerTests|FullyQualifiedName~MutationEndpointsTests|FullyQualifiedName~MutationExecutorTests|FullyQualifiedName~JournalContractTests|FullyQualifiedName~MutationJournalTests|FullyQualifiedName~ActivityJournalTests|FullyQualifiedName~MutationCommandTests|FullyQualifiedName~MutationRequestTests|FullyQualifiedName~JournalReaderTests' --no-restore
```

Output: `Passed! - Failed: 0, Passed: 232, Skipped: 1, Total: 233, Duration: 30 s`. The skipped test is `ActivityJournalTests.Database_and_directory_are_owner_only_on_unix`, which is Unix-only. Output was captured in the terminal session, not persisted as a standalone log.

The MCP-focused command, after the result was moved to the executor and before the final index-only schema adjustment, was:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~McpMutationToolTests --no-restore
```

Output: `Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14, Duration: 6 s`. This is a focused MCP check, not the full MCP suite; output was captured in the terminal session, not a standalone log.

RED command for initial contract:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~MutationEvidenceTests --no-restore
```

Compiler diagnostics included `CS1061: 'IActivityJournal' does not contain a definition for 'RecordMutationResult'`, `CS1061: 'OperationSummary' does not contain a definition for 'RecordsAffected'` and `CS1061: 'OperationDetail' does not contain a definition for 'MutationRequest'`. The test subsequently passed with the new API.

RED command for best-effort behavior:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~Journal_link_failure_does_not_change_approval_or_execution --no-restore
```

Output: `Failed: 1`, with `System.InvalidOperationException: journal unavailable` escaping `MutationBroker.SubmitAsync`. The final focused Core command above includes this test and passed after the callback guard.

RED command for canonical executor result:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~Successful_execution_updates_the_exact_journal_row_to_succeeded --no-restore
```

Output: `Failed: 1`, `Assert.Single() Failure: The collection was empty` for the captured provider result. The final focused Core command includes this test and passed after moving the write into the executor; the fake report returns 0.

`git diff --cached --check` returned exit 0 and no output before the commit. No standalone log files were created.

## Task 4 review fix round 1

Finding: an operation reference alone did not prove it prepared the submitted mutation. Fix: during successful journaled non-dry-run preparation, generate a one-use random token. Only version-1 sessions persist its SHA-256 and the prepared request digest, as nullable columns in additive schema 24. The raw token is internal, excluded from normal JSON and record `ToString`, and serialized only into the local broker HTTP transport. The endpoint recomputes the prepared request and supplies its digest and the transport token to the journal. The transaction verifies exact reference, non-dry-run state, version 1, digest and token hash before linking; the same transaction consumes the token hash. Failed linking leaves it usable. After success, deleting/pruning the mutation request cannot make the token reusable. Journaling and random-token failures remain best effort and do not approve or execute anything.

RED:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~Link_requires_matching_preparation_and_consumes_token --no-restore
```

Compiler diagnostics included `CS1739` for missing `OperationEnd.MutationPreparationDigest` and `CS1501` for the four-argument `LinkMutation` seam.

Focused GREEN before final token visibility-only adjustment:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter 'FullyQualifiedName~MutationEvidenceTests|FullyQualifiedName~MutationEndpointsTests|FullyQualifiedName~JournalContractTests|FullyQualifiedName~ActivityJournalTests|FullyQualifiedName~MutationJournalTests|FullyQualifiedName~JournalingModuleTests|FullyQualifiedName~MutationCommandTests|FullyQualifiedName~MutationRequestTests|FullyQualifiedName~MutationBrokerTests|FullyQualifiedName~MutationExecutorTests|FullyQualifiedName~JournalReaderTests' --no-restore
```

Output `Passed: 279, Failed: 0, Skipped: 1, Total: 280`; skipped test is Unix-only. Final tree after restricting token to internal and adding ToString assertion:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter 'FullyQualifiedName~MutationEvidenceTests|FullyQualifiedName~MutationEndpointsTests' --no-restore
```

Output `Passed: 34, Failed: 0, Skipped: 0`. These tests cover real journaled preparation through endpoint, mismatched digest, dry-run reference injection, replay after mutation history pruning, failed link before successful link, old/future version gates, v23 migration, omitted token in normal serialization/ToString, and token only in explicit internal HTTP transport. No live database was used.

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:SkipDashboardUi=true --filter FullyQualifiedName~McpMutationToolTests --no-restore
```

Output `Passed: 14, Failed: 0, Skipped: 0` before the final visibility-only adjustment. The final Core focused build rebuilt MCP and CLI as dependencies. Outputs were captured in terminal sessions, not standalone logs. No full gate, UI or live acceptance was run. Cost: two small nullable scalars per eligible prepared operation; journal failures leave correlation unavailable. There is no policy or approval behavior change.
