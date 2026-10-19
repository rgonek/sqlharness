# Plan 082 verification evidence

## State and exact product tree

Plan 082 is DONE and locally merged into `main` at `dd375124d6ca442ea03dadb7cda90cb9489034b8`. Tasks 0–5 have clean scoped reviews. The Sol low whole-branch review's two Important findings were fixed together, and its scoped re-review approved both fixes with no new breakage or remaining findings. The final verified product revision is `b9f60924d09cdfa13f07106662549fcca8ee1f56`.

An earlier integrated Windows gate tested product revision `7f98ef99f731af5fbc2ba37f8bb6e0d453cd6c34` (`7f98ef9`). Feature commits `dd0544d` (narrow IO attribution and legacy hash fixes) and `c60b1b6` (hand-corrected branch C# formatting) preceded integration of main baseline `ca4c8b8` into the feature branch as `5bfa9f3`. The post-integration fixes are `c7bd3d7` (TypeScript test query type fix) and `7f98ef9` (contract expectations for schema/API/fallback). The final review fix and its repeated green gate on `b9f6092` are recorded below. Later documentation edits do not change that tested product tree.

## Task review ledger

| Task | Reviewed implementation range | Result |
|---|---|---|
| 0 | `9dc73f6..03716f8` | Clean spec and quality review; spike evidence is separate from Task 5 UAT. |
| 1 | `03716f8..8e9ca2b` | Clean scoped review; root executions remain independent from object IO completeness. |
| 2 | `8e9ca2b..fca7755` | Clean spec and quality review; median-total-run reconciliation, omission persistence, and empty statement sets were reviewed. The 45/45 focused run was pre-final-edit working-tree evidence, not verification of committed `fca7755`; see the Task 2/3 correction below. |
| 3 | `fca7755..a06e7e5` | Clean scoped review after mean additivity, measured-row semantics, and badge-basis fixes. |
| 4 | `a06e7e5..14518e8` | Clean scoped review after parameter-set plan identity, PostgreSQL Markdown wording, migration coverage, and sort coverage fixes. |
| 5 | `14518e8..c7b9e4a` | Clean scoped review of the Task 5 fixes, UAT, verification, and documentation. |
| Whole branch | `ca4c8b8..c7b9e4a` | Sol low found two Important breakdown issues; both were fixed in `b9f6092`. |
| Final scoped re-review | `c7b9e4a..2184c13` | Sol low approved both fixes; no new breakage or out-of-scope observations. |

The feature branch included main baseline `ca4c8b8` through merge commit `5bfa9f3` before final verification. The user-authorized local merge into `main` is `dd375124d6ca442ea03dadb7cda90cb9489034b8`. Its complete tree matched the reviewed feature head, and a separate product-path comparison against verified revision `b9f6092` was empty. Consequently no redundant integrated-tree gate was run; subsequent changes are documentation only. No push was made.

Raw log paths below identify temporary verification locations. The completed plan's SDD workspace is cleaned after integration; this committed evidence and the synthetic plan fixtures preserve the results and reproduction context.

## Earlier integrated verification (7f98ef9)

The explicit branch C# formatting check passed on that earlier integrated product tree:

```powershell
pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef ca4c8b8
```

It checked 23 branch-changed C# files and exited `0`. `just verify` also exited `0` directly on the exact product revision above, with five stages in 5m44s:

- Build passed in 31.9s.
- UI check passed in 3m34s: 60 test files and 312 tests passed; lint and typecheck passed.
- .NET tests passed in 2m10s: `SqlHarness.Tests` 4,505 passed / 11 skipped / 0 failed; `SqlHarness.Mcp.Tests` 435 passed / 4 skipped / 0 failed.
- The gate's format substage skipped because it found zero C# files changed in the clean working tree. The separate branch formatting check above covers the feature diff against `ca4c8b8`.

The UI check log contains `ECONNREFUSED` diagnostics for localhost port 3000 after the test output, but the stage exited `0`; this is not described as pristine-console output. The full gate log and direct exit capture were retained under ignored scratch directory `.superpowers/sdd/082-statement-io-attribution/task-5-scratch/verify-final-7f98ef9/` and `just-verify-final-7f98ef9.exit`. Linux verification was explicitly skipped. No extra gate was run after documentation edits.

Before the final green run, two failed full-gate attempts exposed real test/fixture issues and were corrected:

- The first integrated gate run found an unsupported `exact` option in the Plan 082 `OperationPage.test.tsx` role query. Removing that option preserved the exact accessible-name match. Focused UI typecheck exited `0`; `OperationPage.test.tsx` passed 21/21.
- The next run found three stale assertions: the operation-detail property contract lacked `omittedStatements` and `statementAttribution`; a migration test pinned journal version 17 although current schema is 19; and an Optimization fixture with no statement evidence expected a changed-statement view. The contract now names both fields, the pin is 19, and the incomplete fixture asserts whole-batch fallback plus the exact note `per-statement attribution incomplete`. Those checks passed 10/10 before the final gate.

The initial `c60b1b6` gate attempt also hit MSBuild DLL locks from test-owned dashboard process 10948. Its executable was the Plan 082 worktree CLI, and its start time matched the isolated scratch dashboard state. A normal `dashboard --stop` using the exact isolated `SQLHARNESS_HOME` stopped it with direct exit `0`; the subsequent gate was rerun. The first stop had pointed at the nested `home` directory and missed this session. No unrelated process was stopped.

## Operator-authorized synthetic UAT

The UAT target was the named `civiclens-dev` SQL Server profile on `localhost` / `CivicLens`, using integrated authentication. `SQLHARNESS_HOME` pointed to a Task 5 scratch home containing only that named profile; the operator's normal home, journal, and config were not modified. A read-only identity query verified the target before measuring. No persistent SQL, approval, other database, or credential output was used.

The synthetic batch created a uniquely named local `#temp` seed with generated rows, populated a table variable, and compared two reads of `@work` with one read in the candidate. Setup and measured SQL used local temporary objects only. The captured actual Showplan documents are retained as test fixtures:

- `tests/SqlHarness.Tests/Fixtures/Plans/082-scalar-baseline-setup.sqlplan`
- `tests/SqlHarness.Tests/Fixtures/Plans/082-scalar-baseline.sqlplan`
- `tests/SqlHarness.Tests/Fixtures/Plans/082-scalar-candidate-setup.sqlplan`
- `tests/SqlHarness.Tests/Fixtures/Plans/082-scalar-candidate.sqlplan`

The final measure returned direct exit `0` and dashboard operation 6 reported `statementAttribution=confirmed`, zero omitted statements, 9,019 median logical reads, and one scan for the changed aggregate. The final compare returned direct exit `0`, equivalent results, and one case whose dashboard outcome was `better`. The changed work was 20 reads / two scans before and 10 reads / one scan after. Shared preparation was 9,009 reads on each side; therefore whole-batch totals were 9,029 before and 9,019 after. The dashboard API reported `verdictBasis=changed statements`, `view=changed`, no fallback note, and the additive whole-batch line `whole batch: 9029 → 9019 reads (shared statements: 9009)`. The regression policy was inconclusive only for unstable elapsed time; the logical-read result was not regressed (0.11% fewer reads). This elapsed-time policy result is separate from the better per-case read verdict.

Exact measure/compare logs, direct exits, selected dashboard summaries, and generated SQL are retained in the ignored Task 5 scratch directory. This durable record intentionally contains no business rows, login, credentials, or tokens.

## Decisions preserved from the controller ledger

The following cross-task decisions and their costs are retained so future changes do not silently broaden the contract:

1. Store omitted statement counts once per selected median-run variant/case on the existing metrics row, defaulting to zero. Omission belongs to the whole run even when no statement rows remain; storing it elsewhere risks duplication and would require a schema/reader/API change.
2. Reconciliation of an empty statement list returns an empty status list, not a synthetic sentinel. Consumers derive unavailable attribution from absent evidence. If this is wrong, explicit batch-status schema/API metadata and downstream no-plan tests will be needed.
3. A single-run measure statement uses status `measured`, puts values in after fields, leaves before fields null and change empty, and reports attribution separately. If this shared-row shape is wrong, the API/UI vocabulary must change.
4. Missing runtime IO metadata is neutral only for an object-free `Compute Scalar` when all object counters reconcile. Root executions remain independently nullable; positive unidentified IO or object-operator gaps remain incomplete. The cost of an unforeseen scalar IO shape is false confirmation and a narrower rule/rework.
5. That neutral-metadata rule also covers object-free `Nested Loops` because the captured children own reconciled IO and the loop reports executions only. Positive unidentified IO, object-bearing gaps, spools, and TVFs remain incomplete. The bounded risk is unseen internal loop IO.
6. The same rule covers object-free `Stream Aggregate` because the captured aggregate folds its child stream and owns no object IO. The whitelist is exactly `Compute Scalar`, `Nested Loops`, and `Stream Aggregate`; positive IO and all unlisted operators stay fail-closed. The bounded risk is unseen aggregate-internal IO.

The captured fixture tests verify the real plans become confirmed after reconciliation, while negative tests keep unknown object-free operators, positive unidentified IO, and spool/TVF uncertainty partial.

## Final whole-branch review fix wave (2026-10-19)

The two Important findings in `.superpowers/sdd/082-statement-io-attribution/final-review.md` were fixed together in product commit `b9f60924d09cdfa13f07106662549fcca8ee1f56` (`b9f6092`). The comparer now joins per-statement objects by case-insensitive object name and computes before/after counters from their respective selected cases. Confirmed statement absence contributes zero to sum and mean; unavailable attribution leaves counters null, and incomplete cases continue to force whole-batch basis. The single-run measured after-only row contract remains unchanged. No journal schema, extractor, or live SQL path changed.

Focused regression evidence:

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~OptimizationComparerTests` exited `1` before the fix: 33 passed, 2 failed. `Compared_statement_objects_join_by_name_and_report_the_side_to_side_change` received two duplicate `dbo.T` rows (20 → 20 and 10 → 10); `All_case_branch_uses_confirmed_absence_as_zero_and_mean_includes_it` expected 10 and received null.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~DashboardServerTests.Optimization_endpoints_validate_selectors_and_return_scoped_comparison|FullyQualifiedName~DashboardServerTests.Optimization_compare_endpoint_returns_one_object_row_with_side_specific_counts"` exited `0`: 37 passed, 0 failed. Coverage includes case-insensitive joined comparison rows, one-sided objects, confirmed missing-case mean values, unavailable-attribution nulls, and serialized comparison API output.
- GREEN: from `src/SqlHarness.Dashboard/ui`, `npm test -- src/components/optimization/StatementRows.test.tsx` exited `0`: 1 file and 4 tests passed, including one row rendering 20 → 10.
- Formatting: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef ca4c8b8` exited `0` on the final product tree and checked 23 branch-changed C# files. The first invocation flagged only an extra trailing CRLF in the three edited C# files; it was removed manually and the command was rerun successfully.

The Windows verification gate (`scripts/verify.ps1`, the command used by `just verify`) exited `0` directly on product revision `b9f60924d09cdfa13f07106662549fcca8ee1f56` in 5m47s. Build passed with 0 warnings and 0 errors; UI check passed 60 files / 313 tests, lint, and typecheck; .NET tests passed (`SqlHarness.Tests`: 4,509 passed / 11 skipped / 0 failed; `SqlHarness.Mcp.Tests`: 435 passed / 4 skipped / 0 failed). The gate format stage skipped because the clean committed tree had zero changed C# files; the explicit branch format check above covers the product diff. Direct exit was captured in `.superpowers/sdd/082-statement-io-attribution/task-final-fix-verify-b9f6092/just-verify.exit`; stage logs were in its `logs/` directory. The UI check log includes `ECONNREFUSED` diagnostics for localhost port 3000 after successful tests and a successful stage exit; they are not called pristine-console output.

Linux verification remains unrun, as explicitly requested. The final Sol low scoped re-review approved both fixes with no new Critical or Important breakage and no out-of-scope observations. No synthetic SQL Server UAT rerun was needed because these fixes only change comparison presentation and aggregation in the Dashboard.

## Task 2 and Task 3 evidence correction

Task 2's report records a focused 45/45 test run, but the final edit narrowing `JournalStatementMetrics.UdfElapsedMilliseconds` from `double?` to `long?` happened after that run. The edit left `JournalBenchmarkBuilder` assigning a `double?` value to a `long?` property, so committed revision `fca77555843cfe67277b092401ec57a0c26238a3` did not compile and was not verified by the 45/45 result. Task 3 commit `d966484` fixed the precision mismatch by preserving fractional milliseconds as `double?` and SQLite `REAL`, then reran the impacted suites: comparer/reader 47 passed, journal/endpoint 47 passed, and contract/fractional-UDF tests 9 passed. Follow-up Task 3 fixes are in `a06e7e5`. The Task 2 review approval does not convert the earlier working-tree test result into committed-tree evidence.

## Task 5 legacy-hash compatibility correction

The later failing test `Statement_metrics_hash_uses_the_paired_full_executed_span` was meaningful compatibility coverage, not stale-test noise. It compares the journal's legacy full executed-span `StatementHash` with the full artifact span hash; it does not compare the new normalized `StatementKey`. The builder had restored the old hash only when `StatementId` was present, although the retained fixture had valid per-document statement spans without IDs. The Task 5 fix resolves the exact retained `PlanDocumentOrdinal` and `PlanStatementIndex`, then uses only an existing, bounded, valid `StatementSpanPairer` span; it does not guess when pairing is absent and retains the plan-text fallback. A no-pair regression test preserves that boundary. The broad extractor/reconciler/builder suite then passed 48/48.
