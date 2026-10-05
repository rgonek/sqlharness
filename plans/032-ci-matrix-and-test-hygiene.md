# Plan 032: CI runs on Windows and Linux, OS-specific tests report Skip instead of passing silently, and the test suite stops racing on env vars and leaking publish output

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- .github/workflows/ci.yml .gitattributes tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs tests/SqlHarness.Tests/Postgres/PostgresConnectionStringTests.cs tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs tests/SqlHarness.Tests/ArtifactReaderTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md (required); plans/028-release-pipeline-hardening.md (edits `ci.yml` permissions and adds `workflow_call`; land 028 first or merge carefully)
- **Category**: tests / dx
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

The product is Windows-first (PowerShell examples, `cmd /c az`, a Windows-only approval tray design), but CI runs **only on ubuntu-latest**. Development happens on Windows. Several tests are written as `if (!OperatingSystem.IsWindows()) return;` (or the case/symlink equivalents), so each half of the platform-specific suite only runs on one OS, and the other OS reports a **pass**, not a skip. Plan 016's Linux failures are the direct result: two tests had only ever run on Windows. Also:
- Tests mutate process-wide env vars (`SQLHARNESS_PG_PASSWORD`, `P`) from classes that run **in parallel** (not in the non-parallel `SqlHarnessHomeCollection`). One class's `finally` can clear the variable while another is reading it, which is a plausible source of intermittent failures.
- `McpStdioProcessTests` runs a full self-contained `dotnet publish` into `%TEMP%` on every suite run and never deletes it.
- The two Pester suites (`tests/scripts/*.Tests.ps1`) are never run anywhere.
- `ci.yml` has no timeout, no concurrency cancel, no NuGet cache and no test-result upload.

## Current state

- `.github/workflows/ci.yml` (after plan 016; plan 028 may have added `permissions: contents: read` and `workflow_call:`):

  ```yaml
  jobs:
    build:
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
- Silent OS guards: `tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs:51,102,172` (`if (!OperatingSystem.IsWindows()) return;`), and the symlink/case guards in `tests/SqlHarness.Mcp.Tests/McpInputReaderTests.cs` (~148, 163, 299, 349, 366) and `tests/SqlHarness.Tests/ArtifactReaderTests.cs` (~334). Read each one; some return when a symlink cannot be created, which is a capability check rather than an OS check.
- The existing **good** pattern for reporting a skip, `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs:647-655`:

  ```csharp
  [AttributeUsage(AttributeTargets.Method)]
  internal sealed class NativeRidFactAttribute : FactAttribute
  {
      public NativeRidFactAttribute(string rid)
      {
          if (!string.Equals(rid, McpStdioProcessHarness.CurrentRid, StringComparison.OrdinalIgnoreCase))
              Skip = $"Requires a native {rid} runner (this host is {McpStdioProcessHarness.CurrentRid}).";
      }
  }
  ```
- Env-var races: `tests/SqlHarness.Tests/Postgres/PostgresConnectionStringTests.cs:19,34` (`SQLHARNESS_PG_PASSWORD`), `:40,52` (`P`); `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs:444,458` (`SQLHARNESS_PG_PASSWORD`). Neither class is in `[Collection(SqlHarnessHomeCollection.Name)]`. `PostgresConnectionStringTests.cs:178-194` already uses a per-test `variable`, which is the pattern to copy.
- Publish leak: `McpStdioProcessTests.cs:~82-84` publishes to `Path.Combine(Path.GetTempPath(), "sqlharness-mcp-stdio-publish-" + Guid…, rid)`. Find where the harness is disposed and whether that directory is deleted (`grep -n "stdio-publish\|Directory.Delete" tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs`).
- Pester: `tests/scripts/setup-local-postgres.Tests.ps1` and `setup-local-adventureworks.Tests.ps1`, "Pester 3.4-compatible", using a fake `docker` on PATH (a `.cmd`, so Windows-only).
- There is no `.gitattributes`. `.editorconfig` sets `insert_final_newline = false`, and the dev machine uses `core.autocrlf=true`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |
| Skip report | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~ProcessRunner" --logger "console;verbosity=normal"` on Windows | tests run (no skips on Windows) |
| Pester (Windows) | `powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"` | exit 0 |

## Scope

**In scope**: `.github/workflows/ci.yml`, `.gitattributes` (create), the test files listed above, and a new `tests/SqlHarness.Tests/PlatformFactAttributes.cs` (and an equivalent in the MCP test project if needed).

**Out of scope**: integration DB jobs (plan 036), release workflow (plan 028), production code, and analyzer/`EnforceCodeStyleInBuild` changes (not worth the churn now).

## Git workflow

Branch `ci/plan-032-matrix`. Commits per step. Do NOT push.

## Steps

### Step 1: Skips that say so

Create `PlatformFactAttributes.cs` with `WindowsFactAttribute : FactAttribute` (`Skip = "Requires Windows."` when `!OperatingSystem.IsWindows()`) and `UnixFactAttribute` (the reverse), modelled on `NativeRidFactAttribute`. Replace the three `ProcessRunnerTests` guards: change `[Fact]` to `[WindowsFact]` and delete the early `return`.

For the symlink/case guards: when the guard is an **OS** check, use the attribute. When it is a **runtime capability** check (cannot create a symlink, filesystem is case-insensitive), xUnit v2 cannot skip dynamically, so keep the early return and write the reason to `ITestOutputHelper` (if the class has one), with a comment `// capability guard: xUnit v2 has no dynamic skip`. Do not invent a dynamic-skip library.

**Verify**: full gate passes on Windows, with the same pass count minus 0 (Windows still runs them).

### Step 2: Remove env-var races

- `PostgresConnectionStringTests.cs:19-34` and `:40-52`, `PostgresSafetyTests.cs:444-458`: use a unique variable per test (`var variable = $"SQLHARNESS_TEST_{Guid.NewGuid():N}";`) and pass that name into the profile/`AuthSpec` instead of the fixed `SQLHARNESS_PG_PASSWORD` / `P`. Keep the assertions otherwise identical. If a test asserts the variable **name** appears in an error message, assert the generated name.
- `grep -rn 'SetEnvironmentVariable("' tests --include=*.cs | grep -v SQLHARNESS_HOME` → every remaining fixed name must be in a class marked `[Collection(SqlHarnessHomeCollection.Name)]` (or the MCP project's equivalent). Fix any that are not, using the same technique.

**Verify**: full gate passes three times in a row.

### Step 3: Clean up the publish output

In `McpStdioProcessTests`, delete the `sqlharness-mcp-stdio-publish-<guid>` root after the smoke test (in the fixture/harness dispose, or a `finally` around the test). Use a best-effort delete that ignores `IOException` (a just-exited process may hold the file briefly; retry 3× with 200 ms).

**Verify**: run the publish smoke once (`dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~Published_single_file_server_passes_the_stdio_smoke_on_this_rid"`), then `Get-ChildItem $env:TEMP -Filter "sqlharness-mcp-stdio-publish-*"` returns nothing new.

### Step 4: CI matrix and hygiene

Rewrite `ci.yml`'s job, keeping any `permissions`/`workflow_call` added by plan 028:

- `strategy: { fail-fast: false, matrix: { os: [ubuntu-latest, windows-latest] } }`, `runs-on: ${{ matrix.os }}`, `timeout-minutes: 30`.
- Top-level `concurrency: { group: ci-${{ github.ref }}, cancel-in-progress: true }`.
- Cache: `actions/setup-dotnet` with `cache: true` and `cache-dependency-path: '**/*.csproj'` if supported by the pinned version. Otherwise use `actions/cache` on `~/.nuget/packages`, keyed on `hashFiles('Directory.Packages.props', '**/*.csproj')`.
- Test step: `dotnet test --no-build --filter "FullyQualifiedName!~Integration" --logger trx --results-directory TestResults`, then `actions/upload-artifact` (`if: always()`, name `test-results-${{ matrix.os }}`).
- Format step only on ubuntu (`if: matrix.os == 'ubuntu-latest'`), to avoid CRLF noise.
- A Windows-only step `Invoke-Pester` for `tests/scripts`, using the command from the table (`shell: powershell`). First confirm locally that the suites pass under the available Pester. If they need Pester 3.4 and the runner has only 5.x, use `Install-Module Pester -RequiredVersion 3.4.0 -Force -SkipPublisherCheck -Scope CurrentUser` in that step.
- If plan 028 has SHA-pinned actions, pin any new action the same way.

Create `.gitattributes` with `* text=auto` (normalize to LF in the repo, leaving each machine's checkout preference alone) and `*.ps1 text eol=crlf` (Windows PowerShell 5.1 compatibility). After adding it, run `git add --renormalize .` and confirm `git status` shows **no** content changes beyond `.gitattributes`. The repo already stores LF. If files change, STOP and report the list.

**Verify**: local full gate passes. A workflow lint such as `actionlint`, if available, reports no errors. The real proof is the first CI run after push: record it in the index row as pending.

## Test plan

No new behavior tests. Attribute conversions, env-var isolation and cleanup are covered by running the existing suite repeatedly (Step 2: three consecutive green runs).

## Done criteria

- [ ] `grep -rn "if (!OperatingSystem.IsWindows())" tests --include=*.cs` → no match (or only capability guards with the explanatory comment)
- [ ] `grep -rn 'SetEnvironmentVariable("SQLHARNESS_PG_PASSWORD"' tests` → no match
- [ ] `ci.yml` contains `windows-latest`, `timeout-minutes`, `concurrency`, `--logger trx`, `Invoke-Pester`
- [ ] Full gate green three runs in a row locally
- [ ] `plans/README.md` row updated (CI proof pending first push)

## STOP conditions

- `git add --renormalize .` changes file contents.
- The Pester suites fail locally on Windows before any change. Report it; they may be stale. Do not fix the scripts in this plan.
- A Windows-only test fails when converted to an attribute (it was relying on the silent return elsewhere).

## Maintenance notes

- New OS-specific tests must use `[WindowsFact]`/`[UnixFact]`, never an early `return`.
- If CI time on Windows becomes a problem, move the publish smoke to a separate job rather than dropping the OS.
