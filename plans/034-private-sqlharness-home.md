# Plan 034: SQLHarness home is created private on Unix

> Read current source before editing. Inspect drift since `cadde67` and adapt paths within this intent. A changed file alone is not a STOP condition. Revised 2026-10-11: replaces the per-file creation-mode design with one private home directory.

## Status

- **Priority**: P3 (only matters on a shared Unix host)
- **Effort**: S
- **Risk**: LOW
- **Depends on**: —
- **Planned at**: `729d88f`; revised at `cadde67`, 2026-10-11

## Why this matters

Snapshots, compare/plan artifacts, Query Store text and the activity journal live under the SQLHarness home (`~/.sqlharness` or `SQLHARNESS_HOME`). On a shared Unix host another user could read them if the home is group/other-readable.

Other users cannot traverse a directory with mode 0700, so the modes of files below it do not matter to them. One private home therefore protects every current and future writer. The earlier design (0600 creation for every file, SQLite WAL/SHM lifecycle, per-path doctor checks) solves the same problem at every write site and must be repeated for each new writer.

## Current state

- `src/SqlHarness.Core/SqlHarnessPaths.cs` resolves `Home` and the subdirectories.
- `src/SqlHarness.Core/Journal/OwnerOnlyFiles.cs` already restricts the journal data directory and DB/WAL/SHM files. Keep it; it is harmless and covers a broad custom home.
- `DoctorCommand.cs` produces the offline installation report.

## Scope

In scope: `SqlHarnessPaths.cs` (or one helper it calls), `DoctorCommand.cs`, tests, one README/AGENTS sentence.

Out of scope: per-file creation modes, Windows ACLs, chmod of directories SQLHarness did not create.

## Steps

1. When SQLHarness creates the home directory on Unix, create it with `UnixFileMode.UserRead | UserWrite | UserExecute` (the `Directory.CreateDirectory(path, mode)` overload), guarded for CA1416. Every writer must go through the path that ensures the home exists; find the creation sites with `rg "CreateDirectory" src/SqlHarness.Core`.
2. An existing home is left as is. `doctor --json` reports a warning when the home has any group/other permission bit, with `chmod 700 '<path>'` guidance. Doctor never changes modes.
3. Tests (Unix only, reported as skipped elsewhere): a fresh home is 0700; an existing 0755 home is unchanged and doctor warns; a 0700 home gives no warning.
4. Docs: "On Unix, SQLHarness creates its home with mode 0700; `doctor` warns when an existing home is broader."

## Verification

- `dotnet build SqlHarness.sln -warnaserror` — 0 warnings, CA1416 clean.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Doctor|FullyQualifiedName~SqlHarnessPaths|FullyQualifiedName~Journal"` — pass.
- Final `scripts/verify.ps1` → `verify: OK`; `scripts/verify-linux.ps1` → `verify-linux: OK` (the Unix tests must run there).

## Done criteria

- [x] Fresh home is 0700 on Linux; existing homes are never chmodded.
- [x] Doctor warns on a broad home.
- [x] Journal owner-only behaviour unchanged.
- [x] Both gates pass; `plans/README.md` row updated.

## Implementation notes

- `HomeDirectory.EnsureFor` is the single home-creation path. It is a no-op outside the
  home, so writers that work on a caller-supplied root (tests, `--root`) are unaffected.
- Only the directory it creates gets the mode: `Directory.CreateDirectory(path, 0700)`
  applies the mode to the leaf and default modes to intermediate ancestors. That is why
  the home must be created explicitly before any subdirectory. Verified on Linux
  (.NET 10): `CreateDirectory(home/data, 0700)` leaves `home` at the umask default, and
  re-calling it on an existing broad home does not change the mode.
- Wired sites (Core): `SqlHarnessConfigWriter.Write`, `ActivityJournal.Initialize`,
  `ArtifactDirectoryPublisher.Publish`, `ArtifactStoragePreflight.Check`,
  `SnapshotStore.Save`. Dashboard: `DashboardAutostart.TryStart`,
  `DashboardLock.TryAcquire`, `DashboardPrices.RefreshAsync`.
- Doctor gained `homePath`, `homePresent`, `homePrivate` (null off Unix or when the home
  is missing) and `warnings`; it stays offline and creates nothing.
- The Unix-only tests early-return on Windows and execute on the Linux gate.

## STOP conditions

- A writer creates files under the home before the home-creation path runs (for example a custom `SQLHARNESS_HOME` subdirectory created elsewhere). Report the path.
