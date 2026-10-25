# Plan 118 Task 1 report

Status: DONE

## Changes

- Added `plans/118-evidence/inventory.md`, a source-grounded inventory of retained condition fields, operation and case fingerprints, measurement-fingerprint pairing, sensitivity and omission behavior, dashboard comparison identities, and current generic explanation branches.
- Included short excerpts from the current payload builders and linked each finding to current source locations.
- No product source, tests, scripts, journal, settings, database, or user-home data was changed or inspected live.

## Source checks and scoped documentation verification

- Baseline checked: `7c1b2fbf282d8ee3aa1e970759053156a21550a9`.
- Reopened the cited sections in `ConditionsModelBuilder.cs`, `ActivityJournal.cs`, `ConditionsModel.cs`, `OptimizationSummaryBuilder.cs`, `OptimizationComparer.cs`, `JournalReader.cs`, `ComparisonPanel.tsx`, `optimizationMarkdown.ts`, and `ConditionsList.tsx`.
- `Select-String -Path plans/118-evidence/inventory.md -Pattern 'MeasurementFingerprint|Repeat|Timeout|CompareNullability|identity|omitted'`: all six requested topics are present with evidence-backed dispositions.
- Markdown link check: 14 links checked; all targets exist.
- `git diff --check`: passed for tracked diff (none existed before staging). `git diff --no-index --check -- NUL plans/118-evidence/inventory.md`: no whitespace errors; Git emitted only its standard LF-to-CRLF working-copy notice.
- Product tests and full `just verify` were not run: this is a read-only product study and the verification contract scopes docs-only work to documentation checks.

## Self-review

- Confirmed `Repeat`, `TimeoutSeconds`, and `CompareNullability` are retained in `ConditionsExecution` but absent from both regular and measurement fingerprint payloads.
- Confirmed regular fingerprints include comparison mode and measurement fingerprints omit it; case pairing uses measurement fingerprint with case-ID fallback.
- Confirmed `conditions differ` has multiple fail-closed causes and does not name the differing field; the current UI and copied Markdown share generic wording.
- Kept unknown, omitted, typed NULL, absent JSON property, and empty collection distinct. Fingerprints and SQL hashes are described only as opaque identities.
- The inventory describes observed current behavior and does not recommend collecting additional data or changing comparison policy.

## Concerns / limits

- Product behavior was inspected at the assigned worktree revision only. No runtime journal or live database was examined, so this report makes no claim about any particular operator's historical measurements.
- Linux, deployment, and live acceptance are outside this study and remain unverified.

## Files

- `plans/118-evidence/inventory.md`
- `.superpowers/sdd/118-condition-difference-explanations-study/task-1-report.md`

## Review fix round 1

Applied all three Important findings from `plans/118-evidence/reviews/task-1-review.md`:

- Added retained-field dispositions for operation, warmup, session policy, setup binding, and materialized list/TVP metadata and contents, including fingerprint, sensitivity, omission, and comparison use.
- Added exact short excerpts from the regular and measurement fingerprint payload builders, and described per-case counterparts.
- Recorded that `ToCondition` stores typed null as JSON null while `ConditionsList.parameterValue` maps both null and undefined/missing to the same `(value not stored)` display. Clarified that the label cannot establish absence.

Reopened source used by these additions: `ConditionsModelBuilder.cs` lines 54, 80-100, 124-191, 195-197; `ConditionsModel.cs` lines 24-32; `ActivityJournal.cs` lines 723-727, 760-787; and `ConditionsList.tsx` lines 4-5.

Exact scoped checks and output:

1. Command:

   ```powershell
   Select-String -Path 'plans/118-evidence/inventory.md' -Pattern 'MeasurementFingerprint|Repeat|Timeout|CompareNullability|identity|omitted' | ForEach-Object { '{0}:{1}' -f $_.LineNumber, $_.Matches.Value }
   ```

   Output included matches for all six alternatives: `MeasurementFingerprint`, `Repeat`, `Timeout`, `CompareNullability`, `identity`, and `omitted` (26 matching lines).

2. Command:

   ```powershell
   $doc = Resolve-Path 'plans/118-evidence/inventory.md'; $body = Get-Content $doc -Raw; $links = [regex]::Matches($body, '\[[^\]]+\]\(([^)]+)\)'); $broken = @(); foreach ($link in $links) { $target = ($link.Groups[1].Value -split '#')[0]; if ($target -and -not (Test-Path (Join-Path (Split-Path $doc) $target))) { $broken += $target } }; "Links checked: $($links.Count)"; "Broken links: $($broken.Count)"; if ($broken.Count) { $broken; exit 1 }; git diff --check; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; 'git diff --check: passed'
   ```

   Output:

   ```text
   Links checked: 21
   Broken links: 0
   git diff --check: passed
   ```

   Git printed its standard LF-to-CRLF working-copy notice for `inventory.md`; there were no whitespace errors. Product tests and `just verify` remain unrun because this is a documentation-only evidence correction.
