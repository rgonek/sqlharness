# Combined Plan 125/126 integration review

Reviewed the merge of quality branch `c062822a` and main `85d1c95e` before publication. Backend and frontend conflicts were resolved separately, followed by an independent read-only integration review.

The review checked contributor completeness, per-variant runtime eligibility, duplicate identity weights, selected-pair metrics/actions, reproduction mappings, pending-request boundaries, cancellation and additive DTO compatibility. No P1 findings. One P2 finding: default empty flat availability dictionaries masked populated paired availability in statement rows. Resolution: use flat dictionaries only when nonempty, otherwise show paired reasons. The regression failed before the fix (1 failed/8 passed), then passed (9/9); logs are retained with integration evidence.

Both independent implementations are retained: flat outcome/availability projections and weighted cases from Plan 126, plus nested outcome compatibility, timing contributors, page-server IO, per-variant eligibility and availability diagnostics from main's Plan 125. Contributor identities come from both snapshots, including metric-only ambiguity groups. Approximate comparisons retain raw values but do not classify IO as a verdict; incoming tests expecting the older wording/classification are adapted to that existing conservative behavior.

The review does not replace the final combined-tree Windows gate or synthetic browser checks. Their results are recorded in integration.md.
