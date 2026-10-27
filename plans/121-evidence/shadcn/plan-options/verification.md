# Plan options follow-up

Branch/worktree: `dashboard-shadcn-fixes`. Base: `3eff7808`; final product candidate: `aaa346e0`. Latest operator clarification is authoritative: **standard visible RadioGroup for Plan A/B, with styling confined to the A/B badges**. The earlier segmented-radio and interim Tabs approach are superseded.

## Implementation

- Plan version uses unchanged shadcn RadioGroup/RadioGroupItem/Label, with ordinary visible 16px radio indicators. Existing grey A / dark B Badge labels remain. The unused soft appearance was removed from the application SegmentedRadioGroup; other joined option groups retain their existing appearance.
- Repetition moved into a default-closed shadcn Collapsible labelled Advanced. It remains the same recorded-repetition selection; no SQL execution, median changes or data collection added.
- The Advanced trigger stays in the existing toolbar portal, while its content remains in the same pending-protected plan subtree. React context connects them without a new popup portal.
- Document Select popup uses content width, at least the anchor width, with a viewport maximum and reserved checkmark space. Default selected-item alignment and primitive sources remain unchanged.
- Per-document unmatched warning boxes are removed from the main surface. One concise SQL-linking count is available only inside Advanced. Omitted/not-stored/error evidence remains distinct and visible.
- The single-statement “Batch SQL stays visible…” placeholder now renders nothing; the actual multi-statement selector is unchanged.
- During 1280px → 390px QA, the implicit SQL grid track retained Monaco's old wide minimum. An explicit minmax(0,1fr) column (`grid-cols-1`) allows the editor to resize within the page. [RED geometry](overflow-red.json).

Generated `components/ui/*` sources are unchanged. No backend/API/storage changes. Read-only source review found no issues with accessibility, resets, Collapsible context across the portal or pending containment.

## Checks

- TypeScript and lint passed.
- Updated focused tests passed: three files, **10 tests**; verify standard radio keyboard behavior, case/repetition/document reset paths and collapsed diagnostics. [Log](focused-tests.log).
- Browser checks use the actual built UI with browser-only fixtures: **4 repetitions, 16 documents, 4 unmatched fragments**. No journals/databases are modified.
- [Result](results.json): default repetition hidden; native radio indicators visible at opacity 1 and 16×16; keyboard selection and focus retained; repetition changes under Advanced; repeated notices and static batch hint absent; complete document labels fit popup bounds/checkmark gutter at 1280px and 390px; no whole-document overflow or page errors.
- [Gallery](index.html): four interaction states plus two focused document-menu captures.

An intermediate focused run tested the briefly considered Tabs implementation and failed its automatic-arrow-activation assumption. That approach was not delivered: the operator explicitly clarified visible standard radios, and the final radio test passes. An unsupported Testing Library exact option was also removed before final typecheck. These intermediate runs are not passing evidence.

## Final gate / delivery

Full Windows `just verify` passed on aaa346e0: 5 stages / 16m08s; UI 386, Core 4722 (11 skipped), MCP 535 (4 skipped). The existing ignored output-only xUnit configuration keeps collections sequential within each project; no source timeout changes or test filters. Publication follows the expanded [UI information and resize scope](../ui-information/verification.md) at 58a29997. Main remains untouched; no merge/push.

Latest operator screenshot correction supersedes only the visible-radio presentation: Plan uses the same joined segment appearance as Side by side / Unified. Repetition/Advanced and document width fixes remain. See [current follow-up](../audit-recommendations/verification.md).
