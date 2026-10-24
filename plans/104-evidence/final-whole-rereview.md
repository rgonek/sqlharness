# Plan 104 final fix-wave rereview

Scope: prior whole-review finding and new breakage in `6aa801a3..f22137e8`. I read the fix report, complete product fix diff through `9fc113a3`, the subsequent evidence-only `9fc113a3..f22137e8` delta, affected source/tests, and recorded focused logs. The latter delta adds the branch-format log and corrects report chronology; `src`, `tests`, and `docs` are unchanged. This was a read-only review; I ran no tests. The post-fix full Windows gate remains controller-owned.

## Finding disposition

- **P2 plan-side shape inference: ADDRESSED.** `StatementReconciler.Normalize` now groups only explicit `table-variable` kind or `@` identity as anonymous variables. A `kind=table` plan object named `#ABCDEF12` stays an object key and can match producer-classified local-temp IO. Tests cover that exact case and a mixed variable/local-temp case. The new typed reconciliation key also prevents a real object named `table variables` from colliding with the aggregate; swapped-counter coverage checks the status remains Partial.
- **Earlier full-gate synthetic statement fixture: ADDRESSED.** The affected synthetic `TableIo` fixtures now explicitly carry `AnonymousTableVariable` provenance. This fits the accepted producer-provenance rule without reclassifying legacy rows by display name.
- **Earlier MCP annotation catalog budget failure: ADDRESSED.** Measure/compare descriptions use a short SQL Server-only pointer, and the recorded targeted catalog budget test passes under the unchanged 32768-byte ceiling.
- **Earlier PostgreSQL foreign wording failure: ADDRESSED.** SQL Server-specific IO wording is emitted only from `BenchmarkDescription` for SQL Server. PostgreSQL `SqlHarnessEngineGuidance.TableIo` is null and omitted from JSON. Fixed and request MCP capability builders choose the resolved or request-scope SQL Server engine respectively; the fixed-scope `with` copy retains guidance. The MCP schema test checks both serializations.

## New breakage assessment

No new confirmed issue in the fix diff. The additive nullable `engineGuidance.tableIo` leaves the neutral and PostgreSQL guidance without the field, carries the complete experimental qualifier for SQL Server, and increments the capabilities contract to 9. Existing query safety and setup guidance remain in their engine-scoped descriptions. The `WithInputRootPage` copy retains the new guidance and continues measuring serialized response size against the result budget. The concise tool descriptions retain the compare verdict, equivalence, setup, failure, dry-run, hash and statement-evidence qualifiers that the fix touched.

The supplied final focused logs report 142/142 Core, 65/65 MCP, and the targeted MCP catalog budget test passing. The post-commit branch formatter log reports `format: OK` for 21 changed C# files. I did not rerun these checks.

## Declined to judge

- Full post-fix Windows `just verify`: controller is rerunning it; its direct result is required before completion.
- Linux, installed binary and implementation live UAT: outside this scoped offline fix review and not claimed.
- Cross-version guarantee for the bare `#8hex` producer rule: the user accepted the characterized experimental rule with this limit recorded.
- Unchanged product paths outside `6aa801a3..f22137e8`: already covered by the original whole-branch review, not reassessed here.

**Scoped verdict: APPROVED.** The prior P2 finding and the reported gate failures are addressed in this fix wave; no new blocker was found. Overall branch readiness still depends on the controller's post-fix full Windows gate and final plan/index evidence update.
