# Plan 109 Task 1 — equivalent-evidence baseline

Status: **DONE**. This is a deterministic offline study using the repository's real MCP SDK client, server tools, artifact writer/reader, and output projectors. The workflow host supplies canned SQL results and plans and records zero database connection attempts. No production behavior changed, no database was contacted, and no actual tokenizer was run.

## Frozen inputs and evidence

The exact SQL and parameter-set inputs are in [`task-1-sdk-baseline.json`](../../../plans/109-evidence/task-1-sdk-baseline.json). The test fixture uses a four-statement measured batch, one separate `SET NOCOUNT ON;` setup statement, four named sets (`small`, `medium`, `large`, `edge`) with three measured repetitions each, two candidate revisions that differ in statement 3, and a query returning a 96-row first set followed by two assertion sets.

The capture contains all 16 SDK tool responses, exact selectors and artifact IDs, request and response wire byte counts, model-visible UTF-8 text byte counts, output budgets, truncation/omission metadata, and continuation values. `actualTokenizerTokenCount` is explicitly null. `estimatedTokensByteDiv4` is the model-visible UTF-8 byte count divided by four; it is a rough estimate, not a tokenizer measurement.

| Baseline path | Calls | Request wire bytes | Response wire bytes | Model-visible UTF-8 bytes | Bytes / 4 estimate | Continuations / omitted |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Candidate revision 1: compare, statements, four candidate plan selections | 6 | 1,953 | 12,633 | 7,603 | 1,900.75 | 0 / 0 |
| Candidate revision 2: compare, statements, four candidate plan selections | 6 | 1,953 | 12,633 | 7,603 | 1,900.75 | 0 / 0 |
| Four named sets: measure, metrics, statements | 3 | 1,169 | 17,824 | 10,974 | 2,743.50 | 0 / 0 |
| Large first result plus later assertions: query | 1 | 270 | 13,069 | 11,469 | 2,867.25 | 0 / 0 |
| **Total** | **16** | **5,345** | **56,159** | **37,649** | **9,412.25** | **0 / 0** |

The current baseline paths rank by emitted model-visible bytes as: late-result query (11,469), named-set attribution (10,974), then each candidate revision review (7,603). This ranks the captured paths only; it does not claim savings or compare a proposed implementation.

Candidate revisions produced distinct saved-artifact identities: `19700101T000000000Z-workflow-fixture-87f2fc03a9a64196b975bc8e07d9df06` and `19700101T000000000Z-workflow-fixture-eb8577116a1f482784e498faf7d2437d`. Each revision returned four distinct statement hashes, and the changed fourth statement had a different hash. Each plan request selected `variant=candidate`, one exact statement ordinal (0–3), and `repetition=5`; each response identified repetition 5 and available repetitions 1–5. The selector loop was prepared to follow continuation cursors; none were returned.

The named-set artifact returned four ordered variants with per-table logical-read attribution. Its statement section returned 16 records: all four statement ordinals for each of the four named sets. The query returned all three result sets, including the two assertions after the 96-row first set. The setup text was supplied through the separate `setup` field, and the fixture report counts four setup executions, one per named set/session.

## Source assumptions assessed

The central source assumptions were confirmed on baseline `677b2f93b66c06d3dc0bda8475c7570ef29738d9`:

- `AgentMeasureSetItem` has no per-table logical-read map, while `ArtifactNamedMetrics` does. The real SDK artifact metrics path returned each named set's table attribution.
- `ArtifactStatementMetric` always contains `TopOperators`. The four-statement pages included operator summaries alongside CPU/elapsed fields. This baseline has no compact CPU/elapsed-only selector.
- Saved-plan reads support exact variant, statement, repetition, page size, and byte budget selectors. Separate candidate artifacts retained separate statement hashes; no cross-revision reuse was inferred.
- Query projection returned later result sets after the large first set in this fixture. This baseline request has no result-set selector, so the complete first set and both later sets were emitted.
- Setup was provided separately from the measured four-statement query. The fixture did not remove a setup statement from inside SQL or subtract overlapping counters.

These results establish only the checked source and synthetic fixture behavior. They do not establish frequency across installations, runtime binary identity, live database behavior, model-agent behavior, or actual token savings. No uncertain cause is asserted.

## Commands and results

From `D:\Dev\sqlharness\.worktrees\plan-109`:

1. `dotnet build tests/SqlHarness.Mcp.WorkflowHost/SqlHarness.Mcp.WorkflowHost.csproj --no-restore` — initially blocked because the fresh worktree had no `project.assets.json`; the subsequent restoring build passed with zero warnings/errors.
2. `dotnet build tests/SqlHarness.Mcp.WorkflowHost/SqlHarness.Mcp.WorkflowHost.csproj` — passed with zero warnings/errors.
3. `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~Artifact_review_study_freezes_four_set_batch_revisions_and_late_assertions --logger 'console;verbosity=normal'` — environment setup failed at Dashboard `npm ci` with `ENOTEMPTY` while removing `ui/node_modules/@base-ui/react/checkbox-group`.
4. Retrying the same focused test found the partial install had a package-lock marker but no Vite executable (`'vite' is not recognized`). No further npm cleanup was attempted.
5. `$env:SQLHARNESS_109_CAPTURE_DIR='D:\Dev\sqlharness\.worktrees\plan-109\plans\109-evidence'; dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore -p:SkipDashboardUi=true --filter FullyQualifiedName~Artifact_review_study_freezes_four_set_batch_revisions_and_late_assertions --logger 'console;verbosity=minimal'` — passed, 1/1 test. The focused option skips only Dashboard UI install/build; it does not run the Windows verification gate.
6. `git diff --check` — passed.

The full Windows `just verify` gate is not part of Task 1 and remains for coordination on the final executable tree. The normal Dashboard UI dependency build also remains unverified in this worktree after the partial `npm ci` state.

## Files changed

- `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs` — one focused real-SDK study test and call evidence capture.
- `tests/SqlHarness.Mcp.WorkflowHost/WorkflowModule.cs` — offline canned measure-set/query behavior and validation that the measure fixture emits the expected 16 per-set statement records.
- `tests/SqlHarness.Mcp.WorkflowHost/WorkflowScenarios.cs` — exact synthetic SQL inputs and separate candidate revision identities.
- `plans/109-evidence/task-1-sdk-baseline.json` — reproducible captured output.

## Self-review

- The changes are test and evidence only; production source and public behavior are untouched.
- The test asserts four statements per compare variant, four distinct candidate hashes across two artifact IDs, the exact candidate plan repetition and statement selectors, four sets with per-table metrics and four statements each, and both later assertion result sets.
- All byte measures are separated into wire and model-visible bytes. Byte/4 estimates are marked as estimates; actual tokenizer counts remain unavailable.
- The captured tool outputs show no truncation, omitted items, or continuation pages at the selected 65,536-byte and 16,384-byte budgets.
- No plan/index completion status was changed. This commit is Task 1 only and does not include Task 2 candidate evaluation.
