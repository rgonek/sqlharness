# Task 2 report — safe synthetic dispatch study

Status: DONE_WITH_CONCERNS

Branch `plan-120`, worktree `C:\Users\rgone\MultiCliProfiles\codex\account-b\worktrees\502b\sqlharness`. Study base and starting HEAD: `f5a218801d56197f3490f6eda3c5358913b92179`; initial worktree clean. All tracked edits are under `plans/120-evidence/`. This report is in the requested ignored SDD workspace.

## Deliverables

- `plans/120-evidence/design.md` freezes the study schema, input-root and exact-hash checks, all-suite preflight plus immediate per-entry recheck, stop/partial-result behavior, target rule, and A/B/C matrix.
- `cases.json` and `evaluate.ps1` exercise 26 named cases with an injected in-memory dispatcher only. `results.json` records 17 synthetic invocations, per-entry outcome metadata and input identity. It records zero database or external-process calls and no suite-equivalence verdict.
- `verify.ps1` independently hard-codes the case inventory and checks exact order/count/identity, fixed and named-case semantics, completed negative evidence, tail stops, all-suite preflight, changed-after-preflight retention, symlink proof mode, no duplicate dispatch/reuse, and the no-connection boundary.
- The fixture generator now identifies the target surface. Ticket B uses explicit fixed typed scalar input so the fixture does not claim the unsupported `parameterSetFile` shape. The intentionally stale Ticket B SQL pin remains and is rejected by full-suite preflight before any dispatch. Ticket B's duplicate-sensitive completed negative remains intact.

## Verification (initial implementation, commit `2bdaaa5e`)

Run from the assigned worktree:

`pwsh -NoProfile -File plans/120-evidence/fixtures/generate.ps1`

Exit 0. Output:

```text
Generated 21 fixture files under C:\Users\rgone\MultiCliProfiles\codex\account-b\worktrees\502b\sqlharness\plans\120-evidence
Ticket B changed-file pin: 8949ef817baf41e8b4b46528ba41e6ed7ea6e3790f1b3aff9e9cb1dbdbaa7f05; current: 6396ff03f0fa635d123aa4301ccd88c77dcbd0beb95131ff2aaeb7f5ca55d8d7
```

`pwsh -NoProfile -File plans/120-evidence/evaluate.ps1`

Exit 0. Output: `Evaluated 24 synthetic cases; dispatch records: 16; database/external-process calls: 0/0`.

`pwsh -NoProfile -File plans/120-evidence/verify.ps1`

Exit 0. Output: `PASS: 24 named cases; dispatch order, outcomes, stop behavior, input semantics, hash/path preflight, and no-connection boundary verified.`

Missing-evidence checks temporarily removed and restored files within the evidence directory. With `results.json` absent, the verifier exited 1 with `Verifier failed: results.json is missing; run evaluate.ps1 first`. With `cases.json` absent, the evaluator exited 1 with a missing-file error. The verifier passed again after restoration.

`git diff --check`

Exit 0. Git emitted only the expected working-copy CRLF-to-LF notices for the two regenerated fixture manifests.

No product build or `just verify` was run: this task changes only study documentation, synthetic fixtures and standalone study scripts. No SQLHarness command, MCP client, database, profile or target was invoked.

## Limitations and concerns

- The OS denied creation of a test symlink. `results.json` explicitly records `os-link-unavailable-simulated-rejection` and `symlink-reparse-escape-simulated`; no executed filesystem symlink proof is claimed. The simulated rejection is verified.
- The study accepts only fixed scalar inputs and MCP named parameter-set files. CLI named sets, matrix variants, `parameterSetFile`, TVP/list, and mixed inputs are rejected or deferred. The parser covers generated ordinary JSON fixtures and does not claim full current reader grammar parity (BOM/comments/trailing commas) or Core type resolution; a production design must reuse existing readers/resolvers.
- Mock feasibility and synthetic placement counts cannot establish recurring demand, operator target permission, database snapshot identity, or real execution/artifact identifiers.

## Self-review

Reviewed the full study diff and generated results. The stale Ticket B candidate is found at suite preflight, before entry 1; input hashes are rechecked immediately before each later dispatch; the change-after-preflight case retains entry 1 and stops before entry 2; error/timeout/cancellation/unknown each stop before the explicit third tail; completed `different` and `regression` results remain completed outcomes. The evaluator has no process-launch, command-evaluation, MCP, or DB call path. No full parameter grammar parity or filesystem symlink execution is claimed.

## Fix round 1 — review findings

The review findings were addressed within `plans/120-evidence/`:

- The evaluator now checks the copied manifest's raw file length before calling `ConvertFrom-Json`. The oversized case appends whitespace to the source file; results record `manifestBytes=1051192` against the 1048576-byte cap.
- `compareNullability` must deserialize as a JSON boolean. The new malformed-type case supplies the string `"false"` and is rejected with zero dispatches.
- Error, timeout, cancellation, and unknown injected outcomes now record `comparisonStatus=not-run`, `equivalence=not-evaluated`, `performance=not-measured`, and coverage 0/3. The independent verifier also asserts that the earlier completed entry retains its completed comparison and 3/3 coverage.
- A new mock dispatch case covers Ticket B's fixed `boundaryDate` value; the verifier independently checks both Ticket B manifest entries for `boundaryDate` / `date` / `2024-02-29`.
- The JSON example is balanced and is parsed as part of the schema-example check.

Commands and exact output from the fix-round worktree:

`pwsh -NoProfile -File plans/120-evidence/evaluate.ps1`

Exit 0. Output: `Evaluated 26 synthetic cases; dispatch records: 17; database/external-process calls: 0/0`.

`pwsh -NoProfile -File plans/120-evidence/verify.ps1`

Exit 0. Output: `PASS: 26 named cases; dispatch order, outcomes, stop behavior, input semantics, hash/path preflight, and no-connection boundary verified.`

Schema and fixture JSON parse check:

```powershell
$design = Get-Content plans/120-evidence/design.md -Raw; $match = [regex]::Match($design, '(?s)```json\s*(.*?)\s*```'); if (-not $match.Success) { throw 'schema JSON example not found' }; $example = ConvertFrom-Json -InputObject $match.Groups[1].Value -ErrorAction Stop; if ($example.input.kind -ne 'parameterSetFiles') { throw 'parsed schema example has unexpected input kind' }; $null = Get-Content plans/120-evidence/cases.json -Raw | ConvertFrom-Json -ErrorAction Stop; $null = Get-Content plans/120-evidence/results.json -Raw | ConvertFrom-Json -ErrorAction Stop; $null = Get-Content plans/120-evidence/fixtures/ticket-b/experiment-suite.json -Raw | ConvertFrom-Json -ErrorAction Stop; Write-Output 'PASS: candidate schema example, cases, results, and Ticket B manifest parse as JSON.'
```

Exit 0. Output: `PASS: candidate schema example, cases, results, and Ticket B manifest parse as JSON.`

Focused outcome evidence:

```text
error-entry-2 error not-run not-evaluated not-measured coverage=0/3
timeout-entry-2 timeout not-run not-evaluated not-measured coverage=0/3
cancellation-entry-2 cancelled not-run not-evaluated not-measured coverage=0/3
unknown-completion-entry-2 unknown not-run not-evaluated not-measured coverage=0/3
invalid-manifest-size raw-bytes=1051192 error=manifest-size-limit
malformed-nullability-type error=invalid-nullability-policy-type dispatches=0
ticket-b-boundary-date boundaryDate/date=2024-02-29
```

`git diff --check` exited 0. Git emitted the expected working-copy line-ending notice for `plans/120-evidence/results.json`; there were no whitespace errors.
