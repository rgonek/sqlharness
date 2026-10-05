# Plan 020: The Azure CLI token fetch can no longer run a binary planted in the working directory, and it cannot hang or read MCP stdin

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/Auth/ tests/SqlHarness.Tests/Auth/`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1 (security)
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md
- **Category**: security
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

Profiles with `"auth": "azure-cli"` fetch an Entra token by spawning the Azure CLI on **every connection**, for both CLI and the MCP server. The process is started by **bare name** (`cmd.exe` on Windows, `az` elsewhere) with the working directory set to the **current directory**. For coding agents, that is normally a checked-out repository:

- On Windows, `CreateProcess` searches the current directory before `System32` for a bare `cmd.exe`, and `cmd /c az` looks for `az.cmd`/`az.bat`/`az.exe` in the current directory before `PATH`.
- On Unix, .NET resolves a bare file name against the current directory before `PATH`.

So a repository containing an `az.cmd` (or `az`) executes it whenever an agent runs SQLHarness from that repository with an azure-cli profile. That is arbitrary code execution, and the fake `az` can also hand back a token of its choosing. Separately, the child has **no timeout** (only the caller's token) and **inherits stdin**. Under the MCP stdio server, stdin is the JSON-RPC stream, so a hanging or prompting `az` blocks the single DB operation slot and can consume protocol bytes.

## Current state

Files:
- `src/SqlHarness.Core/Auth/AzureCli.cs`: `ProcessRunner` (generic, internal) and `AzureCli` (public).
- `src/SqlHarness.Core/Auth/IAzureCli.cs`: interface (`IsLoggedInAsync`, `RunJsonAsync`).
- `src/SqlHarness.Core/SqlExecution.cs:228-232`: the only production consumer of the token:

  ```csharp
          if (target.Auth.RequiresAccessToken)
              accessToken = ReadAccessToken(await _azureCli.RunJsonAsync(AccessTokenArguments, ct));
  ```
- `src/SqlHarness.Core/SqlHarnessModule.cs:70`: wiring `new SqlClientSessionFactory(new AzureCli())`.
- Tests: `tests/SqlHarness.Tests/Auth/AzureCliTests.cs` (uses a `StubProcessRunner`), `tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs` (Windows-only process-tree tests that call `new ProcessRunner().RunAsync("powershell.exe", ...)`).

`ProcessRunner.RunAsync` (`AzureCli.cs:19-50`):

```csharp
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };
        ...
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessTreeAsync(process);
            throw;
        }
```

`AzureCli` (`AzureCli.cs:85-146`):

```csharp
    private string Executable => _isWindows ? "cmd.exe" : "az";
    ...
        var result = await _runner.RunAsync(
            Executable,
            BuildArguments(fullArgs, _isWindows),
            cancellationToken: cancellationToken);
    ...
        var fullArgs = new List<string> { "/c", "az" };
```

`BuildArguments` also rejects arguments containing whitespace or cmd metacharacters on Windows (`IsUnsafeForWindowsCommand`). Keep that.

Exit-code mapping (`src/SqlHarness.Core/OperationFailureMapper.cs:33`): `AzureCliException => SqlHarnessExitCode.Authentication` (exit 3). Throwing `AzureCliException` on timeout gives the right exit code with no mapper change.

Error-message convention: `AzureCliException` messages are fixed and never contain arguments, stdout or stderr (pinned by `RunJsonAsync_NonzeroExitUsesRedactedException`). New messages must follow that.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Auth tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlHarness.Tests.Auth"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**:
- `src/SqlHarness.Core/Auth/AzureCli.cs`
- `tests/SqlHarness.Tests/Auth/AzureCliTests.cs`
- `tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs` (only to add a stdin test)

**Out of scope**:
- `IAzureCli` public surface (do not change signatures).
- `AuthSpec.cs` / profile schema (no new profile fields).
- `SqlExecution.cs` (the consumer stays the same).
- README/AGENTS changes, except one line in README's azure-cli section stating the timeout (optional).

## Git workflow

- Branch `fix/plan-020-azure-cli-hardening`. Commits `test(020): ...` then `fix(020): ...`. Do NOT push.

## Steps

### Step 1: Tests for the new resolution contract (RED)

`AzureCli` gets an internal seam to make resolution testable without touching real PATH:

```csharp
    internal AzureCli(IProcessRunner runner, bool isWindows, AzureCliLocator locator, TimeSpan timeout)
```

(keep the existing constructors delegating to it with `AzureCliLocator.Default` and `TimeSpan.FromSeconds(60)`).

Write tests in `AzureCliTests.cs` against a recording `StubProcessRunner` that captures `fileName`, `arguments` and `workingDirectory`:

1. `Windows_uses_absolute_system_cmd_and_neutral_working_directory`: with `isWindows: true` and a locator whose system directory is `C:\Windows\System32`, assert `fileName == @"C:\Windows\System32\cmd.exe"` and `workingDirectory` equals that same system directory, **not** `Directory.GetCurrentDirectory()`.
2. `Unix_resolves_az_from_absolute_PATH_entries_only`: locator with PATH entries `["", ".", "relative/bin", "/opt/az/bin", "/usr/bin"]` and a file-exists predicate true only for `/opt/az/bin/az` and `./az`. Assert `fileName == "/opt/az/bin/az"`.
3. `Unix_missing_az_throws_AzureCliException`: no match → `AzureCliException` with the fixed message `"Azure CLI executable was not found on PATH."`, and the runner is never called.
4. `Timeout_kills_and_throws_AzureCliException`: stub runner that awaits `Task.Delay(Timeout.Infinite, token)`; `timeout = 200 ms`; caller token not cancelled → `AzureCliException` with message `"Azure CLI did not finish within the time limit."` within 5 s.
5. `Caller_cancellation_still_surfaces_as_OperationCanceledException`: caller token cancelled → `OperationCanceledException` (not `AzureCliException`).

**Verify**: build fails (constructor/locator do not exist) or the tests fail. Either is RED.

### Step 2: Implement `AzureCliLocator`

Add, in `AzureCli.cs`:

```csharp
internal sealed class AzureCliLocator(
    Func<string> systemDirectory,
    Func<string?> pathVariable,
    Func<string, bool> fileExists)
{
    internal static AzureCliLocator Default { get; } = new(
        () => Environment.SystemDirectory,
        () => Environment.GetEnvironmentVariable("PATH"),
        File.Exists);

    internal string WindowsCommandProcessor => Path.Combine(systemDirectory(), "cmd.exe");

    internal string NeutralWorkingDirectory => systemDirectory(); // Windows

    // Unix: first absolute PATH entry containing an "az" file. Relative and
    // empty entries (which mean the current directory) are skipped.
    internal string? FindUnixAz() { ... }
}
```

On Unix, `Environment.SystemDirectory` is empty. Use `"/"` as the neutral working directory there. Use `Path.IsPathFullyQualified(entry)` to accept an entry.

### Step 3: Use it in `AzureCli`

- Windows: `fileName = locator.WindowsCommandProcessor`, args unchanged (`/c az ...`), `workingDirectory = locator.NeutralWorkingDirectory`. `cmd` still finds `az` via PATH, but the current directory is now `System32`, which a user cannot write to, so a planted `az.cmd` in the agent's repo is no longer reachable. Also pass the child an environment where `NoDefaultCurrentDirectoryInExePath=1`. This needs a way to set environment variables through `IProcessRunner`: add an optional `IReadOnlyDictionary<string,string>? environment = null` parameter to `IProcessRunner.RunAsync` and `ProcessRunner`, applied to `startInfo.Environment`. Update `StubProcessRunner` implementations in tests accordingly.
- Unix: `fileName = locator.FindUnixAz() ?? throw new AzureCliException("Azure CLI executable was not found on PATH.")`, `workingDirectory = "/"`.
- Timeout: wrap the run in `using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(_timeout);`. Catch `OperationCanceledException` **when** `!cancellationToken.IsCancellationRequested` and rethrow as `AzureCliException("Azure CLI did not finish within the time limit.")`. `ProcessRunner` already kills the process tree on cancellation.
- Apply the same changes in `IsLoggedInAsync` and `RunJsonAsync` (factor one private `RunAzAsync`).

### Step 4: Do not inherit stdin

In `ProcessRunner.RunAsync`, set `RedirectStandardInput = true` and, right after `process.Start()`, call `process.StandardInput.Close()`. Add a Windows-only test in `ProcessRunnerTests.cs` (same `if (!OperatingSystem.IsWindows()) return;` convention as its neighbours): run `cmd.exe /c "set /p x= & echo done"` with arguments that pass the runner's API (or a tiny PowerShell `[Console]::In.ReadToEnd()` script) and assert it completes within 10 s with exit 0, i.e. it saw EOF instead of waiting.

**Verify**: `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~SqlHarness.Tests.Auth"` → all pass, including the 5 new `AzureCliTests` and the stdin test.

### Step 5: Full gate

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`. `grep -n '"cmd.exe" : "az"' src/SqlHarness.Core/Auth/AzureCli.cs` → no match.

## Test plan

- 5 new `AzureCliTests` (Step 1) plus 1 `ProcessRunnerTests` stdin test.
- Pattern: existing `StubProcessRunner` usage in `AzureCliTests.cs:15-40`.
- Manual check, optional and only with the user's own logged-in `az`: `sqlharness ping <azure-cli profile> --json` still succeeds. Do not run against a profile without explicit user approval.

## Done criteria

- [ ] Auth tests pass, including the new ones
- [ ] `grep -n "Directory.GetCurrentDirectory" src/SqlHarness.Core/Auth/AzureCli.cs` → only inside `ProcessRunner`'s generic default, never on the AzureCli path (AzureCli always passes `workingDirectory`)
- [ ] `grep -n "RedirectStandardInput = true" src/SqlHarness.Core/Auth/AzureCli.cs` → match
- [ ] `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`
- [ ] Only in-scope files changed; `plans/README.md` row updated

## STOP conditions

- The `IProcessRunner` signature change breaks callers outside `src/SqlHarness.Core/Auth/` and `tests/SqlHarness.Tests/Auth/`. Find them with `grep -rn "IProcessRunner" src tests`, and report rather than widening scope.
- A Windows `az` install is found only through a PATH entry that is relative. Report it; do not add CWD back.
- The existing `ProcessRunnerTests` process-tree tests fail after the stdin change.

## Maintenance notes

- Any new external process the harness spawns must go through the same rules: an absolute executable, a neutral working directory, no inherited stdin, and a bounded wait.
- The 60 s default covers a slow `az account get-access-token`. If users report timeouts on first-run token refresh, raise the default instead of removing it.
- Reviewers: check that no exception message includes the resolved `az` path. Paths are not secrets, but the existing redaction tests assume fixed messages.
