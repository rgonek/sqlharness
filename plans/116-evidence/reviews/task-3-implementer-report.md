# Plan 116 Task 3 report: loaded producer identity decision

## Status and decision

**RESEARCH COMPLETE. Candidate A (capabilities only) is accepted as a separately selected implementation direction; candidates B and C are deferred.** No implementation, new numbered plan, product gate, merge, or push was performed. The implementation contract, exact availability/null behavior, future file/test map, limitations and decision rationale are in `plans/116-evidence/design.md` and `plans/116-evidence/decision.md`.

The decision is tied to study revision `443f468e620e98c2e65fb109040d91ef06c286c5`. The offline fixture uses PowerShell `ConvertTo-Json`, so its UTF-8 byte measurements are non-wire estimates. Its compact capability shape and `pageSize`-driven root fixture do not verify production SDK envelope sizing or actual budget-driven paging. Full-envelope 4,096-byte minimum behavior and real-client acceptance remain future checks. Historical loaded-build identity for Plans 107 and 113 remains unresolved.

## Changes

- Added `plans/116-evidence/decision.md` with explicit A/B/C dispositions, evidence scope, alternatives, future costs/checks, and historical attribution limits.
- Finalized `plans/116-evidence/design.md`, including required-version unavailable behavior (`version`, informational version and MVID all null for that assembly), independently nullable optional facts, and required-versus-optional availability reasons.
- Extended `plans/116-evidence/verify.ps1` to require all three dispositions, the exact revision, non-wire measurement labeling, budget bounds, SDK/client acceptance and historical 107/113 limits.
- Archived prior task 1/2 review reports and implementer reports in `plans/116-evidence/reviews/`; names distinguish initial review, intermediate rereview, final rereview, and implementer report.
- Updated the Plan 116 status/checklist and `plans/README.md` row to RESEARCH COMPLETE. B/C are deferred; the index does not say IMPLEMENTED.

No source, test, script-under-source, MCP contract, or product behavior changed. The study did not inspect user-home state or processes, access a database, launch/restart a server or client, or establish live attribution, token savings, or production wire acceptance.

## Commands and results

Study evaluator:

```text
pwsh -NoProfile -File plans/116-evidence/evaluate.ps1
Exit: 0
Output: Wrote D:\Dev\sqlharness\.worktrees\plan-116\plans\116-evidence\results.json (7 scenarios; non-wire UTF-8 estimates).
```

Study verifier:

```text
pwsh -NoProfile -File plans/116-evidence/verify.ps1
Exit: 0
Output: PASS: seven independent synthetic scenarios; availability, identity stability, provenance, path exclusion, byte-budget declarations, and progress-capable root paging.
```

Tracked diff checks:

```text
git diff --check
Exit: 0
Output: no output

git diff --name-only 443f468e620e98c2e65fb109040d91ef06c286c5 -- src tests scripts
Exit: 0
Output: no output
```

For each untracked file below `plans/116-evidence`, the check command was:

```text
git -c core.autocrlf=false -c core.safecrlf=false diff --no-index --check -- NUL <file>
```

The command returned exit `1` for each file, as expected for a clean no-index comparison against the empty `NUL` file; there was no whitespace-error output. `core.safecrlf=false` suppressed Git's line-ending conversion warning while retaining the required `core.autocrlf=false` setting. The initial pass found one pre-existing blank line at EOF in `inventory.md`; it was removed, and the complete check then passed for all 14 untracked evidence and review files. That final loop exited `0`.

Relative Markdown link check:

```text
$files = @('plans/116-loaded-producer-identity-study.md','plans/README.md') + @(Get-ChildItem -LiteralPath 'plans/116-evidence' -File -Recurse -Filter '*.md' | ForEach-Object { $_.FullName.Substring((Get-Location).Path.Length + 1) }); $bad = [System.Collections.Generic.List[string]]::new(); foreach ($relativeFile in $files) { $fullFile = Join-Path (Get-Location) $relativeFile; $content = Get-Content -LiteralPath $fullFile -Raw; foreach ($match in [regex]::Matches($content, '\[[^\]]*\]\(([^)]+)\)')) { $target = $match.Groups[1].Value.Trim(); if ($target -match '^(https?:|mailto:|#)' ) { continue }; $target = ($target -split '[#?]', 2)[0]; if ([string]::IsNullOrWhiteSpace($target)) { continue }; $decoded = [uri]::UnescapeDataString($target); $resolved = Join-Path (Split-Path -Parent $fullFile) $decoded; if (-not (Test-Path -LiteralPath $resolved)) { $bad.Add("$relativeFile -> $target") } } }; if ($bad.Count) { $bad; exit 1 }; "PASS: $($files.Count) Markdown files scanned; all relative targets resolve."
Exit: 0
Output: PASS: 12 Markdown files scanned; all relative targets resolve.
```

The seven required study files (`inventory.md`, `design.md`, `cases.json`, `evaluate.ps1`, `verify.ps1`, `results.json`, `decision.md`) are present. The final worktree status is limited to modifications of the Plan 116 plan/index and untracked `plans/116-evidence/`. No product gate or tests were run because executable behavior did not change.

## Archived review reports

- `task-1-initial-review.md`, `task-1-final-rereview.md`, `task-1-implementer-report.md`
- `task-2-initial-review.md`, `task-2-rereview-1.md`, `task-2-final-rereview.md`, `task-2-implementer-report.md`

Task 3 and whole-branch review reports are left for the controller to append.
