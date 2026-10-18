# Plan 088 whole-branch review

Base `272d5dbb5f721536f85355b925273507e22de67b`; head `f65dd68`. Reviewed the specification, plan, product diff in passes (journal/pairing, graph/source map, API, UI, docs/tests), and task evidence. Prior task reports were treated as claims rather than substitutes for code inspection. I did not rerun the full gate. The recorded Windows gate passed on the same product tree with the later committed `PlanTree` deletions; Linux and live database behavior remain unverified.

## Strengths

- Raw document SHA and shape identity are separate. Journal occurrences retain repetition, document and statement identity; the sensitive-storage gate also protects reads of retained plans.
- ScriptDom pairing uses executed SQL spans and records uncertainty explicitly. Graph extraction preserves own-operator properties, and the source map avoids asserting lexical scope from graph proximity.
- The dashboard serves bounded graph, property, manifest and source-map responses. The UI has document-scoped canvases, explicit confidence states and a shared SQL/operator selection model. The synthetic browser evidence exercises actual canvas and Monaco interaction.
- Focused tests cover the distinctive repetition, PSP, CTE, module and omission cases. The Windows gate is green: UI 300, Core 4,375, MCP 430 passed; the two localhost connection diagnostics and DEP0190 remain non-failing diagnostics.

## Issues

### Critical

None.

### Important

1. **SQL selection can leave the selected document with no visible graph.** `src/SqlHarness.Dashboard/ui/src/components/plan/PlanWorkbench.tsx:176-183` searches all links to choose a statement, although `visibleDocuments` at lines 96-102 is restricted to the selected document. If another document has the first overlapping link, the handler switches to its statement while keeping the document filter. The SQL highlight then has no corresponding visible operators, despite a valid link in another document. Restrict statement choice to links in the selected document when it is selected; if no eligible link exists, keep the current statement and expose the absence of a link. Add a multi-document interaction test.

### Minor

1. **Storage-gate wording omits the read-time effect.** `README.md:554` says plans and SQL are available when the setting was enabled *for the operation*. The API also checks the current setting (`src/SqlHarness.Dashboard/JournalReader.cs:688-689`), so turning it off hides previously retained plans. State both conditions in the README to prevent an operator from mistaking hidden data for lost data.
2. **Source map builder has concentrated responsibilities.** `src/SqlHarness.Core/Plans/SourceMapBuilder.cs:1` combines ScriptDom scope enumeration, XML object extraction, graph matching, and confidence decisions in 883 lines. It is currently tested and can merge, but splitting the scope inventory and matching rules into cohesive internal units would make future inference rules easier to review.
3. **API selection coverage is thin across cases and documents.** `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs` exercises the source-map endpoint, but does not assert that selecting a second case and a second document returns only their matching occurrences/links. This is a targeted follow-up test because the UI depends on those selectors.

## Declined to judge

- Live SQL Server plan fidelity and user acceptance: the plan and evidence use synthetic/hand-written fixtures; no live database or UAT was authorized for this review.
- Linux behavior: the user explicitly skipped Linux verification.
- Unidentified non-failing localhost `ECONNREFUSED` diagnostics and existing Node `DEP0190`: the final gate passed and the available logs do not tie these diagnostics to a Plan 088 behavior defect.
- Follow-up-plan features (expression lineage, module definition fetch, estimated plans, query capture, plan comparison and PostgreSQL graphs): explicitly outside Plan 088.

## Recommendations

Fix the document-filtered SQL selection and add a focused interaction test before merge. The three minor items can be deferred with an explicit owner or fixed in the same scoped pass if inexpensive. No broad test rerun is warranted unless product behavior changes beyond the focused UI fix.

## Assessment

**Ready to merge? With fixes.** The main contracts and gate are sound, but the document selection bug breaks two-way linking in a supported multi-document workflow. A focused fix and re-review should close the remaining merge blocker.
