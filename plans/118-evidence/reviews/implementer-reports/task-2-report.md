# Plan 118 Task 2 report: condition difference explanation study

## Status

Offline synthetic study completed. No product source, tests, shared scripts, journal data, database, user-home, settings, installation, or runtime policy was changed or inspected. The study is not product implementation, runtime acceptance, or authorization to capture additional metadata.

## Deliverables

- `plans/118-evidence/design.md` freezes the candidate explanation schema, values, availability/gap semantics, UI wording, limits, deterministic output order, and later dashboard route/component map.
- `plans/118-evidence/cases.json` contains 27 named synthetic scenarios. It includes all 15 scenarios from the task brief, retained setup-text change, unknown-versus-empty, genuine absent-property/collection states, an informational-only unexplained measurement mismatch, a paired-case parameter difference with an opaque matching key, retained typed NULL projection, and null-versus-empty/nonempty collection regressions.
- `plans/118-evidence/evaluate.ps1` evaluates retained synthetic records only. It takes the existing `basis` as fixture input and copies it to output without recomputing it.
- `plans/118-evidence/verify.ps1` is an independent, hard-coded oracle for all scenario availabilities, exact difference identities/kinds/relations/before/after values, stable gaps, omissions, basis preservation, hidden-value protection, case-key non-disclosure, and UTF-8 bounds. It checks the exact sorted retained identity set for byte-budget overflow.
- `plans/118-evidence/results.json` records the serializer, evaluator revision, per-explanation UTF-8 sizes, aggregate/max sizes, caps, and known limits. It was regenerated and verified with evaluator code at `e2b76bc0999270da1cff9a87a7cd0262552d69f2`; the later closeout commit changes only `results.json` to record this executable-content revision.

## Evidence and checks

Commands run from `D:\Dev\sqlharness\.worktrees\plan-118`:

1. `pwsh -NoProfile -File plans/118-evidence/evaluate.ps1` — exit 0; evaluated 17 scenarios and wrote `results.json`.
2. `pwsh -NoProfile -File plans/118-evidence/verify.ps1` — exit 0; `VERIFIED: 17 named scenarios; verdict preservation, oracle categories, value protection, pairing, omissions, item limits, and UTF-8 limits passed.`
3. `git diff --check` — exit 0.
4. Inspected the current UI/API route boundary in `OptimizationSection.tsx`, `queries.ts`, `types.ts`, `ComparisonPanel.tsx`, `optimizationMarkdown.ts`, and the corresponding dashboard routes. The session and agent-session compare routes accept explicit subject/database/group/scope/version selectors; the response includes existing `basis` and `beforeAction`/`afterAction` operation references. The operation route is separately anchored by operation ID. No source files were modified.

All 22 explanations fit the 8,192 UTF-8 byte limit. Aggregate explanation JSON was 7,256 bytes; maximum single explanation was 3,709 bytes. The multibyte overflow case retained exactly `@p00` through `@p31`, truthfully reported 16 omitted differences and `budget-exceeded`, and stayed under the byte cap after dropping value displays. Basis equality was asserted against each fixture's original object.

## Self-review and concerns

- A setup-hash mismatch alone produces `conditions-unavailable`; no hash or fingerprint is emitted as a value. When setup text is retained on both sides, a known text difference can appear alongside an unavailable gap because other combined fingerprint inputs may also differ.
- Hidden parameter values, opaque case-pairing keys, and cross-journal fingerprints never appear in explanation values or identities. Ambiguous pairings fail closed. Typed NULL, missing property, unknown, omitted, and empty collection remain separate states.
- Repeat, timeout, and comparison-mode changes are informational and leave the supplied basis unchanged. They do not establish measurement-condition incompatibility.
- The current group selector can be used by a later implementation only by attaching to the exact comparison result and its action references. The study does not add fields or API selectors.
- The evaluator is intentionally a candidate model, not a production implementation. It does not establish actual journal recurrence, real dashboard response-envelope size, browser rendering, cross-platform PowerShell sort collation, or exact production serializer behavior. The 8 KiB study cap applies to `conditionExplanation` alone.
- Regular and measurement fingerprints are separate fixture fields. A comparison-mode-only fixture changes only its regular fingerprint and retains the same measurement fingerprint, matching the source inventory. A separate repeat-only fixture has a deliberately unexplained measurement-key mismatch; the informational repeat change does not suppress its gap.
- Current optimization selectors pick versions within one conditions group/scope. The operation route is anchored to one compare operation and compares its baseline/candidate SQL variants. Neither current route exposes a pair of operation condition records across groups, so the report defers cross-group explanation on both paths pending an exact-pair interface.
- The pre-existing untracked `plans/118-evidence/reviews/` directory was preserved and excluded from Task 2 staging/commit.

## Change boundary

Only `design.md`, `cases.json`, `evaluate.ps1`, `verify.ps1`, and `results.json` under `plans/118-evidence/` are intended for the Task 2 commit. This report remains at the requested `.superpowers/sdd/118-condition-difference-explanations-study/task-2-report.md` path and is not part of that commit.

Commits: implementation/evidence `e2b76bc0999270da1cff9a87a7cd0262552d69f2`; results-only revision closeout `0697b2c3bf444a5d4bb88055814668e1d571f695`.

## Task 2 review fix round 1

### Findings addressed

- An unexplained `measurementFingerprint` mismatch now yields `conditions-unavailable` unless at least one known measurement-condition difference explains it. Informational-only differences do not suppress the gap. Regular and measurement fingerprints are distinct fixture fields: the comparison-mode-only case changes the regular fingerprint and retains the measurement fingerprint, as `inventory.md` specifies; a separate repeat-only fixture tests the unexplained mismatch rule.
- The evaluator checks JSON property presence for condition models, scalar values, and parameter/case collections. A genuinely absent property or collection produces a gap and is never treated as empty or equal. `typed NULL versus absent value` and `omitted long fields` now use actual missing JSON properties, with omission paths retained in the latter. A separate present typed-NULL versus retained-value case verifies `before: null` is serialized as present.
- Paired case parameter paths now use `cases.parameters.<identity>...`; opaque pair keys stay internal. A new paired case with `opaque-journal-key-DO-NOT-LEAK` verifies field/value non-disclosure while asserting exact case IDs and parameter before/after values.
- The independent oracle now compares exact before/after display values and null/presence semantics for every difference. Overflow asserts the exact sorted set `@p00` through `@p31`, the exact 16 omissions, and dropped values.
- The route map now says current group selection is limited to versions inside one group/scope. The operation route is anchored to one compare operation and compares its baseline/candidate SQL variants. Neither route exposes cross-group operation condition records, so cross-group explanation on both paths is deferred pending an exact-pair interface.

### Fix round checks

Commands run from `D:\Dev\sqlharness\.worktrees\plan-118`:

1. `pwsh -NoProfile -File plans/118-evidence/evaluate.ps1` — exit 0; `cases=22`.
2. `pwsh -NoProfile -File plans/118-evidence/verify.ps1` — exit 0; `VERIFIED: 22 named scenarios; exact oracle values, basis preservation, absent-property states, fingerprint semantics, pairing non-disclosure, omissions, and UTF-8 limits passed.`
3. `git diff --check` — exit 0.
4. Scoped PowerShell trailing-whitespace scan over the five changed evidence files — clean. The design contains no Markdown links requiring target resolution; route references were checked against current `DashboardServer.cs`, `OptimizationComparer.cs`, `OptimizationSection.tsx`, `queries.ts`, and `types.ts`.

Fix round implementation commit: `74bcb397c93f8f50ac4ce6458c9b7ac93d2c9034`; results-only revision closeout: `2d99dd75e999c7ffb3cc016b4210ce736f297d0d`. The results file records the executable-content revision, not its own closeout commit. At evaluation revision `74bcb397c93f8f50ac4ce6458c9b7ac93d2c9034`, all 22 cases passed; aggregate explanation UTF-8 was 7,256 bytes, with 3,709 maximum per explanation. No product/source/test files were changed. The pre-existing untracked review directory was not staged.

## Task 2 review fix round 2

### Finding addressed

Present JSON `null` collections are now distinct from empty arrays and unavailable collections. The evaluator validates each parameter/case collection as a non-null JSON array before inspecting its contents. It applies the same rule to matched case parameter collections. If a collection is missing, null, or not an array, it emits `field-omitted` and makes no added/removed claims from that collection.

Five regressions cover null parameters versus empty and nonempty, null cases versus nonempty, and null paired-case parameters versus empty and nonempty. The independent oracle expects a gap and an empty difference list for each, so no unsupported addition/removal can pass.

### Round 2 checks

Commands run from `D:\Dev\sqlharness\.worktrees\plan-118`:

1. `pwsh -NoProfile -File plans/118-evidence/evaluate.ps1` — exit 0; `cases=27`.
2. `pwsh -NoProfile -File plans/118-evidence/verify.ps1` — exit 0; `VERIFIED: 27 named scenarios; exact oracle values, basis preservation, absent-property states, fingerprint semantics, pairing non-disclosure, omissions, and UTF-8 limits passed.`
3. `git diff --check` — exit 0.
4. Scoped trailing-whitespace scan over the five changed evidence files — clean. No Markdown hyperlinks were added in this round.

Round 2 implementation commit: `5ebe862f52abf308e7f33e07178f5f0cf50cf485`; results-only closeout commit: `e1e6cd0884978ffe80a5e9a0027349550082ca51`. Results were rerun and verified at the implementation revision and record it. Results contain 27 cases, 7,731 aggregate explanation UTF-8 bytes, and 3,709 maximum per explanation. No product/source/test files were changed, and the existing review directory was not staged.
