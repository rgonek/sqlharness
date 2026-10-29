# Screenshot follow-up: combined final review

Reviewed product range `3f95e9b9..f47e903c`, the supplied combined diff, screenshot specification, follow-up plan, three task reports/reviews and scoped fix reviews, ledger, current draft evidence report and selected current captures. This is the bounded combined review of the fifteen screenshot requests plus A/B, not a review of all earlier layout-polish work. Used the shadcn skill; circular A/B geometry and the joined KPI band are explicit user exceptions.

**Verdict: CHANGES REQUIRED. One P2 integration finding and three P3 visual corrections for the single final fix wave. No P0/P1 findings.**

## Quality finding

### P2 — Handle failed dimension-option queries before rendering Usage

Location: `src/SqlHarness.Dashboard/ui/src/pages/StatsPage.tsx:91-98,140-148`.

With a retained nonempty dimension filter, changing the range creates two different query keys: unfiltered `dimensionOptions` and a disabled filtered aggregate `query`. If the overview succeeds but the selected-profile dimension-options request fails, `dimensionOptions.isPending` becomes false with no data. The filtered query remains disabled/pending with no error. The render branches check only overview/query errors and then dereference `dimensionOptions.data!.profileDimensions`, throwing instead of displaying the request failure. This is introduced by the split options/aggregate query lifecycle; the existing disappearing-dimension success regression does not cover it.

Independent read-only probe using the installed `@tanstack/react-query` QueryClient/QueryObserver reproduced the exact relevant states after a rejected options request:

```text
dimensionOptions: isPending=false, error="dimension lookup unavailable", data=undefined
filtered query:   isPending=true, error=null, data=undefined
current render branch: TypeError: Cannot read properties of undefined (reading 'profileDimensions')
```

This was a bounded in-memory query-state probe, not a new product test or an end-to-end browser reproduction.

Fix: handle `dimensionOptions.error` explicitly and only render StatsContent with actual option data. Retain the prior fix's shared effective filters and disabled aggregate while options load. Add a focused regression: select a dimension, change range, succeed overview but reject selected-profile options, assert a visible error without a render crash or stale aggregate request, then verify recovery when the options become available.

## Specification/visual corrections

### P3 — Restore the distinct selected-A appearance

Location: `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:30-31`.

Both selected circles currently apply primary background/foreground. `session-app.png` confirms A and B are both dark. Requirement 16 restores the previous selection appearance; the controller-provided prior reference distinguishes grey A from dark B. Use the existing secondary variant for checked A and the existing primary/default variant for checked B; unselected circles remain outline. Preserve native inputs, useId names, labels, focus and keyboard semantics. This is a narrow correction to the approved exception, not a new custom color system.

### P3 — Allow transcript Clear to remain in the desktop status bar

Location: `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx:162`; adjacent existing `.settings-transcript-status > div` flex rule in `index.css:231`.

The action wrapper's `w-full` forces a new flex row even when the status/summary and action can fit in one bar. `settings-app.png` shows the extra desktop row. For screenshot 12, use an automatic-width wrapper with `ml-auto`/right alignment and preserve responsive wrapping, the coverage table and existing confirmation/error/pending paths. Do not change the destructive workflow.

### P3 — Finish the toast's requested mock headline

Location: `src/SqlHarness.Dashboard/ui/src/mutations/ApprovalToast.tsx:36,47`.

`mutation-toast-app.png` shows only black `staging`; the reviewed `mutation-mock.png` 1b headline is `Approve change on staging?`, with the risky environment in red plain text. Complete the existing Base Toast title with that wording and a semantic `text-destructive` environment span for prod/production/staging, consistent with the current dialog risk rule. Keep plain text rather than a Badge and retain unknown fallback, Review/Deny behavior and the SQL preview. This is mock fidelity, not an authorization or lifecycle defect.

## Combined integration conclusions

- Shared `JournalReader` and DTO edits retain both stats additions and requester projection. The stats scope predicate reaches count/status/day/SQL/table/wait/target/spill/cold-cache/workflow aggregates; `profileOperations` deliberately remains complete time-window chooser metadata. Dimension choices use the unfiltered options projection. The successful range-change fix removes absent names from both aggregate and panel scope; the error branch above remains to fix.
- SQL reads aggregate metric rows before joining operations, preserving operation counts/durations. Null, recorded zero, missing operations and missing metric rows remain distinguishable. The reads ranking is computed over all filtered SQL groups before the top-20 limit. The displayed definition is a sum of recorded representative medians, not total physical executions. Workflow KPIs exclude legacy artifact volume and do not invent chat usage or prices.
- Requester context reads only `mutation_requests.operation_id -> operations.session_id -> sessions.agent_kind`. Named-risk inspection confirmed existing `LinkMutation` digest/token/evidence-version checks and broker acceptance-before-added ordering; no new source guesses or execution authorization were introduced.
- SQL inline rendering uses the installed formatter and a separate immutable presentation value. The accepted cumulative escaped-literal budget fix is present; unsupported/ambiguous inputs keep exact As sent with explanation. The decision API still receives only request id/decision. Dialog delay, expiry, Enter guard, busy/error handling and writer/multiple-statement alerts remain present.
- `SegmentedRadioGroup` adds only optional disabled forwarding to the existing Base UI primitive; other callers retain their prior default behavior. Compact Session rows retain star PATCH, keep icon, flags, full-value title/detail access, return context and existing live invalidation. Version inputs remain native isolated groups. Operation/IO CSS changes are scoped.
- No further actionable combined-source or shadcn-composition issue was identified within this bounded review. Existing default variants/semantic colors are retained except for the explicitly approved visual cases and the corrections above.

## Evidence considered

Accepted recorded focused task evidence without rerunning it: Task 1 final aggregate 116 UI checks; Task 2 .NET 11 and final UI 17; Task 3 .NET 35 and final UI 18; reported typecheck/lint/build and branch formatting for four C# files. Prior independent task reviews accepted their corrected scopes. Controller records browser controls 26, Usage 12, Session SSE 5, generic interactions 10 and mutation fixture checks 25, including SQL/context/modes, delay/Enter, two safely mocked Deny requests with the negative 500 path, and unknown/unsupported fallback. Selected screenshots were directly inspected for the three visual findings.

The only fresh executable evidence from this combined review is the small query-state probe above. No tests, full gate, browser decision, DB write, source/index/HEAD mutation, or agent delegation was performed; only this review artifact was written. Existing controller plan/evidence work was left untouched.

## Declined to judge / evidence boundaries

- Final Windows `just verify`: pending controller work after fixes. No full-gate PASS or implementation-complete claim is made here.
- Final screenshots and interactions after the unified fix wave: current captures describe the reviewed tree, not future source. Final desktop/mobile reflow, focus/hover and no-overflow claims require refreshed evidence where changed.
- Pixel-perfect agreement for all historical mocks, all viewport widths, every long text/coverage-table combination, dark mode and all browser/assistive-technology implementations: not established by this bounded source review or the selected captures.
- Real database/mutation execution, live approval or denial, production credentials, installed binary behavior, restart, publication, merge and push: neither performed nor accepted. Fake API decisions do not establish live acceptance.
- Linux verification: not run or claimed.
- Whole-repository security, earlier layout-polish work, all Core execution/classification/digest machinery and unrelated UI: outside this diff review. Adjacent reads were limited to named linkage, scope, primitive and layout risks.
- Universal SQL dialect/type correctness or a formal parser/security proof: not claimed. The supported display subset, conservative exact-original fallback, accepted lexical tests and bounded expansion remain the evidence boundary; execution does not consume the preview.
- Large-journal performance, full-history resource scaling, arbitrary concurrent event races and new benchmarking: not measured. No speculative performance redesign is requested.
- Statistical truth beyond retained journal evidence, physical execution-total reads, absent per-agent costs/chat usage and historical reconstruction: intentionally unavailable; synthetic browser fixtures are not proof of real collection.
- Final plan/index/history status, draft report placeholders, artifact archival and removal of the tracked task-3 report from `.superpowers`: controller documentation closeout, not additional product findings. Pending placeholders do not prolong this review.

Re-review should be limited to these four fixes and directly affected regressions, followed by the controller's final gate/evidence closeout.
