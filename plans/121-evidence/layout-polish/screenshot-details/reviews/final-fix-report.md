# Final unified screenshot fix wave

Base: `f47e903c`. Product commit: `bbda23d2a2f7c5dfd2bc7ce42657e18e6a6404e2`.

Implemented only the four findings in final-review.md. Six owned UI source/test paths committed; existing controller plan/evidence edits were preserved. This report is an uncommitted local orchestration artifact.

## Changes and self-review

- P2 Usage: dimensionOptions.error now renders ErrorState before StatsContent. Actual option data is required before rendering; non-null assertions removed. The current effectiveFilters computation and aggregate enabled condition are unchanged. New regression retains project=alpha, changes range, succeeds overview while selected-profile options fail, observes Request failed without Usage summary or filtered aggregate request, then invalidates after options recovery and verifies alpha plus a recovered filtered aggregate and summary. No cohort/projection/query-key contracts changed.
- P3 A/B: selected A uses the existing secondary button variant, selected B the default primary variant, unselected outline. Circular shape and native inputs, names from useId, labels, descriptions, checked/onChange wiring and focus rings remain intact. No CSS-mirroring tests added; existing VersionTable semantic tests pass.
- P3 transcript Clear: only the action wrapper changed from full width to w-auto and ml-auto, preserving existing flex wrapping and all confirmation, pending and error paths. Existing Settings tests pass. Desktop/mobile reflow remains for controller fresh browser evidence.
- P3 approval headline: Base Toast title now reads Approve change on <environment>? with a plain text span using text-destructive for case-insensitive prod/production/staging, matching the dialog rule. Unknown fallback retained; no Badge. Six table-driven cases cover risky/ordinary/empty environments, wording, plain text and available Review/Deny controls. Existing decision lifecycle tests pass.

Original mutation payload, requester provenance, SQL preview budget/fallback, policy/guards, delayed approve, Enter protection, expiry, Review/Deny behavior, and all Task 2 cohort contracts remain unchanged. No C# or shared helpers, API contracts, plan/index, root artifacts, fixture servers or live services changed.

## Actual verification

Commands run from `src/SqlHarness.Dashboard/ui` unless noted:

- `npx vitest run src/pages/StatsPage.test.tsx -t 'retained dimensions survive'`: RED on base implementation, exact TypeError reading profileDimensions reproduced after failing options (1 failed, 15 skipped). GREEN after fix: 1 passed, 15 skipped. An earlier trial via npm incorrectly forwarded -t and had a fixture condition that failed the initial range; corrected before the meaningful RED/GREEN run.
- `npx vitest run src/pages/StatsPage.test.tsx src/components/optimization/VersionTable.test.tsx src/pages/SettingsPage.test.tsx src/mutations/Approval.test.tsx`: PASS, 4 files / 65 tests, 56.92 s.
- `npx vitest run src/mutations/Approval.test.tsx -t 'toast presents the approval headline'`: PASS, 6 passed / 11 skipped, after simplifying the new test lookup to findByLabelText.
- `npm run typecheck`: PASS, exit 0.
- `npm run lint`: PASS, exit 0.
- `npm run build`: PASS, exit 0; Vite reports its existing >500 kB chunk warning.
- Root `git diff --check -- src/SqlHarness.Dashboard/ui`: PASS; only Git LF-to-CRLF informational warnings.
- shadcn skill consulted; `npx shadcn@latest docs button toast` and both official Base UI component documentation pages read.

This is focused verification, not a full-gate or implementation-complete claim. No .NET, npm ci, full Windows gate, Linux gate, browser mutation/API exercise, publication, merge, push, restart or live DB action performed. Parent owns fresh browser controls/Usage/mutation/SSE evidence, one scoped re-review and the final Windows gate. Remaining concern: final visual geometry must be accepted from those fresh captures; source/self-review found no additional issue within this bounded wave.
