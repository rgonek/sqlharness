# Plan 035: The offline `regress` verdict from plan 013 exists, starting with the per-variant run count that makes compare artifacts decidable

> **SUPERSEDED 2026-10-11** by [051](051-compare-regression-verdict.md): the verdict is computed inside `compare` from raw samples, so the offline `regress` command, `MeasuredRunCount` and legacy-producer inference are not needed. Kept for history; do not execute.

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 729d88f..HEAD -- plans/013-regression-contract.md plans/013-regression-implementation.md src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/BenchmarkSummary.cs src/SqlHarness.Core/ArtifactReader.cs src/SqlHarness.Core/BenchmarkRunner.cs src/SqlHarness.Cli/SqlHarnessCli.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; adapt mechanical details to the authorized contract. Stop only for an incompatible product decision, unmet proof or an action outside scope.

## Status

- **Priority**: P2 (direction: the most finished design on file)
- **Effort**: M–L (Phase A is S; Phases B–D are M)
- **Risk**: LOW–MED
- **Depends on**: plans/016-restore-green-ci.md and completed plan 046. Execute all steps as one plan so default equivalence reflects observed data and ignores nullability-only differences; legacy without a provable producer invariant remains inconclusive.
- **Category**: direction
- **Planned at**: commit `729d88f`, revised 2026-10-11

## Why this matters

Agents and CI users currently read two timing distributions and guess whether a candidate regressed. Plan 013 already produced an accepted **contract** (`plans/013-regression-contract.md`) and a file-level **implementation design** (`plans/013-regression-implementation.md`, both in Polish) for an offline `regress <artifact-id> --policy v1 --json` that returns `pass` / `fail` / `inconclusive` from a saved compare artifact, with no database connection. The 2026-10-11 revision makes latency independently decisive and costs diagnostic; implement the revised contract. The current manifest provides no producer identity proving the legacy equal-count invariant; missing per-variant counts therefore remain inconclusive unless a recognized producer/version supplies that proof. Adding that count (Phase A) is small and starts producing decidable artifacts immediately.

## Current state

**Authority**: `plans/013-regression-contract.md` defines behavior. Where it differs from `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` §4–5, the contract wins (stated at the top of the implementation design). Read both 013 documents in full before starting. They are the specification. This plan only sequences them into verifiable phases and adds gates.

Key facts from the implementation design, revised at `729d88f`:
- Closed set of new files: `src/SqlHarness.Core/RegressionPolicy.cs`, `src/SqlHarness.Core/RegressionDecider.cs`, `src/SqlHarness.Cli/Commands/RegressCommand.cs`, `tests/SqlHarness.Tests/RegressionDeciderTests.cs`, `tests/SqlHarness.Tests/Cli/RegressCommandTests.cs`. No other new files.
- Existing-file edits for the run count: an optional `int? MeasuredRunCount` `init` property with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on `CompareVariantReport` (`src/SqlHarness.Core/Artifacts.cs`), `BenchmarkVariantSummary` (`src/SqlHarness.Core/BenchmarkSummary.cs`, copied by `BenchmarkSummaryProjector.ProjectVariant`) and `ArtifactNamedMetrics` (`src/SqlHarness.Core/ArtifactReader.cs`, copied by `ArtifactReader.Of`). Set it in `BenchmarkReports.CreateVariantReport` (`src/SqlHarness.Core/BenchmarkRunner.cs`) to the `runs.Count` it receives (warm-up repetition 0 excluded). Do not infer it from total /2 or Repetitions alone. The contract permits only a recognized successful producer/schema with a tested equal-count invariant, consistency checks and explicit inferred-known-producer provenance. Current manifest v1 does not provide this proof.
- Legacy rule: a missing key deserializes as `null` (not 0); `0` is a stated count.
- Registration: next to `c.AddCommand<ArtifactCommand>("artifact")…` in `SqlHarnessCli.Create` (`src/SqlHarness.Cli/SqlHarnessCli.cs`). `RegressCommand` takes no `ISqlHarnessModule` and uses the same error pattern as `ArtifactCommand` (`ArtifactReadException.ExitCode`; outside I/O errors → 6).
- `--policy` accepts exactly `v1`. Anything else does not call the decider.
- Advertising (`Capabilities.cs`, AGENTS, README) happens **last**, only once the command exists. MCP exposure is explicitly not part of 013.
- The design's `## Testy` section lists the decider test table; `## Granice dowodu i utrzymanie` lists what stays unproven offline (median truncation vs raw samples).

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Focused | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Regress|FullyQualifiedName~ArtifactReader|FullyQualifiedName~BenchmarkSummary|FullyQualifiedName~Compare"` | all pass |
| Windows gate | `pwsh -NoProfile -File scripts/verify.ps1` | exit0 and `verify: OK` |
| Linux gate | `pwsh -NoProfile -File scripts/verify-linux.ps1` | exit0 and `verify-linux: OK`; complete four-stage success inside ext4 |

## Scope

**In scope**: exactly the 5 new files and the existing-file edits named in `plans/013-regression-implementation.md`, plus `SqlHarnessCli.cs` (one registration line), `Capabilities.cs` / `AGENTS.md` / `README.md` in Phase D, and the capabilities and docs-sync pins those changes require.

**Out of scope**: an MCP tool, a thresholds file, unrelated changes to `plans/013-*` documents (adapt references and mechanics after inspecting drift; stop for a genuinely incompatible normative requirement), and any live database run.

## Git workflow

Branch `feat/plan-035-regress`. Execute the complete plan after 046 in one delivery. Phases A–D below are internal step ordering, not separately scheduled or merged partial tasks. Keep logical commits and final verification for the whole result. Do NOT push.

## Steps

### Phase A: per-variant `MeasuredRunCount` (S)

1. RED test (in the existing artifact reader or summary test file) covering the three facts the design requires: a `report.json` with the key absent reads as `null`; a key of `0` reads as `0`; a newly written compare pair carries the count in the report, the summary and the metrics section.
2. Add the property in the three records and set it in `CreateVariantReport`, as specified.

**Verify**: focused tests pass, both platform gates pass, and `git diff --stat` touches only the files the design lists for this phase plus tests.

### Phase B: `RegressionPolicy` and `RegressionDecider` (pure)

Implement the revised contract gate order (gates 1–4, then zero/threshold decisions). Stable elapsed regression is sufficient for fail; reads/CPU are optional diagnostics. Preserve equivalence and spread gates. Use plan046 default equivalence: nullability-only differences must not cause default inconclusive; explicit strict comparison remains authoritative. Translate the design's test table (`## Testy`) into `RegressionDeciderTests.cs` as data-driven `[Theory]` rows, coverage for every table entry, and keep the row identifiers from the design in the test names or data, so a reviewer can map them. The decider must not perform I/O.

**Verify**: `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~RegressionDeciderTests"`: every design row is present and passes. Every acceptance case is covered; one table row may expand to multiple meaningful cases. Include the latency-only, unavailable-cost, zero-boundary and legacy-proof cases. Do not assert a brittle equality between test count and Markdown row count.

### Phase C: `RegressCommand`

Implement `regress <artifact-id> --policy v1 --json` with the legacy/error behavior from the design's `## Komenda i wynik`. CLI tests in `RegressCommandTests.cs`, following `tests/SqlHarness.Tests/Cli/ArtifactCommandTests.cs`.

**Verify**: focused tests and both platform gates pass.

### Phase D: Advertise

Add `regress` to `Capabilities.cs`, the README command table, AGENTS.md and the skill (if plan 029 landed, `DocsSyncTests` enforces this). Add the CI gate example from the design's `## Przykład CI` to README.

**Verify**: both platform gates pass. `dotnet run --project src/SqlHarness.Cli -- regress --help` shows the command.

## Test plan

As specified in `plans/013-regression-implementation.md` `## Testy`, plus Phase A projection tests and default/strict nullability integration after plan046.

## Done criteria

- [ ] All four phases done, or Phase A done with B–D recorded as TODO in the index
- [ ] `rg -n "MeasuredRunCount" src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/BenchmarkSummary.cs src/SqlHarness.Core/ArtifactReader.cs` → matches
- [ ] Every acceptance case in the design has coverage, including 100 → 1000 ms at fixed/unavailable costs, 0→5/6 boundaries, strict versus default equivalence, provenance and unsupported legacy
- [ ] Both Windows and ext4 Linux gates pass; `plans/README.md` row updated

## STOP conditions

- A genuine incompatible behavioral requirement remains after inspecting and updating mechanical drift against the revised 013 contract; report exact passages. A changed source file alone is not a STOP.
- A design test row cannot be expressed without reading `runs.jsonl` or connecting to a database.
- A legacy inference cannot prove the producer/version invariant: keep that artifact inconclusive and continue recorded-count implementation; do not generalize /2. An existing JSON shape needs a non-additive change outside the stated scope.

## Maintenance notes

- Any future change to metric availability (e.g. plan 021's `unavailable` CPU on non-English SQL Server) must be checked against the decider's gate 2 rules.
- An MCP regress tool is a separate catalog decision that would change the documented exactly 11 tools.
