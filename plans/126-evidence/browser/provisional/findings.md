# Interpretation of the provisional browser run

The original `acceptance.json`, `report.md`, screenshots and twenty-sample series are preserved unchanged.
They describe the 2026-11-10 19:28:32–19:31:43 UTC run on the first seeded home. They are provisional,
not evidence for the later final product build or revised fixture.

## Observed product findings

- Operation detail has an unlabeled Monaco textarea in both themes (`label` axe violation). The
  coordinator traced this to diff mode updates replacing the side labels; a later product fix needs
  a new actual-browser scan.
- `operation-390.png` visibly says `orders · v49 → v50` in the operation heading while the same
  screen's comparison context and script actions use `v48 → v49`. This conflicting identity requires
  correction and a regression check.
- The Results summary card clips its conclusion at 390px (`results equival…`). Essential text
  appears elsewhere, but the card does not satisfy the frozen typography criterion.
- Facet input-to-paint p95 is **200.6ms**, exceeding the frozen **200ms** threshold. The 0.6ms
  overage remains a failure; no threshold was changed. Other sampled actions passed. This series
  was taken on the recorded host/build and must not be silently replaced or described as passing.

## Harness and fixture corrections before the final run

- The first selected-case API request used raw case ID `0`; the dashboard comparison contract uses
  `synthetic-case-0`. The resulting 404 was a harness defect. Canonical IDs are used in the updated
  harness.
- The seeded raw scope `whole` is projected as `whole query`. The first session links hardcoded
  `whole`, so the UI correctly used its default non-adjacent pair (`v0 → v49`). This also caused the
  first case-dialog assertion to inspect independent rather than recorded direct policy evidence.
  The updated harness takes the scope from the actual optimization DTO.
- The first approval close assertion queried every `role=dialog`; Base UI's restored notification
  toast also has that role. The remaining toast was misreported as an open modal. The updated check
  targets the approval's `data-slot=dialog-content` and then tests focus return separately.
- Initial fixture case 0 has reads 171 → 162 and policy failure but IO `same`: the nine-read decrease
  does not clear the existing absolute IO threshold. The coordinator is preparing a fresh v2 journal
  with higher initial reads to exercise the required IO `better` plus recorded policy `fail` case.
  No outcome threshold or product policy should change to accommodate this fixture.
- Provisional Usage readiness waited only for its heading. Screenshot inspection shows that the
  statistics body was still a skeleton. Its provisional axe/reflow checks cover the shell/loading
  state, not representative populated Usage. The updated harness waits for a visible Usage summary
  containing 10,000 operations and completed fetches. Those results need a new run.

## Evidence retained

Actual non-adjacent independent pairing preserves 20 unavailable recorded-policy outcomes. Activity
search/facet survives detail navigation and browser back/forward. Keyboard skip navigation, the
synthetic failed reason, two 500-operation history pages, populated Profiles, intercepted
save conflict and HTTP 401/408/500 feedback passed. All 24 six-route width checks at
390/768/1440/1920px found no page overflow; this does not prove every essential cell is unclipped.

| Action | Samples | p95 ms | Threshold ms | Maximum long task ms |
|---|---:|---:|---:|---:|
| Search | 20 | 102.1 | 200 | 60 |
| Facet | 20 | 200.6 | 200 | 153 |
| Session expansion | 20 | 109.9 | 200 | 72 |
| A/B selection | 20 | 93.8 | 500 | 109 |
| Case change | 20 | 310.4 | 500 | 160 |

Screen-reader acceptance, native 200% browser zoom and complete manual non-text contrast remain
unverified. Additional transient-state, clipboard, rejected-operation and large-SQL checks were added
to the harness for a later run; adding them is not evidence that they passed. No category is graded
9.0 from this provisional evidence.
