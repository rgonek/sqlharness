# Plan 119: Measure large-cell collection costs and decide a bounded streaming design

> **For agentic workers:** Use `superpowers:executing-plans`, or delegated execution when selected. This is a synthetic offline research spike. Production source/tests and provider interfaces remain read-only. A negative or deferred decision is a valid complete outcome.
>
> **Drift check:** `git diff --stat 5f7342a7..HEAD -- src/SqlHarness.Core/CanonicalResults.cs src/SqlHarness.Core/QueryResultCollector.cs src/SqlHarness.Core/BenchmarkCollector.cs src/SqlHarness.Core/BenchmarkRunner.cs src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/Postgres tests/SqlHarness.Tests/CanonicalResultsTests.cs`
> Reconcile compatible drift; stop if exact canonical-byte preservation or the no-live/no-product boundary cannot be maintained.

## Status

- **Status:** TODO; research spike only; no product implementation, live DB, install, merge or push.
- **Priority:** P3; selected direction proposal 4; demand is not established.
- **Effort / risk / confidence:** L / HIGH / HIGH implementation-gap evidence, MED priority confidence.
- **Depends on:** none of 116-120; preserves completed Plan 006 resource decisions.
- **Planned at:** `5f7342a7f5d48e03268491059949d1edfc24c65c`, 2026-10-25.
- **Goal:** Quantify large scalar allocation behavior and decide whether a compatible bounded reader/canonicalization path warrants implementation.
- **Architecture:** Compare existing whole-value and chunk-helper paths with a test-only bounded-source prototype. Preserve exact scalar and whole-result canonical bytes, not merely a digest of substituted row/cell digests.
- **Tech stack:** .NET 10 standalone C# spike, existing canonical codec, process/allocation counters, PowerShell runner; no new package dependency.
- **Spec:** Measurement protocol and gates below; output `119-evidence/design.md` and `decision.md`.

## Execution setup

Use an isolated `plan-119` checkout after inspecting `git status --short` and `git worktree list --porcelain`. Before changes run `$studyStart = (git rev-parse HEAD).Trim()` and record the revision/initial status. Preserve unrelated edits; do not automatically commit/merge/push. Follow `docs/agents/verification.md`; the standalone spike requires its stated build/experiment checks, not a product-wide gate. Missing evidence and invalid measurements must fail scripts with a nonzero exit code.

## Why this matters

Presentation limits do not bound the size of a value read through `GetValue`. Very large text/binary cells can allocate proportionally to the full value before their output is clipped. The repository already names an unfinished chunked path, but helper-level hash equivalence does not prove bounded-memory provider reads or compatible whole-result encoding.

## Current state and conventions

- `src/SqlHarness.Core/QueryResultCollector.cs:23-29` explicitly documents full materialization; line 111 calls `reader.GetValue`.
- `src/SqlHarness.Core/SqlExecution.cs:15` defines `ISqlReader`, whose value method is `object GetValue(int ordinal)`; it has no chunk reader contract. Trace actual SQL Server and Npgsql adapters separately.
- `src/SqlHarness.Core/BenchmarkCollector.cs:46-53` and the measurement collector in `BenchmarkRunner.cs` normalize complete row values before canonical accumulation.
- Existing helper in `src/SqlHarness.Core/CanonicalResults.cs:272`:

```csharp
internal static class ChunkedCanonicalCellHash
{
    public static byte[] HashString(IReadOnlyList<string> chunks)
```

- `HashString` first scans chunks to count UTF-8 length, then emits escaped bytes. `HashBytes` likewise accepts a retained chunk list. Retaining all chunks is still O(cell size). Character count alone does not supply the canonical UTF-8 length.
- `CanonicalResultAccumulator` at line 403 writes canonical event JSON. Replacing an encoded scalar with its SHA-256 changes the whole-result hash; helper output cannot simply be spliced into that event stream as a value.
- `tests/SqlHarness.Tests/CanonicalResultsTests.cs:368-401` provides split-surrogate, lone-surrogate, escaping, empty chunk and base64-boundary compatibility examples. Preserve them as independent oracles.
- `Directory.Build.props` pins net10.0/nullability/warnings as errors. Core uses a package reference to `Microsoft.SqlServer.Types`; friend access includes assembly name `SqlHarness.Tests`.

## Global constraints and review focus

- Synthetic data only; do not read user SQL/results/profiles or connect to any DB. No provider package migration or changes to production `ISqlReader`/canonical output.
- Existing result row/fingerprint limits, complete-read behavior, cancellation and sensitive-storage rules stay intact. No row/value truncation to achieve a memory result.
- Measure fixture generation and collection separately. A preallocated input string outside the timed region is not evidence that production collection allocates nothing.
- Hash equality covers empty/NULL distinctions, Unicode split/lone surrogates, UTF-8 length framing, JSON escaping and base64 carry. Scalar equality alone is insufficient for whole-result identity.
- Report wall time, allocated bytes and sampled peak memory separately. An allocation total or sampled peak is not a universal constant-memory proof.
- Temporary synthetic spool files must be bounded, scoped under this evidence directory and cleaned on cancellation/failure. A future real-data spool requires a separately reviewed privacy/lifecycle policy.

## Scope and permitted spike setup

**Only writable:** this plan/index row and `plans/119-evidence/`: `inventory.md`, `design.md`, `decision.md`, `run.ps1`, `verify.ps1`, `measurements.json`, `cases.json`, `spike/Spike.csproj`, `spike/Program.cs`, additional spike-only C# files and logs. Build output may use standard ignored `bin/obj` directories only. Do not edit the solution, central packages or existing tests.

Create a standalone console project with `ProjectReference` to Core and `AssemblyName` `SqlHarness.Tests` solely to use existing friend access. Do not load the real test assembly or add this project to the solution; record this deliberate test-only arrangement. Reuse central package versions; no new packages. The project may call existing internal codec/accumulator/collector APIs through synthetic fake readers/sessions. Any candidate chunk interface lives only in the spike. If that setup cannot compile without product edits, stop and report; do not modify friend access.

## Tasks

### Task 1: Trace every materialization and length-framing boundary

- [ ] Write `inventory.md`: query/watch/snapshot, compare, measure, raw footprint, canonical result and provider reader paths. Record which paths hold values, chunk lists, row hashes and full result data. Name SQL Server-first applicability and explicit PostgreSQL limitations.
- [ ] Compare options in `design.md`: leave as-is; stream binary with reliable byte-length evidence; bounded disk spool/two passes for text; provider-specific length/read support. Do not assert an API supplies UTF-8 length without source/documentation proof. External documentation, if needed, must be primary and cited with retrieval date.
- [ ] Verify: `Select-String -Path plans/119-evidence/inventory.md -Pattern 'GetValue|UTF-8|query|compare|measure|PostgreSQL'` -> every topic has a concrete source reference and applicability row.

### Task 2: Build a deterministic allocation and hash experiment

- [ ] Build the standalone project above. Keep original Core files linked/referenced unchanged; do not copy and silently alter the baseline codec.
- [ ] Fixtures: ASCII, escape-heavy text, Polish/multibyte text, paired/lone surrogates, binary; NULL and empty; sizes 1, 8 and 32 MiB of source payload (also record UTF-16/UTF-8/encoded lengths). Use deterministic generators and lazy synthetic readers that can materialize during the measured region. Exclude provider/database claims.
- [ ] Measure three paths separately: actual existing whole-value collection; existing retained-chunk helper; candidate lazy chunk consumption with at most 64 KiB working buffers plus an explicitly counted bounded spool if needed. The candidate must reproduce both scalar encoding and one-column whole-result event encoding. Do not inject a scalar hash instead of canonical bytes.
- [ ] For large cases run each scenario in a fresh child process via `dotnet run --project plans/119-evidence/spike/Spike.csproj -c Release -- <case-id> <path>`. A preliminary warm-up is excluded; run five measured samples, retain each and its median. `run.ps1` must pass structured argument lists, never `Invoke-Expression` or concatenated executable commands. No SQLHarness executable is launched.
- [ ] Record runtime/OS/revision, fixture identity, source creation timing/allocation, collection allocation, wall time, sampled peak working set/private bytes, max simultaneously retained payload bytes, spool bytes, digests, cancellation/disposal outcome. Unsupported counters are unavailable, not zero. Bound any synthetic spool at 64 MiB and make oversize rejection explicit.
- [ ] Verify build: `dotnet build plans/119-evidence/spike/Spike.csproj -c Release -warnaserror` -> exit 0. Verify run: `pwsh -NoProfile -File plans/119-evidence/run.ps1` -> exit 0 and complete `measurements.json`; test output is only synthetic metadata/digests, no large payload dump.

### Task 3: Test boundary behavior and make a go/no-go decision

- [ ] `verify.ps1` independently asserts hash equality for every size/type/path, small exhaustive split cases (including binary splits at 1/2/3-byte boundaries), full-result identity, cancellation before/during read, reader disposal, injected spool/read failure, oversize rejection and no leftover spool. Fail the experiment for missing cases or mismatched hashes; do not average them away.
- [ ] For a bounded candidate, instrument maximum retained source/chunk payload separately from output/fingerprint state. Require that metric <=256 KiB and unchanged in order of growth across 1/8/32 MiB cells; document other measured memory, spill and latency costs. If that cannot be shown, choose defer/reject rather than claiming bounded collection. Timing regression is a measured tradeoff, not a universal numerical product gate.
- [ ] Write `decision.md` with accept/defer/reject per binary/text and per query/benchmark path, real-workload demand still needed, whether disk spill is acceptable, and unverified live-provider behavior. `design.md` supplies proposed reader signatures/lifetimes and future production file/test map only for accepted subsets.
- [ ] Verify: `pwsh -NoProfile -File plans/119-evidence/verify.ps1` -> exit 0, or record the failed candidate as a no-go and make the verifier distinguish valid negative study outcome from corrupted/missing evidence. No future implementation is authorized by a positive spike.

## Done criteria and future gates

- [ ] Inventory, repeatable runner, independent boundary/hash assertions, raw measurements and per-path decision all exist.
- [ ] Scripts exit successfully only for a fully accounted-for study; rejected candidates retain failure evidence and cannot be labeled implemented.
- [ ] `git diff --name-only $studyStart -- src tests scripts Directory.Build.props Directory.Packages.props` is empty; check untracked scope with `git status --short`; Markdown links/whitespace pass.
- [ ] No claim of constant process memory, real provider acceptance or reduced model tokens. Standard ignored spike build artifacts are distinguished from tracked evidence.
- [ ] A later selected product implementation requires canonical/result-equivalence/reader/collector tests, cancellation tests, Windows `just verify` and explicit changed-branch formatting. Live SQL Server proof would need a separately authorized disposable target; Linux remains unverified unless requested.

## STOP conditions and maintenance

Stop for hash incompatibility that cannot be explained within the spike, a requirement for real data/DB access, new packages/product edits, unbounded spool or copying full payloads into tracked evidence. A known failed candidate is a valid research result; missing measurements are not. Future provider changes must preserve forward-only reader lifetimes and actual canonical length framing. Keep isolated branch `plan-119`; no merge/push/install.
