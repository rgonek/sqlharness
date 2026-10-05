# Plan 034: On Unix, SQLHarness data (snapshots, plans, Query Store text, gain) is created owner-only, and `doctor` warns when the home is readable by others

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/SqlHarnessPaths.cs src/SqlHarness.Core/SnapshotStore.cs src/SqlHarness.Core/ArtifactDirectoryPublisher.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/GainStore.cs src/SqlHarness.Cli/Commands/DoctorCommand.cs README.md AGENTS.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2 (security, Unix/macOS)
- **Effort**: M
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md; plans/032 (a Linux CI leg) is needed for automated proof of the Unix branch
- **Category**: security
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

`~/.sqlharness` (or `$SQLHARNESS_HOME`) holds data AGENTS.md calls **locally sensitive**: named snapshots with real result rows, compare artifacts with `.sqlplan` XML (which can embed parameter values), Query Store `queries.jsonl` with full SQL text, and the profile file `targets.json`. Every directory and file is created with .NET defaults. On Linux/macOS with the usual `umask 022`, that means **world-readable** (`0755` directories, `0644` files), so any other local user on a shared host or jump box can read production rows and query text. On Windows the user-profile ACL protects the default location. A custom `SQLHARNESS_HOME` inherits whatever its parent allows; this plan only documents that case.

## Current state

- `src/SqlHarness.Core/SqlHarnessPaths.cs` (whole file, 15 lines): `Home` = `SQLHARNESS_HOME` or `~/.sqlharness`; subpaths `targets.json`, `data/gain.jsonl`, `compare`, `snapshots`, `query-store`, `index-analysis`.
- Files that create directories or files in Core (`grep -rln "CreateDirectory\|File.Write\|new FileStream" src/SqlHarness.Core`): `ArtifactDirectoryPublisher.cs`, `Artifacts.cs`, `GainStore.cs`, `ParameterSetFileReader.cs` (reads only; confirm), `SnapshotStore.cs`.
  - `SnapshotStore.cs:~147-153`: `_createDirectory(_root)`, `_writeAllBytes(tempPath, bytes)`, `_move(tempPath, path, force)`. The I/O delegates are injected for tests.
  - `ArtifactDirectoryPublisher.cs:~58-75`: `Directory.CreateDirectory(_root)`, a staging directory, `writeStaging`, `_moveDirectory`.
  - `GainStore.cs:~55-61`: `Directory.CreateDirectory(directory)`, `new FileStream(... FileMode.Append ...)`.
- `src/SqlHarness.Cli/Commands/DoctorCommand.cs`: the offline installation check. Read it and its tests (`tests/SqlHarness.Tests/Cli/DoctorCommandTests.cs`) for the report shape.

.NET APIs (net8+): `Directory.CreateDirectory(string path, UnixFileMode mode)` (Unix only; throws `PlatformNotSupportedException` on Windows), `File.SetUnixFileMode(path, mode)`, `File.GetUnixFileMode(path)`, and `FileStreamOptions.UnixCreateMode`. Guard every call with `if (!OperatingSystem.IsWindows())`. The analyzer CA1416 enforces platform guards, and `TreatWarningsAsErrors` makes it an error.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings (CA1416 clean) |
| Focused tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Snapshot|FullyQualifiedName~ArtifactDirectoryPublisher|FullyQualifiedName~Gain|FullyQualifiedName~Doctor|FullyQualifiedName~PrivateHome"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `SqlHarnessPaths.cs` (a new `PrivateFiles` helper may live here or in a new `PrivateFileSystem.cs`), `SnapshotStore.cs`, `ArtifactDirectoryPublisher.cs`, `Artifacts.cs` (only where it creates files outside the publisher), `GainStore.cs`, `DoctorCommand.cs` and its Core report if needed, tests, and README/AGENTS (one paragraph).

**Out of scope**: Windows ACL changes; changing existing users' directory modes automatically (only `doctor` reports); `targets.json` creation (users write it themselves).

## Git workflow

Branch `fix/plan-034-private-home`. Commits per step. Do NOT push.

## Steps

### Step 1: A small private-filesystem helper

Create `internal static class PrivateFileSystem` with:
- `CreateDirectory(string path)`: on Unix, `Directory.CreateDirectory(path, UnixFileMode.UserRead | UserWrite | UserExecute)`; on Windows, `Directory.CreateDirectory(path)`. Note that the Unix overload applies the mode only to directories it **creates**.
- `RestrictFile(string path)`: on Unix, `File.SetUnixFileMode(path, UserRead | UserWrite)`; no-op on Windows.
- `IsGroupOrOtherAccessible(string path)`: Unix only; `File.GetUnixFileMode(path)` has any `Group*`/`Other*` bit.

Unit tests (`PrivateHomeTests.cs`): mark the Unix-only tests with the `[UnixFact]` attribute from plan 032. If 032 has not landed, use `if (OperatingSystem.IsWindows()) return;` with a `// replace with [UnixFact] (plan 032)` comment. Cover: a created nested directory has mode `0700`; a restricted file has `0600`.

**Verify**: focused tests pass on Windows (Unix tests skipped or returned).

### Step 2: Use it at every creation site

- `SnapshotStore`: the default `_createDirectory` delegate uses `PrivateFileSystem.CreateDirectory`. After `_writeAllBytes(tempPath, ...)`, call `PrivateFileSystem.RestrictFile(tempPath)` **before** `_move`, so the final file never exists with broad permissions.
- `ArtifactDirectoryPublisher`: `_root` and `staging` via `PrivateFileSystem.CreateDirectory`. After `writeStaging` returns, restrict every file under `staging` (`Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)`) before the move.
- `GainStore.Append`: the directory via the helper. Restrict the gain file after creation: if the file did not exist before opening, call `RestrictFile` once after the write.
- Any other creation site found by the grep in "Current state": same treatment, or a note in the commit explaining why it is not needed.

Also make the **home** directory itself private when SQLHarness creates it: all the subdirectories live under `Home`, and `PrivateFileSystem.CreateDirectory` creates missing parents with the mode, including the home itself when absent.

**Verify**: focused tests and the full gate pass. Existing tests that inject fake I/O delegates are unaffected.

### Step 3: `doctor` reports an exposed home

On Unix, when `SqlHarnessPaths.Home` exists and `IsGroupOrOtherAccessible(Home)` is true, `doctor` adds a warning: `"SQLHarness home is readable by other users; run: chmod 700 <home>"`. The path is not secret, but use the same placeholder style the doctor output already uses for paths. It must never change permissions itself. Follow the existing doctor check/warning structure and its JSON shape. Add a unit test with an injectable mode probe if doctor's design allows it; otherwise test the helper and keep doctor wiring minimal.

**Verify**: doctor tests pass.

### Step 4: Docs

README and AGENTS ("Treat … as locally sensitive" paragraph): "On Linux/macOS SQLHarness creates its home and data owner-only (0700/0600); `doctor` warns when an existing home is broader. On Windows the default home under the user profile inherits the profile's ACL; a custom `SQLHARNESS_HOME` inherits its parent's ACL."

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

`PrivateHomeTests` (Unix-only mode assertions), one doctor test, and existing snapshot/artifact/gain tests unchanged. The automated Unix proof comes from the Linux CI leg.

## Done criteria

- [ ] `grep -rn "Directory.CreateDirectory(" src/SqlHarness.Core --include=*.cs` → only inside `PrivateFileSystem` (or each remaining use has a justification comment)
- [ ] Full gate passes; build has no CA1416 errors
- [ ] `plans/README.md` row updated (Unix proof: pending CI if not run on Linux locally)

## STOP conditions

- A test double or injected delegate signature must change in a way that ripples beyond the in-scope files.
- `File.SetUnixFileMode` fails on macOS for files inside a directory created moments earlier (unexpected; report it).

## Maintenance notes

- Any new persistent output must go through `PrivateFileSystem`. Reviewers should grep for raw `Directory.CreateDirectory` / `File.WriteAll*` in Core.
- An owner-only ACL on Windows for a custom `SQLHARNESS_HOME` can be added later behind `doctor` guidance, if users ask.
