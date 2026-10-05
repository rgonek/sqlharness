# Plan 035: The offline `regress` verdict from plan 013 exists, starting with the per-variant run count that makes compare artifacts decidable

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- plans/013-regression-contract.md plans/013-regression-implementation.md src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/BenchmarkSummary.cs src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/BenchmarkRunner.cs src/SqlHarness.Cli/SqlHarnessCli.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2 (direction: the most finished design on file)
- **Effort**: M–L (Phase A is S; Phases B–D are M)
- **Risk**: LOW–MED
- **Depends on**: plans/016-restore-green-ci.md. Phase A should land **early**, even if B–D wait, because artifacts written before it stay `inconclusive` forever.
- **Category**: direction
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

Agents and CI users currently read two timing distributions and guess whether a candidate regressed. Plan 013 already produced an accepted **contract** (`plans/013-regression-contract.md`) and a file-level **implementation design** (`plans/013-regression-implementation.md`, both in Polish) for an offline `regress <artifact-id> --policy v1 --json` that returns `pass` / `fail` / `inconclusive` from a saved compare artifact, with no database connection. Nothing blocks it except scheduling. The design shows that today **every** artifact would come out `inconclusive` at gate 1, because a compare report has no per-variant measured-run count. Adding that count (Phase A) is small and starts producing decidable artifacts immediately.

## Current state

**Authority**: `plans/013-regression-contract.md` defines behavior. Where it differs from `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` §4–5, the contract wins (stated at the top of the implementation design). Read both 013 documents in full before starting. They are the specification. This plan only sequences them into verifiable phases and adds gates.

Key facts from the implementation design, verified at `5280f78`:
- Closed set of new files: `src/SqlHarness.Core/RegressionPolicy.cs`, `src/SqlHarness.Core/RegressionDecider.cs`, `src/SqlHarness.Cli/Commands/RegressCommand.cs`, `tests/SqlHarness.Tests/RegressionDeciderTests.cs`, `tests/SqlHarness.Tests/Cli/RegressCommandTests.cs`. No other new files.
- Existing-file edits for the run count: an optional `int? MeasuredRunCount` `init` property with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on `CompareVariantReport` (`src/SqlHarness.Core/Artifacts.cs`), `BenchmarkVariantSummary` (`src/SqlHarness.Core/BenchmarkSummary.cs`, copied by `BenchmarkSummaryProjector.ProjectVariant`) and `ArtifactNamedMetrics` (`src/SqlHarness.Core/ArtifactReader.cs`, copied by `ArtifactReader.Of`). Set it in `BenchmarkReports.CreateVariantReport` (`src/SqlHarness.Core/BenchmarkRunner.cs`) to the `runs.Count` it receives (warm-up repetition 0 excluded). **Never** derive it from report-level `MeasuredRunCount / 2` or `Repetitions`.
- Legacy rule: a missing key deserializes as `null` (not 0); `0` is a stated count.
- Registration: next to `c.AddCommand<ArtifactCommand>("artifact")…` in `SqlHarnessCli.Create` (`src/SqlHarness.Cli/SqlHarnessCli.cs`). `RegressCommand` takes no `ISqlHarnessModule` and uses the same error pattern as `ArtifactCommand` (`ArtifactReadException.ExitCode`; outside I/O errors → 6).
- `--policy` accepts exactly `v1`. Anything else does not call the decider.
- Advertising (`Capabilities.cs`, AGENTS, README) happens **last**, only once the command exists. MCP exposure is explicitly not part of 013.
- The design's `## Testy` section lists the decider test table; `## UNPROVEN` lists what stays unproven offline (median truncation vs raw samples).

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Focused | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Regress|FullyQualifiedName~ArtifactReader|FullyQualifiedName~BenchmarkSummary|FullyQualifiedName~Compare"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: exactly the 5 new files and the existing-file edits named in `plans/013-regression-implementation.md`, plus `SqlHarnessCli.cs` (one registration line), `Capabilities.cs` / `AGENTS.md` / `README.md` in Phase D, and the capabilities and docs-sync pins those changes require.

**Out of scope**: an MCP tool, a thresholds file, changes to `plans/013-*` documents (if you find a contradiction, STOP), and any live database run.

## Git workflow

Branch `feat/plan-035-regress`. One commit series per phase: `feat(035/A): ...`, `feat(035/B): ...`, etc. Phase A may be merged on its own. Do NOT push.

## Steps

### Phase A: per-variant `MeasuredRunCount` (S)

1. RED test (in the existing artifact reader or summary test file) covering the three facts the design requires: a `report.json` with the key absent reads as `null`; a key of `0` reads as `0`; a newly written compare pair carries the count in the report, the summary and the metrics section.
2. Add the property in the three records and set it in `CreateVariantReport`, as specified.

**Verify**: focused tests pass, the full gate passes, and `git diff --stat` touches only the files the design lists for this phase plus tests.

### Phase B: `RegressionPolicy` and `RegressionDecider` (pure)

Implement exactly the contract's first-match gate order (gates 1–4, then "Po bamach"). Translate the design's test table (`## Testy`) into `RegressionDeciderTests.cs` as data-driven `[Theory]` rows, one row per table entry, and keep the row identifiers from the design in the test names or data, so a reviewer can map them. The decider must not perform I/O.

**Verify**: `dotnet test ... --filter "FullyQualifiedName~RegressionDecider"`: every design row is present and passes. The row count equals the design table's row count; state both numbers in the commit message.

### Phase C: `RegressCommand`

Implement `regress <artifact-id> --policy v1 --json` with the legacy/error behavior from the design's `## Legacy i błędy offline`. CLI tests in `RegressCommandTests.cs`, following `tests/SqlHarness.Tests/Cli/ArtifactCommandTests.cs`.

**Verify**: focused tests and the full gate pass.

### Phase D: Advertise

Add `regress` to `Capabilities.cs`, the README command table, AGENTS.md and the skill (if plan 029 landed, `DocsSyncTests` enforces this). Add the CI gate example from the design's `## Przykład bramki CI` to README.

**Verify**: full gate passes. `dotnet run --project src/SqlHarness.Cli -- regress --help` shows the command.

## Test plan

As specified in `plans/013-regression-implementation.md` `## Testy`, plus the Phase A legacy test.

## Done criteria

- [ ] All four phases done, or Phase A done with B–D recorded as TODO in the index
- [ ] `grep -rn "MeasuredRunCount" src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/BenchmarkSummary.cs src/SqlHarness.Core/ArtifactReader.cs` → matches
- [ ] Decider test row count = design table row count
- [ ] Full gate passes; `plans/README.md` row updated

## STOP conditions

- The 013 contract and implementation design disagree, or either disagrees with the code at the cited symbols. Report the exact passages.
- A design test row cannot be expressed without reading `runs.jsonl` or connecting to a database.
- Phase A changes any existing pinned JSON other than by adding the optional key.

## Maintenance notes

- Any future change to metric availability (e.g. plan 021's `unavailable` CPU on non-English SQL Server) must be checked against the decider's gate 2 rules.
- An MCP `sqlharness_regress` tool is a separate catalog decision (13 tools would change the documented "exactly 11").
