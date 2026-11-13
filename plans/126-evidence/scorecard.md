# Dashboard quality scorecard

Assessed product: `85c75cb2f2219ac36c0c4e89ea362e99968f0c71` on `dashboard-quality-9`, 2026-11-11.

These expert ratings describe the approved representative workflows and synthetic datasets. All14 categories meet the frozen >=9.0 threshold on the verified feature branch. [Native NVDA acceptance](browser/native-accessibility/nvda-acceptance.md) and the [final Windows gate](verification.md) pass. A usability study and production-scale performance are outside the evidence collected here.

| Category | Current rating /10 | Evidence and assessment |
|---|---:|---|
| 1. Typography and readability | 9.0 | SQL14px/22px, supporting text13px, tabular numeric columns, explicit units and aggregation labels; desktop/mobile screenshot inspection. |
| 2. Visual hierarchy | 9.1 | Active A/B pair, main result metrics and independent Results/IO/Policy appear before SQL/details; failure-first case diagnostics. |
| 3. Spacing and alignment | 9.0 | Shared control/card rhythm, wrapped metadata, bounded SQL frames and consistent table axes across four viewport widths. |
| 4. Layout and information density | 9.0 | Loaded-data counts and paging retained; version table has bounded vertical scroll and pinned identity/A/B. Narrow identity leaves room for horizontal metrics; mobile scroll guidance. |
| 5. Colour and contrast | 9.0 | Twelve light/dark axe scans have zero violations; sampled controls, focus, stars, chart roles and graph boundaries measured. Dedicated SQL-comment colour meets contrast on diff backgrounds. |
| 6. Component consistency | 9.1 | Shared CopyButton feedback/manual fallback, standard buttons/fields/dialogs; selected A and B use the same treatment. |
| 7. Information architecture and navigation | 9.2 | Validated URL selections, bounded Activity context/scroll restoration, session/Usage/Profiles display preferences; stale case bookmarks normalize with history replacement. |
| 8. Workflow and efficiency | 9.1 | Detail round trips retain context; case/aggregate/A/B changes and script actions remain bound to evidence. Five twenty-sample series meet frozen response thresholds. |
| 9. Discoverability and interaction | 9.0 | Named copy/script controls, explicit selected/pending states, bounded resize, actionable retries and keyboard dialog return. |
| 10. Data, SQL and comparisons | 9.1 | Correct selected-case medians/sum/mean, scans and supported nullable IO; zero differs from missing. Full SQL reaches Clipboard API despite bounded preview. |
| 11. Interpretation and trust | 9.2 | Strict eligible measurements, direct/independent/approximate provenance, honest partial attribution, weighted ambiguous coverage and separate policy/IO outcomes. Actual journal-to-DTO-to-UI checks pass. |
| 12. System states and feedback | 9.0 | Delayed comparison, rejection, empty Activity, disconnect/reconnect, HTTP401/408/500, save/clipboard failure and synthetic approval states exercised. |
| 13. Language and microcopy | 9.0 | Consistent English actions and case/aggregation/provenance labels; contextual explanations for missing/approximate evidence and retained error reasons. |
| 15. Accessibility and resilience | 9.0 | Browser keyboard/focus, twelve axe scans, reduced motion, actual200% zoom/reflow and large fixtures pass. Actual NVDA17-step acceptance confirms names/selections, case-table failure, read-only SQL, copy feedback and retained focus after copying. |

**All-category >=9.0 acceptance: achieved on `dashboard-quality-9`, final product85c75cb2.** The post-unlock NVDA run closes category15. Ratings describe the approved representative scope; the native report records targeted focus setup and the absence of an audible/braille or blind-user study.

## Evidence

- [72/72 browser checks and raw performance](browser/acceptance-5f05ca07/report.md), [exact assertions/data](browser/acceptance-5f05ca07/acceptance.json.gz).
- [Native accessibility report](browser/native-accessibility/report.md), [current control contrast](browser/native-accessibility/final-5f05ca07/contrast.json), [graph samples](browser/native-accessibility/final-5f05ca07/graph.json), [native zoom](browser/native-accessibility/final-5f05ca07/zoom.json), [NVDA blocker](browser/native-accessibility/screenreader-blocked.json).
- [Whole-branch review](final-branch-review.md), [review resolution](final-review-resolution.md), [browser defects and fixes](browser-iteration.md).
- Backend, comparison, shared and Activity implementation reports alongside this scorecard. Explicit branch formatting:13 C# files PASS. Full Windows verification passes: UI544, Core4873/11skips, MCP537/4skips; see [verification](verification.md).

Native/automated checks describe sampled states. Approximate comparison and plan graph rendering use clearly identified intercepted DTOs; direct and non-adjacent independent comparisons use the real isolated backend. No live SQL workload, mutation approval, installed dashboard replacement, merge, push or publication occurred.

The broad72-check browser matrix, contrast, zoom and responsiveness evidence is from5f05ca07. The only subsequent product change is CopyButton pending-focus handling (85c75cb2), verified by five focused actual-browser checks and the full17-step NVDA repeat. No unchanged performance series is promoted to a new run.
