# Plan 027: Gain accounting never fails a successful command, survives bad or newer lines, and records MCP calls as the spec requires

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/GainStore.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs tests/SqlHarness.Tests/GainStoreTests.cs tests/SqlHarness.Mcp.Tests/McpGainTests.cs README.md docs/mcp.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S–M
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md
- **Category**: bug
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

After each command, SQLHarness appends one metadata-only line to `~/.sqlharness/data/gain.jsonl` (bytes in vs bytes out) through an "emission receipt". Problems:

1. **Cross-process collisions change exit codes.** The only lock is a `static` in-process object, and the writer opens with `FileShare.Read`. Two processes appending at once (two agent CLIs, or the CLI plus the long-lived MCP server) can hit a Windows sharing violation. The receipt then returns `LocalStorage` (exit 6), and the CLI uses that as the process exit code even though the query **succeeded**.
2. **One bad line breaks `gain` forever.** `Aggregate` throws on the first malformed line (a crash mid-write) **and** on any command name outside a hard-coded list. When a newer binary records a new command (e.g. `pgstop` from plan 014), every older binary still on PATH fails `gain`. Plan 015 documents that stale binaries on PATH are real here.
3. **MCP never records gain.** The design spec (`docs/superpowers/specs/2026-09-26-mcp-adapter-design.md` §6: "Gain zapisuje raz logiczne wywołanie, a footprint obejmuje rzeczywiste oba przedstawienia wyniku", i.e. gain records each logical call once, and the footprint covers both actual representations of the result) and `docs/mcp.md:66` ("gain accounting may write to disk") say MCP calls are recorded. But no production code in `src/SqlHarness.Mcp` calls `EmissionReceipt.CompleteAsync`. Only the test `McpGainTests.cs` does it by hand. `sqlharness_gain` therefore reports CLI history only.

## Current state

- `src/SqlHarness.Core/GainStore.cs`:
  - `:45` `private static readonly object FileLock = new();`
  - `Append` (`:51-80`): `new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read)`, writes one JSON object plus `\n`.
  - `Aggregate` (`:82-106`): `foreach (var line in File.ReadLines(_path)) { ... var record = ReadRecord(line); ... GetAccumulator(...)?.Add(record); }`.
  - `GetAccumulator` (`:128-147`) throws `ArgumentException` for unknown commands; `"plan" or "schema" => null`.
  - `ReadRecord` (`:149-165`) uses `JsonDocument.Parse(line)` and `Validate(record)`, which throws for unknown commands (`:167-171`).
- `src/SqlHarness.Core/SqlHarnessModule.cs:276-306` `WithReceipt(...)`: the receipt lambda catches **any** exception from `_gainStore.Append` and returns `SqlHarnessExitCode.LocalStorage`.
- CLI consumer: `src/SqlHarness.Cli/Infrastructure/OutputContext.cs` around `:57-59` (read it) turns the receipt result into the process exit code.
- MCP: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `RunAsync` (`~:333-350`) and `RunDbAsync` (`~:400-430`), both `return McpResultAdapter.Adapt(outcome, command, budget);` with no receipt completion. `McpResultAdapter.EmittedFootprint(CallToolResult)` (`McpResultAdapter.cs:244`) exists for exactly this purpose.
- Test that shows the intended MCP wiring: `tests/SqlHarness.Mcp.Tests/McpGainTests.cs:85-110` (`One_execution_writes_one_gain_record_despite_double_complete`): `var emitted = McpResultAdapter.EmittedFootprint(McpResultAdapter.Adapt(outcome, "sqlharness_query")); await receipt.CompleteAsync(emitted);`.
- README (`:255`): "Every command except `gain` records metadata-only raw and emitted byte counts…". In practice offline commands (`capabilities`, `doctor`, `validate`, `artifact`, `mcp serve`) never record. Step 5 corrects the sentence.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Gain tests | `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName~Gain"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `src/SqlHarness.Core/GainStore.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (receipt completion only), `tests/SqlHarness.Tests/GainStoreTests.cs`, `tests/SqlHarness.Mcp.Tests/McpGainTests.cs`, `README.md` (gain sentence), `docs/mcp.md` (one sentence).

**Out of scope**: the gain report shape (`SqlHarnessGainReport` fields), adding new buckets, log rotation (note it as a follow-up), and `WithReceipt` semantics in the CLI. Keep `LocalStorage` for genuine persistent failures after the retry.

## Git workflow

Branch `fix/plan-027-gain`. Commits per step. Do NOT push.

## Steps

### Step 1: Tolerant aggregation (RED → GREEN)

Tests in `GainStoreTests.cs` (follow the file's existing temp-path pattern):
- A file with a valid line, a truncated line (`{"timestamp":"2026-`), and another valid line → `Aggregate` succeeds and `Total.Executions == 2`.
- A line with `"command":"pgstop"` (otherwise valid) → counted in `Total`, no exception, no named bucket.

Implementation: in `Aggregate`, wrap `ReadRecord` in a `try/catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)` that skips the line. Do not count skipped lines anywhere in the report shape, because changing the report shape is out of scope; add a `// 027:` comment. Split `Validate` into `ValidateForAppend` (strict, unchanged list, used by `Append`) and `ValidateForRead` (non-negative numbers only). `GetAccumulator` returns `null` for unknown commands instead of throwing.

**Verify**: gain tests pass.

### Step 2: Cross-process-safe append

- Open with `FileShare.ReadWrite` (other appenders and readers may coexist; each `Append` writes one line in a single `Write` call). Build the full line, JSON plus `\n`, in a `MemoryStream`/`ArrayBufferWriter<byte>` first, then write it with **one** `stream.Write(bytes)` so a concurrent appender cannot interleave inside a line.
- Retry `IOException` up to 5 times with 20–100 ms jittered backoff before letting it propagate.
- In `Aggregate`, open the file with `new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)` and a `StreamReader`, instead of `File.ReadLines`, so reading never blocks or fails because a writer has the file open.

Test: start two `Task.Run` loops, each with its **own** `GainStore` instance on the same path, appending 200 records each, then `Aggregate` → `Total.Executions == 400` and no exception. This exercises the OS file sharing within one process, which is the best a unit test can do. Note in the test comment that cross-process behavior is the same Win32 sharing check.

**Verify**: gain tests pass, three runs in a row (`for /L` or a PowerShell loop).

### Step 3: MCP records gain once per logical call

In `McpToolCatalog.cs`, in both `RunAsync` and `RunDbAsync`, replace `return McpResultAdapter.Adapt(outcome, command, budget);` with:

```csharp
            var result = McpResultAdapter.Adapt(outcome, command, budget);
            await CompleteGainAsync(outcome, result);
            return result;
```

and add:

```csharp
    // 027: spec §6 — one gain record per logical call, footprint of the actual
    // wire result. Gain failures never change the tool result.
    private static async Task CompleteGainAsync(SqlHarnessOutcome outcome, CallToolResult result)
    {
        if (outcome.EmissionReceipt is not { } receipt)
            return;
        try { _ = await receipt.CompleteAsync(McpResultAdapter.EmittedFootprint(result)); }
        catch { /* best-effort accounting */ }
    }
```

Do this only on the path where an `outcome` exists. `Fail(...)` and cancelled results have no receipt. `RunAsync` receives a lambda result, so keep the outcome in a local before adapting.

Test in `McpGainTests.cs`: drive one real `sqlharness_capabilities` or offline-failing `sqlharness_query` call through the catalog (pattern: other tests in this project that invoke tools through the in-process host; find one with `grep -rn "CallToolAsync\|InvokeAsync" tests/SqlHarness.Mcp.Tests`). Then call `sqlharness_gain` and assert `Total.Executions` increased by exactly 1 for the query call. Use an isolated `SQLHARNESS_HOME`, following the `McpScopeHome` pattern in that project.

**Verify**: `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~Gain"` → pass.

### Step 4: CLI exit code is not changed by a gain write failure after retries (decision recorded)

Keep the current behavior (`LocalStorage` after retries are exhausted). With Step 2 it only happens on genuine disk problems. Add a comment at `WithReceipt` pointing to this plan. No code change.

### Step 5: Docs

- `README.md:255`: "Commands that execute against a target, plus `plan`, record…" (list matches `GainStore`'s append list). Add "MCP tool calls are recorded the same way (one record per call)."
- `docs/mcp.md:66`: keep the sentence, and add that `sqlharness_gain` includes MCP calls.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

Two aggregation tests, one concurrency test and one MCP gain test. Patterns: `GainStoreTests.cs` and `McpGainTests.cs:85-110`.

## Done criteria

- [ ] Gain tests pass in both projects; full gate passes
- [ ] `grep -n "FileShare.Read)" src/SqlHarness.Core/GainStore.cs` → no match
- [ ] `grep -n "CompleteGainAsync" src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` → definition plus 2 call sites
- [ ] `plans/README.md` row updated

## STOP conditions

- MCP wire-budget tests (`McpTokenBudgetTests`, `McpOutputTests`) change. Gain must not alter the result.
- The MCP gain write makes `McpStdioProcessTests` slower or flakier (the stderr/stdout separation must hold). Run that class twice.
- An existing test asserts that `gain` **fails** on an unknown command. Report it; the contract may have intended that.

## Maintenance notes

- `gain.jsonl` grows without bound. Rotation (e.g. monthly files) is a later, separate change.
- When adding a command, add its bucket only if it needs one. Unknown commands now count toward `Total` safely, so a newer binary's records no longer break older ones.
