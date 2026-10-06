# Plan 016: Main is green again in CI and one local command reproduces the CI gate

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs tests/SqlHarness.Tests/AgentWorkflowTests.cs tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json .github/workflows/ci.yml README.md AGENTS.md scripts/`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (every later plan depends on this one)
- **Category**: tests / dx
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

GitHub Actions CI (`.github/workflows/ci.yml`, ubuntu-latest) has failed on every push to `main` since 2026-09-28 (runs `36453910837`, `37057027804`; last green run 2026-09-24). Two tests fail **only on Linux**, so they pass on the Windows dev machine. When tests fail, the `dotnet format --verify-no-changes` step is skipped, and that step also fails today on about 50 files. `release.yml` runs the same tests on ubuntu-latest, so **the next `v*` tag would fail to release**. Until this is fixed, CI gates nothing, and no other plan can show a green gate.

## Current state

The CI workflow, `.github/workflows/ci.yml`:

```yaml
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - run: dotnet restore
      - run: dotnet build --no-restore -warnaserror
      - run: dotnet test --no-build
      - run: dotnet format --no-restore --verify-no-changes
```

### Failure A — `tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs:360-373`

```csharp
    [Fact]
    public async Task Case_only_sibling_is_rejected_on_case_sensitive_filesystems()
    {
        var probe = Path.Combine(_home, "caseprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        if (Directory.Exists(probe.ToUpperInvariant()))
            return; // Case-insensitive FS: no proof possible here. See T1 report.

        var sibling = _root.ToUpperInvariant();
        Directory.CreateDirectory(sibling);
```

`_root` is `Path.Combine(_home, "inputs")` and `_home` is `Path.Combine(Path.GetTempPath(), "sqlharness-mcp-t3-reader-<guid>")` (lines 29 and 39). On Linux, `ToUpperInvariant()` turns the **whole absolute path** into `/TMP/SQLHARNESS-MCP-T3-READER-…/INPUTS`, and creating it fails with `UnauthorizedAccessException: Access to the path '/TMP' is denied.` On Windows the early `return` fires, so this test has **never run its assertion on any machine**. The intent is to create a sibling directory that differs from `_root` only in the case of the **last segment**.

### Failure B — `tests/SqlHarness.Tests/AgentWorkflowTests.cs:74-100` and `:144-156`

The compare test writes `artifactDirectory = Path.Combine(Path.GetTempPath(), $"sqlharness-artifacts-{Guid.NewGuid():N}")`, which is echoed into the JSON output. `AssertBytesWithinBudget` then asserts an **exact** byte count:

```csharp
        var observedBytes = budgets.RootElement.GetProperty("observedUtf8Bytes").GetProperty(scenario).GetInt32();
        ...
        Assert.Equal(observedBytes, utf8Bytes);
        Assert.InRange(utf8Bytes, 1, budget);
```

`tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json` pins `"compareSummary": 1006`, the Windows value (`C:\\Users\\…\\Temp\\…` JSON-escaped). Linux CI produces `1001`. The other two scenarios contain no temp path and pass on both OSes. The exact pin is intentional: it is a regression guard on agent output size. Keep it, and make the measured content platform-independent.

### Failure C — formatter drift

`dotnet format SqlHarness.sln --no-restore --verify-no-changes` exits 2 with about 316 diagnostics:

- `FINALNEWLINE`: `.editorconfig:32` sets `insert_final_newline = false`, but files added since 2026-09-26 (e.g. `src/SqlHarness.Mcp/McpHost.cs`, `src/SqlHarness.Cli/SqlHarnessCli.cs`, many test files) end with a newline.
- `IMPORTS` ordering in 7 files, e.g. `src/SqlHarness.Cli/Infrastructure/OutputContext.cs:1-3`:

  ```csharp
  using SqlHarness.Core;
  using System.Globalization;
  using SqlHarness.Cli.Commands;
  ```

  `.editorconfig:38-39` requires `dotnet_separate_import_directive_groups = true` and `dotnet_sort_system_directives_first = true`.

This repo keeps the existing `.editorconfig` policy. Do not change the policy in this plan; apply it.

### Repo conventions

- Commit style (from `git log`): `test(012/final): …`, `fix(011/final): …`, `style: match formatter newline`. Use `test(016): …`, `style(016): …`, `ci(016): …`, `docs(016): …`.
- Branch naming (from history): `fix/plan-001-mcp-safe-logging`. Use `fix/plan-016-green-ci`.
- PowerShell scripts in `scripts/` use `Set-StrictMode`-style defensive PowerShell. Read the top 30 lines of `scripts/setup-local-postgres.ps1` and match its header style (param block, `$ErrorActionPreference = 'Stop'`).

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | `0 Warning(s)`, `0 Error(s)` |
| Offline tests | `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration"` | SqlHarness.Tests ~2811 passed / 0 failed; SqlHarness.Mcp.Tests ~212 passed / 0 failed / 4 skipped |
| Format check | `dotnet format SqlHarness.sln --no-restore --verify-no-changes` | exit 0, no output lines containing `error` |
| Format fix | `dotnet format SqlHarness.sln --no-restore` | exit 0 |
| Focused test | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~AgentWorkflowTests"` | all pass |

## Scope

**In scope**:
- `tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs`: only the `Case_only_sibling_is_rejected_on_case_sensitive_filesystems` method
- `tests/SqlHarness.Tests/AgentWorkflowTests.cs`: only the compare test and `AssertBytesWithinBudget` / a new normalization helper
- `tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json`: only `observedUtf8Bytes.compareSummary`
- Any `*.cs` file changed **only** by `dotnet format` (whitespace, final newline, using order)
- `scripts/verify.ps1` (create)
- `README.md` "Development" section, and `AGENTS.md` (add one short "Verify changes" section)

**Out of scope**:
- `.editorconfig` policy values, `Directory.Build.props`. Changing style policy is a separate decision (plan 032).
- `.github/workflows/*.yml`. CI hardening and an OS matrix are plan 032; release gating is plan 028. Do not touch workflows here.
- Any production code under `src/` beyond what `dotnet format` itself rewrites.
- The skip/return guards in other tests (plan 032).

## Git workflow

- Branch `fix/plan-016-green-ci` from `main`.
- Separate commits: (1) test fixes, (2) the pure `dotnet format` run (`style(016): apply formatter`), (3) the verify script and docs.
- Do NOT push. Do not open a PR unless the operator says so.

## Steps

### Step 1: Fix the case-sibling test so it only changes the last segment's case

In `McpInputReaderTests.cs`, replace `var sibling = _root.ToUpperInvariant();` with code that keeps the parent unchanged and upper-cases only the final directory name:

```csharp
        var sibling = Path.Combine(
            Path.GetDirectoryName(_root)!,
            Path.GetFileName(_root).ToUpperInvariant());
```

`Path.GetFileName(_root)` is `"inputs"`, so the sibling is `<home>/INPUTS`. Change nothing else in the method.

**Verify**: `dotnet build SqlHarness.sln -warnaserror` → 0 warnings. Then `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~Case_only_sibling"` → 1 passed. On Windows it passes trivially through the early return. That is expected; the Linux proof comes from CI after push.

### Step 2: Make the compare byte measurement independent of the temp path

In `AgentWorkflowTests.cs`, in `Compare_summary_points_to_existing_artifacts_and_leaves_detail_lookup_for_plan_06_t2`, change the final call so that the measured content has the artifact directory replaced by a fixed token **in its JSON-escaped form**:

```csharp
            var escapedDirectory = JsonSerializer.Serialize(artifactDirectory).Trim('"');
            var normalized = output.ToString().Replace(escapedDirectory, "<artifactDirectory>", StringComparison.Ordinal);
            AssertBytesWithinBudget("compareSummary", normalized, exitCodes);
```

`JsonSerializer.Serialize` gives the same escaping the CLI output uses (backslashes doubled on Windows). If the CLI's encoder escapes characters differently from the default `JsonSerializer` (check by printing both strings once), use the exact substring that appears in `output.ToString()`. Do not change the other two scenarios.

Then run the test once. It now fails with `Assert.Equal() Failure` and prints a line `compareSummary: calls=1, utf8Bytes=<N>, budget=8192` (shown with `--logger "console;verbosity=detailed"`). Put that `<N>` into `byte-budgets.json` → `observedUtf8Bytes.compareSummary`.

**Verify**: `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~AgentWorkflowTests" --logger "console;verbosity=detailed"` (rebuild first) → all pass, and the printed `utf8Bytes` equals the new pinned value. Sanity check: the new value must equal `1006 - (JSON-escaped artifactDirectory length) + len("<artifactDirectory>")` for the path printed in the same run. If it does not, STOP.

### Step 3: Apply the formatter

Run `dotnet format SqlHarness.sln --no-restore`. Then `git diff --stat`. Every changed hunk must be whitespace, final newline, or `using` reordering. Spot-check 5 files with `git diff -w --ignore-blank-lines <file>`, which should show only `using` line moves or nothing.

**Verify**: `dotnet format SqlHarness.sln --no-restore --verify-no-changes` → exit 0. `dotnet build SqlHarness.sln -warnaserror` → 0 warnings.

### Step 4: Add `scripts/verify.ps1`, the local CI gate in one command

Create `scripts/verify.ps1`. It runs, in order and stopping at the first failure (check `$LASTEXITCODE` after each native call and `exit` with it):

1. `dotnet restore SqlHarness.sln`
2. `dotnet build SqlHarness.sln --no-restore -warnaserror`
3. `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration"`
4. `dotnet format SqlHarness.sln --no-restore --verify-no-changes`

Print one line per stage (`==> build`) and finish with `verify: OK`. No parameters are needed. Do not run integration tests and do not touch `~/.sqlharness`.

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → ends with `verify: OK`, exit code 0 (`echo $LASTEXITCODE` → 0).

### Step 5: Document the gate

- `README.md`, "Development" section (around line 283): add `pwsh ./scripts/verify.ps1` as the command to run before committing. State that it matches CI. State that the SDK comes from `global.json` (currently `9.0.316`, `rollForward: latestPatch`).
- `AGENTS.md`: add a short `## Verify changes` section with the same command and one sentence: "CI runs the same four stages on ubuntu-latest; tests that pass only on Windows are not done."

**Verify**: `git diff --stat` shows only in-scope files. `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

- No new tests. Two existing tests are made platform-correct.
- The case-sibling test only gets a real proof on Linux. That proof is CI after push, outside this plan's local gate. Record in the index row that Linux proof is pending the next CI run.

## Done criteria

- [ ] `pwsh -NoProfile -File scripts/verify.ps1` exits 0 and prints `verify: OK`
- [ ] `dotnet format SqlHarness.sln --no-restore --verify-no-changes` exits 0
- [ ] `grep -n "_root.ToUpperInvariant()" tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs` → no match
- [ ] `byte-budgets.json` `compareSummary` changed, other two values unchanged (`git diff` of that file shows exactly one changed number)
- [ ] `git diff --name-only main` lists only in-scope files plus formatter-only files
- [ ] `plans/README.md` row for 016 updated (note: "Linux proof pending first CI run")

## STOP conditions

- `dotnet format` changes anything other than whitespace, final newlines or using order (e.g. it rewrites expressions). Stop and report the file list.
- After Step 2 the measured bytes still differ between two runs on the same machine.
- Any test other than the two named ones fails before your changes. Record its name, because it is a pre-existing flake owned elsewhere (see plan 005). Stop if it fails twice in a row after your change.
- `pwsh` is not available. Report; do not rewrite the script in another language.

## Maintenance notes

- Any future test that echoes an absolute path into output it byte-pins must normalize the path first. Reviewers should look for `Path.GetTempPath()` near `observedUtf8Bytes`.
- Plan 032 adds a Windows+Linux CI matrix so OS-specific test guards are exercised on both. Until then, Linux-only behavior is proven only by CI.
- Plan 028 makes `release.yml` depend on this green gate.
