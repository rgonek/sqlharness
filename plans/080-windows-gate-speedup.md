# 080 — Windows gate speedup and test cleanup

**Goal:** Shorten the local Windows gate and remove test scaffolding that has no production value, without weakening any assertion the gate depends on.

**Status:** approved in chat on 2026-10-16. Measured on branch `opt-windows-gate-2` (worktree `opt-windows-gate-2-2`).

**Measured baseline (quiet machine, from `plans/history.md`, plan 076):** `verify: OK stages=6 2m25s` — ui-install skipped; ui-check 36,9s; restore 10,8s; build 14,7s; test 1m22s; format skipped. On the contended box used for the plan the same stages measured 2m40s / 43,1s / 19,3s / ~35s / 184s; treat contended numbers as a signal about *relative* weight only.

**What this plan does not do:** it does not adopt Bun. Bun 1.4's global virtual store (`--linker=isolated`) needs symlinks and this machine cannot create them (`New-Item -ItemType SymbolicLink` fails with "Administrator privilege required"), and `bun install --frozen-lockfile` cannot consume this repo's `package-lock.json` (`Could not resolve package 'tslib' in lockfile during migration`). The only remaining Bun gain is roughly one second of script spawning.

## Global constraints

- `AGENTS.md` governs: plans index updated when work lands, no database writes, no credential or classification changes, README serves humans, interface contracts stay in tool descriptions and capabilities.
- Verification is `just verify` on the final product tree per `docs/agents/verification.md`. `just verify-linux` is optional and stays working; it is not part of completion criteria.
- `.editorconfig` sets `insert_final_newline = false`. Removing a trailing newline is a real diff for `dotnet format`; do not "fix" it by adding one.
- The gate is agent-readable first: every stage writes its own log file and a green stage prints exactly one line. Any concurrency added must preserve that.
- `scripts/gate-output.ps1` is the single home for stage execution and stage-line formatting. `scripts/verify.ps1`, `scripts/verify-linux.ps1` and `tests/scripts/gate-scripts.Tests.ps1` (Pester) all consume it.
- No test may lose an assertion to gain time. Removing coverage needs a stated reason; making a test faster does not.

## Review Focus

Conditions that are plausible and easy to get wrong here. Each is pinned by a test in the owning task.

1. **One project fails while the other passes.** The parallel test stage must report the failing project by name and must not exit 0 because `LASTEXITCODE` held only the last-waited process. Pinned in Task 3.
2. **The UI check fails but the build fails too.** With overlapping stages the report must still name `ui-check` and print its log tail, not just the build's. Pinned in Task 4.
3. **A skipped stage still prints a line.** `ui-install` reports `skipped` and `format` reports `skipped` when no C# file changed; overlap must not make a skipped stage look like a passing one. `ui-install` stays foreground for exactly this reason. Pinned in Task 4.
4. **`dotnet test` on a solution whose project list grows.** Both gate scripts name projects explicitly, so a third test project would silently not run. The parity tests must fail when a test project exists that neither gate names. Pinned in Task 3.
5. **Windows and Linux gates drift after the stage reshape.** Every command both gates run must stay comparable; the parity tests compare the two gates literally today, so a new asymmetric command would pass unnoticed if the comparison is loosened. Pinned in Task 3.
6. **A reader test seeds through the production writer.** `JournalSeed` bulk seeding (Task 2) must be used only by tests that read, never by a test that asserts what the writer wrote. Pinned in Task 2.

---

### Task 1: Make the flaky housekeeping test report its cause

`DashboardHost.RunAsync` catches every exception (`src/SqlHarness.Dashboard/DashboardHost.cs:84`) and returns `SqlHarnessExitCode.LocalStorage`, so a housekeeping pass that throws ends the loop without surfacing anything. `DashboardHousekeepingTests.External_journal_commit_during_matching_refreshes_the_idle_window` passes `TextWriter.Null` as `options.Error`, so that message is discarded, and its central assertion

```csharp
Assert.Same(nextPass, await Task.WhenAny(run, nextPass));
```

compares two independently completing tasks. When the loop finishes first the failure reads `Assert.Same() Failure / Expected: Task<VoidTaskResult> { Status = WaitingForActivation } / Actual: Task<int, <RunAsync>d__1> { Status = RanToCompletion }` and names no cause.

This is a **known recurring** flake: `plans/history.md` and `plans/076-partial-matrix-journal-runs.md` both record the identical failure as an "unrelated race", and it failed again on this branch on 2026-10-16. The exact trigger was not identified; a faithful clone passed 40 targeted runs, and instrumenting the manual clock showed the pass that consumes the trigger always reads the clock *after* the advance, refuting the obvious stale-timestamp explanation. **This task does not claim to fix the flake.** It removes the reason the next occurrence is undiagnosable, so the trigger can be identified from one run instead of a fresh investigation.

**Files:**
- Modify: `tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs:374-426`
- Modify: `tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs:73-85` (`Options` helper — add an optional error sink parameter)

**Interfaces:**
- Consumes: `DashboardHostOptions` is a positional record, so `Error` is an `init` property settable through `with`.
- Produces: `private static DashboardHostOptions Options(TempHome home, TimeSpan? idle, TimeProvider time, Passes passes, SqlHarnessConfig? config = null, TextWriter? errors = null)` — when `errors` is non-null it replaces the `TextWriter.Null` passed as `Error`. Every other caller is unaffected because the parameter defaults to null.

- [x] **Step 1: Add the error sink parameter to the `Options` helper**

Change the signature at `DashboardHousekeepingTests.cs:73` to the one in the Interfaces block and change the `TextWriter.Null` in the `Error` position (line 76) to `errors ?? TextWriter.Null`. Leave `Output` as `TextWriter.Null`.

- [x] **Step 2: Rewrite the assertion so the failure names the cause**

In `External_journal_commit_during_matching_refreshes_the_idle_window`, replace lines 406-422 with:

```csharp
var errors = new StringWriter();
var run = DashboardHost.RunAsync(Options(home, TimeSpan.FromMinutes(2), clock, passes, config, errors) with
{
    AgentHomes = homes,
    AgentDirectoryProbe = Probe,
}, CancellationToken.None);
await passes.MoreAsync();
clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(50)));
Volatile.Write(ref triggerWrite, 1);
await wroteDuringScan.Task.WaitAsync(TimeSpan.FromSeconds(20));
await passes.MoreAsync();
Assert.Single(Journal.JournalDb.Rows(home.DatabasePath, "SELECT operation_id FROM operation_agent_links"));

var nextPass = passes.MoreAsync();
clock.Advance(TimeSpan.FromSeconds(10));
// Passes.MoreAsync() already bounds itself at 20 s, so awaiting it is the wait; asserting
// which task finished first only added a race and hid the reason behind an Assert.Same diff.
await nextPass;
var code = run.IsCompleted ? await run : -1;
Assert.False(run.IsCompleted,
    $"the in-cycle external commit starts a new idle window; host exited with {code}: {errors}");
```

The message deliberately carries both the host's exit code and whatever the host wrote to `Error`, which is where `DashboardHost.cs:88` puts the type name of a swallowed exception.

- [x] **Step 3: Verify the test still passes and still guards the same behaviour**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~DashboardHousekeepingTests'`
Expected: 14 passed, 0 failed.

- [x] **Step 4: Verify the message actually appears when the loop is down**

Temporarily change the `await nextPass;` line to `await nextPass.WaitAsync(TimeSpan.FromMilliseconds(1));`, run the same filter, and confirm the failure text mentions `host exited with`. Then revert that one line. This proves the new message is wired, not just written.

- [x] **Step 5: Commit**

```bash
git add tests/SqlHarness.Tests/Dashboard/DashboardHousekeepingTests.cs
git commit -m "test: report why the housekeeping loop ended in the idle-window race"
```

---

### Task 2: Seed bulk journal rows without the per-call writer cost

`ActivityJournal` opens a **new, non-pooled** SQLite connection per journal call (`src/SqlHarness.Core/Journal/ActivityJournal.cs:641-654`, `Pooling = false`) and commits each call. `JournalSeed.Operation` makes three calls (`Begin`, `Complete`, `RecordEmission`), so seeding 240 operations costs about 92 ms per operation — 23,2 s inside a single test, `Starred_list_pages_with_the_id_cursor_without_gaps` at `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs:1365`. Measured class total for `JournalReaderTests` is 52,9 s, the largest in the suite.

`JournalSeed.Annotate` already shows the established pattern for this fixture: a raw pooled `SqliteConnection` with a single `INSERT ... SELECT`, with a comment recording the measured saving.

**Files:**
- Modify: `tests/SqlHarness.Tests/Dashboard/JournalSeed.cs` (add a bulk helper after `Annotate`, line 95)
- Modify: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs:1365-1390` and the `Compare_pages_have_no_gaps_...` test

**Interfaces:**
- Produces: `public static void Operations(string databasePath, int count, Action<int, long>? onSession = null)` in `SqlHarness.Tests.Dashboard.JournalSeed`. Opens one pooled connection, wraps the whole loop in a single `BEGIN`/`COMMIT`, and inserts `count` sessions with session keys `cli:000` … `cli:{count-1:D3}`, each with one completed `query` operation and `last_seen` = `JournalSeed.HostStarted.AddSeconds(count - 1)` so ordering by time matches ordering by id. `onSession(index, sessionId)` is invoked inside the transaction after each session insert so callers can annotate or link.

- [x] **Step 1: Write the bulk helper**

Add to `JournalSeed.cs`. Insert into `sessions` and `operations` with the column names taken from `JournalSchema` — read `src/SqlHarness.Core/Journal/JournalSchema.cs` for the exact current column list of `operations` and use only columns the reader tests select. Use `using var command = connection.CreateCommand()` with `command.Transaction` set, executed once per row (a prepared statement reused in the loop, not string-concatenated SQL). Commit once at the end.

- [x] **Step 2: Switch the id-cursor test to the bulk helper**

Replace the `for` loop at `JournalReaderTests.cs:1371-1376` with:

```csharp
JournalSeed.Operations(home.DatabasePath, 240, (index, id) =>
{
    if (index % 2 == 0)
        JournalSeed.Annotate(home.DatabasePath, $"cli:{index:D3}", starred: true);
});
```

Keep the existing assertions (`Assert.Equal(120, seen.Count)` and the distinctness check) and the existing comment about two full pages proving the cursor.

- [x] **Step 3: Switch the compare-paging test to the bulk helper the same way**

Apply the identical replacement in `Compare_pages_have_no_gaps_and_versions_use_all_retained_history_before_paging`, keeping its own assertions untouched.

- [x] **Step 4: Prove the seeding is still correct before trusting the time**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~JournalReaderTests'`
Expected: 64 passed, 0 failed. If a reader assertion fails, the bulk rows do not match what the writer produced — fix the helper, do not relax the assertion.

- [x] **Step 5: Measure the change**

Run the same filter with `--logger "trx;LogFileName=jr.trx" --results-directory D:/temp/opencode/trx-seed`, then sum `UnitTestResult/@duration` for the class. Expected: class total well below the recorded 52,9 s. Record both numbers in `plans/080-verification-evidence.md` (Step 8).

- [x] **Step 6: Prove no writer test uses the bulk helper**

Run: `Select-String -Path tests/SqlHarness.Tests/**/*.cs -Pattern 'JournalSeed\.Operations'`
Expected: matches only in `JournalReaderTests.cs`. A match in a test that asserts what `ActivityJournal` wrote is a Review Focus item 6 violation — move that test back to `seed.Operation`.

- [x] **Step 7: Format and build**

Run: `pwsh ./scripts/format-changed.ps1 -Fix` then `dotnet build SqlHarness.sln --no-restore -warnaserror`
Expected: 0 warnings, 0 errors.

- [x] **Step 8: Write the evidence note**

Create `plans/080-verification-evidence.md` with the before/after `JournalReaderTests` class totals, the `dotnet test` command used, and the resulting date.

- [x] **Step 9: Commit**

```bash
git add tests/SqlHarness.Tests/Dashboard/JournalSeed.cs tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs plans/080-verification-evidence.md
git commit -m "test: seed reader fixtures in one transaction"
```

---

### Task 3: Run the two test projects concurrently

`dotnet test SqlHarness.sln` runs test projects one after another. Measured: Core 95,9 s then MCP 87,9 s = 183,8 s sequential; the same two as concurrent processes: 95,2 s. On the quiet-machine gate this is `test 1m22s` toward roughly the slower project alone.

**Files:**
- Modify: `scripts/gate-output.ps1` (add a parallel stage helper after `Invoke-GateStage`, line 130)
- Modify: `scripts/verify.ps1:76-79`
- Modify: `scripts/verify-linux.ps1:248-251`
- Modify: `.github/workflows/ci.yml` (`dotnet test` step)
- Modify: `tests/SqlHarness.Tests/GateParityTests.cs` (see the list in Step 4)
- Test: `tests/scripts/gate-scripts.Tests.ps1` (Pester, Windows-only in CI)

**Interfaces:**
- Produces: `Invoke-ParallelGateStage` in `scripts/gate-output.ps1` with signature
  `Invoke-ParallelGateStage -Name <string> -LogDirectory <string> -Project <string[]> -Arguments <string[]> -Skipped -SkipReason <string>`.
  It starts one `dotnet test <project> <Arguments>` process per entry of `-Project`, waits for all of them, writes each project's output to `<LogDirectory>/<Name>-<leaf>.log`, returns a single object shaped like `Invoke-GateStage`'s (`Name`, `ExitCode`, `Seconds`, `LogPath`, `Skipped`, `SkipReason`) whose `ExitCode` is the first non-zero project exit code and whose `LogPath` is the log of the first failing project. `-Skipped` returns the same skipped object `Invoke-GateStage` returns. Projects are listed explicitly, never discovered by glob.

- [x] **Step 1: Write the failing Pester test**

In `tests/scripts/gate-scripts.Tests.ps1`, add a test that dot-sources `scripts/gate-output.ps1`, calls `Invoke-ParallelGateStage` with two fake projects that both exit 0, and asserts `ExitCode` is 0 and both per-project logs exist. Then add a second case with one fake project exiting 3 and assert `ExitCode` is 3 and `LogPath` names that project's log. Fake projects are `.cmd` shims on `PATH` that `exit %1%`, matching the pattern already used by `setup-local-*.Tests.ps1`.

- [x] **Step 2: Run it to verify it fails**

Run: `powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts/gate-output.Tests.ps1 -EnableExit"`
Expected: FAIL — `Invoke-ParallelGateStage` is not a recognized command.

- [x] **Step 3: Implement `Invoke-ParallelGateStage`**

Start every process with `System.Diagnostics.Process` exactly as `Invoke-NativeStageCommand` does (redirect both pipes, drain both concurrently — the comment at `gate-output.ps1:46-48` records the deadlock that a serial drain caused). Wait for all, then write the logs. Return the object in the Interfaces block. Keep `Invoke-GateStage` untouched so `ui-install`, `ui-check`, `build` and `format` keep their current reporting.

- [x] **Step 4: Run it to verify it passes**

Run the Pester command from Step 2. Expected: PASS.

- [x] **Step 5: Rewire `verify.ps1`**

Delete the `Add-Stage -Name 'test'` line at `scripts/verify.ps1:79` and replace it with a single `Add-Stage` whose action calls `Invoke-ParallelGateStage` for `SqlHarness.sln`'s two test projects with the arguments `--no-build --filter FullyQualifiedName!~Integration`. Print the per-project exit codes into the stage log so a failure names the project (Review Focus 1).

- [x] **Step 6: Rewire `verify-linux.ps1`**

Replace the `test` stage lines at `verify-linux.ps1:248-251` with a bash block that starts both `dotnet test <project> ...` in the background, records each PID, `wait`s for each and exits non-zero if either failed. Keep the `--logger "console;verbosity=normal"` on both lines, because `GateParityTests.The_two_gates_have_equal_test_commands_modulo_the_linux_logger` compares the two gates literally.

- [x] **Step 7: Rewire `ci.yml`**

Replace the single `dotnet test` step with two `dotnet test` steps, each naming one project and keeping `--no-build --filter "FullyQualifiedName!~Integration" --logger trx --results-directory TestResults`. Two steps rather than one shell block, so the GitHub job summary names the failing project.

- [x] **Step 8: Update the parity tests**

In `GateParityTests.cs` make these changes:

- `ExpectedVerbs` (line 7) becomes `["build", "test", "format"]` and `StageVerbs` (line 456) returns distinct verbs in first-occurrence order, because a project list now yields `test` twice. Task 4 already made `ExpectedVerbs` `["build", "test", "format"]`; here it is the `StageVerbs` dedupe that is new.
- Replace `The_two_gates_have_equal_test_commands_modulo_the_linux_logger` (line 92) with a comparison over both per-project test commands: every Windows `dotnet test` command must equal its Linux counterpart plus `--logger "console;verbosity=normal"`, and both lists must name the same two projects.
- Add a test that fails when the solution grows a test project neither gate names: read `SqlHarness.sln` for `tests/**.csproj` entries and assert each one appears in `scripts/verify.ps1` and `scripts/verify-linux.ps1` (Review Focus 4).
- Update `The_extractor_returns_the_expected_commands_for_the_real_gate_files` (line 229) to the two new per-project command strings.
- Leave `The_windows_test_command_does_not_include_a_logger` and `The_only_logger_occurrence_is_in_the_linux_test_command` asserting over every `dotnet test` command in each file rather than the first match.

- [x] **Step 9: Run the parity and script tests**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests|FullyQualifiedName~VerifyLinuxScriptTests'`
Expected: all pass. Then run the Pester command from Step 2 over `tests/scripts` and expect it to pass.

- [x] **Step 10: Measure both orders before committing**

Run the two projects sequentially, then concurrently, on the quiet machine, three runs each, and record the medians in `plans/080-verification-evidence.md`. If the concurrent median is not at least 15 % better, stop and report the measurement instead of landing a change that only adds contention — this is the configuration in which the housekeeping flake in Task 1 appeared.

- [x] **Step 11: Commit**

```bash
git add scripts/gate-output.ps1 scripts/verify.ps1 scripts/verify-linux.ps1 .github/workflows/ci.yml tests/SqlHarness.Tests/GateParityTests.cs tests/scripts/gate-output.Tests.ps1 plans/080-verification-evidence.md
git commit -m "build: run the two test projects concurrently"
```

---

### Task 4: Fold restore into build and overlap the UI check

`dotnet restore SqlHarness.sln` costs 10,8 s quiet (19,3 s contended) as its own stage, and `dotnet build` performs the same restore implicitly as a no-op when nothing changed. `ui-check` (36,9 s quiet) needs no .NET artifacts and currently runs before `restore` for no reason.

**Files:**
- Modify: `scripts/verify.ps1:76-79`
- Modify: `scripts/verify-linux.ps1:233-246`
- Modify: `.github/workflows/ci.yml` (`dotnet restore` step)
- Modify: `tests/SqlHarness.Tests/GateParityTests.cs`
- Modify: `tests/SqlHarness.Tests/VerifyLinuxScriptTests.cs`
- Modify: `tests/scripts/gate-scripts.Tests.ps1`
- Modify: `docs/agents/verification.md` if it names the four stages

**Interfaces:**
- Produces: `Start-BackgroundGateStage -Name <string> -LogDirectory <string> -FilePath <string> -Arguments <string[]>` and `Complete-BackgroundGateStage -Name <string> -LogDirectory <string> -Process <System.Diagnostics.Process>` in `scripts/gate-output.ps1`. Both take the native command directly rather than a scriptblock, because the only overlapped stage is `npm run check`, a native command — a scriptblock would force the caller to re-implement output capture. `Start-` launches the process with both pipes redirected and returns it; `Complete-` calls `WaitForExit`, drains both pipes, writes `<LogDirectory>/<Name>.log`, prints the same one-line stage summary `Get-StageLine` produces today, prints `Get-StageFailureText` on failure, and returns the same object shape as `Invoke-GateStage`. There is no skipped variant: `ui-install` stays a foreground stage, so the only stage that can be skipped (`format`) never overlaps.

- [x] **Step 1: Update the parity expectations first, so the change is driven by a failing test**

In `GateParityTests.cs` change `ExpectedVerbs` to `["build", "test", "format"]` and update:
- `Every_local_gate_builds_and_formats_identically` (line 19) — drop the `--no-restore` assertion and assert instead that no gate passes `--no-restore` to `build`.
- Delete `The_two_gates_have_identical_restore_commands` (line 65).
- `Every_gate_installs_and_checks_the_ui_before_restore` (line 348) — look for `dotnet build` instead of `dotnet restore`; `UiInstallMarker` stays as is.
- `The_extractor_returns_the_expected_commands_for_the_real_gate_files` (line 229) — new expected strings.

In `VerifyLinuxScriptTests.cs` rename `Linux_gate_runs_the_four_ci_stages_in_order` to `..._the_three_ci_stages_in_order` and expect `build`, `test`, `format`.

- [x] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests|FullyQualifiedName~VerifyLinuxScriptTests'`
Expected: FAIL on the stage list and the restore assertions.

- [x] **Step 3: Fold restore into build in `verify.ps1`**

Delete the `Add-Stage -Name 'restore'` line (`scripts/verify.ps1:77`) and change the build line (78) to `dotnet build SqlHarness.sln -warnaserror`.

- [x] **Step 4: Fold restore into build in `verify-linux.ps1` and `ci.yml`**

Delete the `restore` stage (`scripts/verify-linux.ps1:238-241`) and drop `--no-restore` from its build line (245). Delete the `dotnet restore` step from `ci.yml` and drop `--no-restore` from its `dotnet build` step.

- [x] **Step 5: Add the background stage helpers to `gate-output.ps1`**

Implement the two signatures in the Interfaces block. Reuse `Get-StageLine`, `Get-StageFailureText` and `Format-Duration` so a background stage prints exactly the same line as a foreground one.

- [x] **Step 6: Start `ui-check` before `build` and join it after**

In `verify.ps1` start `ui-check` with `Start-BackgroundGateStage -Name 'ui-check' -FilePath 'npm' -Arguments @('run', 'check', '--prefix', 'src/SqlHarness.Dashboard/ui')`, then run `build` as a foreground `Add-Stage`, then call `Complete-BackgroundGateStage` for `ui-check`. The join must happen before the `test` stage starts, because `test` is the memory-hungry one and the box has 8 logical cores. Report `ui-check` after `build` in the stage lines and say so in the script's `.DESCRIPTION`: the stage order is no longer execution order.

If `ui-check` failed, its stage line and log tail must still print even when `build` also failed — print both, then exit with the build's code (Review Focus 2). Make `ui-install` stay a foreground stage so its skip reason is reported before anything starts.

- [x] **Step 7: Mirror the overlap in `verify-linux.ps1`**

The Linux gate runs stages inside WSL through one bash script per stage, so overlapping needs the `ui-check` script to run in the background inside the `build` stage's bash block: `npm run check ... &`, then the build, then `wait`. Record in a comment that this couples the two stages inside one WSL invocation and is why the Linux gate's stage lines stay sequential.

- [x] **Step 8: Run the parity, script and gate-output tests**

Run the `dotnet test` command from Step 2, then `powershell.exe -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"`.
Expected: all pass.

- [x] **Step 9: Update `docs/agents/verification.md` and any surface that names the stages**

Run: `Select-String -Path README.md,docs/agents/*.md,docs/mcp.md -Pattern 'four.*stage|restore, build, test|cztery'`
Fix every hit so the documentation matches the gate, and keep `GateParityTests.The_verification_contract_uses_the_justfile_rather_than_the_raw_scripts` (line 312) passing.

- [x] **Step 10: Measure**

Run `just verify` three times on the quiet machine and record the stage lines in `plans/080-verification-evidence.md`. Compare against the 2m25s baseline: expect the restore stage gone and `ui-check` overlapping `build`.

- [x] **Step 11: Commit**

```bash
git add scripts/verify.ps1 scripts/verify-linux.ps1 scripts/gate-output.ps1 .github/workflows/ci.yml tests/SqlHarness.Tests/GateParityTests.cs tests/SqlHarness.Tests/VerifyLinuxScriptTests.cs tests/scripts/gate-scripts.Tests.ps1 docs/agents/verification.md plans/080-verification-evidence.md
git commit -m "build: fold restore into build and overlap the UI check"
```

---

### Task 5: Drop the test that only tests the test harness

`tests/SqlHarness.Tests/PlatformFactAttributesTests.cs` asserts that `WindowsFactAttribute` skips off Windows and `UnixFactAttribute` skips on Windows — two cases testing xunit's own attribute wiring, with no production behaviour behind them. Measured cost 0,00 s. `PlatformFactAttributes.cs` itself stays: the attributes are used by other tests.

**Files:**
- Delete: `tests/SqlHarness.Tests/PlatformFactAttributesTests.cs`

**Interfaces:**
- None. Nothing else references the class.

- [x] **Step 1: Confirm nothing references it**

Run: `Select-String -Path src/**/*.cs,tests/**/*.cs -Pattern 'PlatformFactAttributesTests'`
Expected: no matches.

- [x] **Step 2: Delete the file**

- [x] **Step 3: Confirm the attributes are still used**

Run: `Select-String -Path tests/SqlHarness.Tests/**/*.cs -Pattern '\[WindowsFact|\[UnixFact' | Measure-Object`
Expected: more than zero matches. If zero, the attributes in `PlatformFactAttributes.cs` are dead too and the plan needs a second decision rather than a silent deletion.

- [x] **Step 4: Run the suite to confirm the count drops by two**

Run: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName!~Integration'`
Expected: passed count is 2 lower than the count recorded in `plans/080-verification-evidence.md` before this task, 0 failed.

- [x] **Step 5: Commit**

```bash
git add -A tests/SqlHarness.Tests/PlatformFactAttributesTests.cs
git commit -m "test: drop the tests that only exercise the xunit attributes"
```

---

## Execution order

1. Task 1 (independent, smallest)
2. Task 2 (independent of the gate work)
3. Task 5 (independent)
4. Task 4 (gate shape)
5. Task 3 (parallel tests — depends on Task 4 having removed the restore stage, per its Step 8)

Run `just verify` once after Task 4 and once after Task 3; tasks 1, 2 and 5 are verified by their own focused runs.

## Not in this plan

- Bun adoption. Measured blocked; see the header.
- `McpStdioProcessTests.Published_single_file_server_passes_the_stdio_smoke_on_this_rid` publishes a single-file binary per run and cost 34,5 s of the MCP project's 44,8 s. That is the next largest single item in the gate and deserves its own decision about caching the published artifact.
- Wrapping the housekeeping pass body in `catch` so a transient SQLite failure cannot kill the loop permanently. The comment at `src/SqlHarness.Dashboard/DashboardHost.cs:275-276` promises "a failed pass is retried at the next interval", but that protection currently covers only `JournalRetention.Run`. This is a product behaviour change and is recorded here, not taken.