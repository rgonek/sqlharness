# Plan 117 whole-study review

Reviewed the uncommitted study against `plans/117-equivalence-localization-study.md`, the SDD constraints, inventory, design, 31 independent case expectations, evaluator, verifier, generated results, decision, scoped verification log, and archived task reviews. Base: `05a414a4`. This is a read-only review of the product tree; no reported suite was rerun.

## Strengths

- `cases.json` holds expected verdicts, locations, and run pairs separately from `evaluate.ps1`; the evaluator reads synthetic source facts and does not read `expected`. `verify.ps1` checks the expected values and projected envelopes, including exact pair identity and whole-item output fallback.
- The decision keeps authoritative equivalence, instability, and existing row/fingerprint ceilings separate from partial diagnostics. It excludes unordered alignment, raw values, historical reconstruction, and additional SQL execution.
- The future file and test map names the capture, artifact, MCP, and dashboard seams. The study labels compact JSON bytes as an estimate and explicitly reserves heap and production serializer proof for later implementation.

## Issues

### Critical

None.

### Important

None found within this offline study scope.

### Minor

1. **Generated fixture recipes can drift** — `plans/117-evidence/evaluate.ps1:31-58`, `plans/117-evidence/design.md:5`. Five large fixtures are selected and sized by case ID while `generatedInput` is only documentation. The decision explicitly discloses this limit, so it does not invalidate the fixed 31-case oracle. Keep the runner described as a fixed fixture projector; if it is reused or extended, derive dimensions from the recipe or assert them against the generator.
2. **Completion bookkeeping is inconsistent** — `plans/117-equivalence-localization-study.md:75-99`, `plans/README.md:32`. The plan is marked RESEARCH COMPLETE, but its task/done boxes remain unchecked and the row remains in the selected-studies queue. Check off evidenced work and move the completed-study row to history during closeout; retain the stated implementation boundary.
3. **Mode wording is broader than the result** — `plans/117-evidence/decision.md:5`, `plans/117-evidence/inventory.md:60`. “Every comparison mode” includes `off` in ordinary reading, while `off` is deliberately unavailable (`eq-off-018`). Say `ordered`, `multiset`, and `set` wherever claiming schema localization across modes.

## Declined to judge

- Production capture allocation, managed heap use, and serializer byte limits: the plan authorizes an offline synthetic study and explicitly leaves these to a later selected implementation.
- Live database behavior, deployment, installed binary, and product tests: no product change or live acceptance is authorized by this study.
- The exact future DTO and numerical caps as production commitments: they are candidate inputs subject to measurement and revision.

## Assessment

**Study verdict: Approved with minor closeout fixes.** The bounded research is complete and supports a separately selected implementation design. The three Minors do not undermine the synthetic oracle or the study-only acceptance boundary; items 2 and 3 should be corrected before the study is integrated or cited as closed. This verdict does not approve a product implementation, live behavior, merge, or push.
