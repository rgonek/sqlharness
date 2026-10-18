# Plan 088 final scoped re-review

Range `f65dd68..f503c22`. Scope: the four findings in the whole-branch review and regressions in the fix diff. I inspected the UI change and test, README change, API test, fix report, and verification record. I did not rerun the full gate. The recorded new-tree Windows gate passed all five stages: UI 301, Core 4,376 with 11 skipped, MCP 430 with 4 skipped, build with zero warnings/errors, and formatting for one C# file.

## Findings disposition

1. **Important, document-filtered SQL selection — ADDRESSED.** `PlanWorkbench.tsx:181-186` now limits statement candidates to the selected SHA and ordinal. When no candidate overlaps, it retains the current statement; `PlanWorkbench.tsx:262` displays an explicit absence state. The new `VariantPanel.test.tsx` case covers overlapping spans/reused node IDs across documents and the no-eligible-link path. The change does not alter selection with “All plan documents.”
2. **Minor, storage-gate wording — ADDRESSED.** `README.md:554` now explains that sensitive storage must have been enabled at capture and must remain enabled at read time, and that disabling it hides retained data without deleting it.
3. **Minor, API multi-case/document coverage — ADDRESSED.** `DashboardServerTests.cs:921` seeds two cases, two repetitions, and four document hashes; it requests case B/repetition 2 and checks exact SQL, case identity, document SHA/ordinal, and an exact table link. No API production change was needed.
4. **Minor, SourceMapBuilder decomposition — DEFERRED, accepted.** The fix wave did not change the builder. Its size is a maintainability concern for future inference extensions, not a demonstrated Plan 088 correctness failure or merge blocker. The task ledger and fix report retain the deferral.

## New issues in fix diff

Critical: none. Important: none. Minor: none found.

## Declined to judge

- Live SQL Server fidelity and UAT: outside this synthetic-fixture review; no live validation was authorized.
- Linux behavior: explicitly skipped by the user.
- Existing non-failing localhost diagnostics and Node DEP0190: the passing gate does not establish their cause, and the fix diff offers no causal link.
- Explicit follow-up-plan features: outside Plan 088.

## Assessment

**Ready to merge? Yes.** The only merge-blocking finding is fixed and directly covered by an interaction test; the fix diff shows no new blocker. The remaining builder decomposition is an accepted maintenance deferral. This is a scoped re-review of `f65dd68..f503c22`, not a second whole-branch review or an integrated-tree gate.
