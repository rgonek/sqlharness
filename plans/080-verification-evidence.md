# Plan 080 verification evidence

Evidence note for plan 080 ([080-windows-gate-speedup.md](080-windows-gate-speedup.md)), appended per task. Each section
records the exact command, the machine it ran on, and the numbers it produced, so a later task can extend it without
re-running an earlier measurement. Dates are local dates on the machine named in the section.

## Task 2 — bulk journal seeding in the reader fixtures (2026-10-16)

**Change:** `JournalSeed.Operations` and `JournalSeed.Compares` seed the journal through one pooled connection and one
transaction instead of paying `ActivityJournal`'s per-call non-pooled connection and commit
(`src/SqlHarness.Core/Journal/ActivityJournal.cs:641-654`). Only the two reader tests that needed volume switched to them;
both kept every assertion they had. No `src/` file changed.

**Command (identical before and after):**

```bash
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build \
  --filter 'FullyQualifiedName~JournalReaderTests' \
  --logger "trx;LogFileName=jr.trx" --results-directory <dir>
```

Class total is the sum of `UnitTestResult/@duration` over the 64 results in the resulting `jr.trx`.

| | tests | failed | `JournalReaderTests` class total |
|---|---|---|---|
| before (`trx-seed-before`, at `85d7062`) | 64 | 0 | **18,35 s** |
| after (`trx-seed`, Task 2 working tree) | 64 | 0 | **8,09 s** |

The two tests the task targeted, from the same two trx files:

| test | before | after |
|---|---|---|
| `Starred_list_pages_with_the_id_cursor_without_gaps` | 8,32 s | 0,23 s |
| `Compare_pages_have_no_gaps_and_versions_use_all_retained_history_before_paging` | 2,34 s | 0,13 s |

Those two tests were 10,66 s of the 18,35 s class total before and 0,36 s of 8,09 s after. The remaining class time is
per-test reader-construction cost that Task 2 does not touch: the slowest survivor,
`Dimension_stats_are_complete_filtered_and_keep_missing_values_separate_from_unknown_text`, costs 0,88 s both before and
after, and the next five cost 0,16–0,69 s each in both runs.

**Machine caveat.** The plan header records a 52,9 s `JournalReaderTests` class total and 23,2 s for the starred test
alone. That measurement was taken on a different (slower, contended) box. The table above is one machine before and
after on 2026-10-16, so only the ratio is meaningful: **the class total fell 56 %, and the two targeted tests fell
97 %.** The 52,9 s figure was not re-measured here and is not claimed to be reproduced.

**Whole-gate check.** `just verify` exited 0 on this working tree: `verify: OK  stages=6  9m45s`, with
`SqlHarness.Tests` 4,006 passed / 11 skipped and `SqlHarness.Mcp.Tests` 402 passed / 4 skipped. The box was heavily
contended during that run — `ui-check` alone took 6m14s against the 36,9 s quiet baseline recorded in the plan header —
so this run's stage totals are not comparable to the recorded 2m25s gate baseline. The class-level trx numbers above are
the measurement this task claims; the gate result only shows nothing else in the suite regressed.

## Task 4 — restore folded into build, UI check overlapped with the build (2026-10-16)

**Change:** `dotnet restore` is gone as a stage from `scripts/verify.ps1`, `scripts/verify-linux.ps1` and
`.github/workflows/ci.yml`; `dotnet build SqlHarness.sln -warnaserror` performs the same restore implicitly. The Windows
gate starts `npm run check` in the background, runs the build beside it and joins the check before the test stage, which
is the memory-hungry one on this box. No `src/` file changed.

**Command (three runs):**

```powershell
just verify                                   # run 1, default log directory
just verify -LogDirectory D:/temp/opencode/gate-t4-run2
just verify -LogDirectory D:/temp/opencode/gate-t4-run3
```

| | ui-install | build | ui-check | test | format | summary |
|---|---|---|---|---|---|---|
| plan baseline (quiet, 076) | skipped | 14,7s | 36,9s | 1m22s | skipped | `OK stages=6 2m25s` |
| run 1 | skipped | 20,1s | 5m51s | 1m51s | ok 55,3s | `OK stages=5 8m38s` |
| run 2 | skipped | 29,1s | 5m40s | **FAILED (exit 1)** | not reached | `FAILED stage=test exit=1 8m08s` |
| run 3 | skipped | 28,8s | 5m42s | 1m41s | ok 53,3s | `OK stages=5 8m16s` |

### The timings are not usable as a gate improvement

**The box was too contended to measure.** An unrelated long-running process
(`dotnet CivicLens.Importer.dll import-crju --resweep --all`, started 09:39 on 2026-10-16) held roughly one of the
eight logical cores continuously through all three runs; a 6-second CPU-delta sample during run 1 measured 10,5 of 48
available core-seconds consumed, of which that importer alone took 5,7. Another agent session was also active
(`plan-077-task-2` watcher processes) and another gate run was writing into the same default log directory at the same
time, which is how a stale `restore.log` from the pre-change gate shape appeared beside the new run's logs.

The decisive number is `ui-check`: **5m40s–5m51s against a 36,9 s quiet baseline, a 9–10× inflation**, and consistent
across three runs. The recorded 2m25s baseline and these totals are therefore not comparable, and **no speedup figure
is claimed from them.** What these runs *do* establish is structural, and is independent of contention:

1. **The restore stage is gone.** Three consecutive runs report `stages=5`, not 6, and no stage named `restore` appears.
2. **The UI check genuinely overlaps the build.** Each run wrote `build.log` and `ui-check.log` 5m11s–5m31s apart:
   run 1, build 19:59:42 / ui-check 20:05:13; run 2, 20:09:23 / 20:14:34; run 3, 20:19:51 / 20:25:04. A stage line
   cannot be written before the stage finishes, so the build demonstrably completed while the UI check was still
   running. The `ui-check` duration is measured from start to join, which is why it exceeds the build's.
3. **The report order is completion order.** `==> ui-check` prints after `==> build` even though it started first.

Re-measure on a quiet machine against the 2m25s baseline before quoting any figure.

### Final gate on the committed tree

```powershell
git rev-parse HEAD
# 1454e39c25bc25c82fea2680c7f4cac515938a8d
just verify -LogDirectory D:/temp/opencode/gate-t4-final
```

```
==> ui-install skipped (lockfile unchanged)
==> build ok 37,7s
==> ui-check ok 5m51s
==> test ok 1m37s
format: skipped (0 changed C# files)
==> format skipped (0 changed C# files)
verify: OK  stages=5  7m28s
===== just verify exit code: 0 =====
```

Exit 0, which is the contract in `docs/agents/verification.md`. `format` skips here because the change is committed;
the three measurement runs above paid for it because it was not. Note the build and the test stage are both inflated by
the contention described above.

### Failure paths, proved rather than asserted

`scripts/verify.ps1` was temporarily mutated to make both overlapped stages fail, then reverted:

```
==> ui-install skipped (lockfile unchanged)
==> build FAILED (exit 5)
==> ui-check FAILED (exit 1)
Unknown command: "/c"
full log: ...\ui-check.log
build-failure-marker
full log: ...\build.log
verify: FAILED  stage=build  exit=5  0,9s
===== verify.ps1 exit code: 5 =====
```

Both stage lines print, both log tails print, and the gate exits with the build's code (Review Focus 2). With only the
UI check failing, the gate printed `verify: FAILED stage=ui-check exit=1` and returned 1. The probe incidentally
confirmed `-FilePath 'npm'` resolves and really launches npm — npm itself rejected the `/c` argument the probe passed.
That resolution is **not** PATHEXT: `Process.Start` applies no PATHEXT, which is the whole reason a bare `npm` fails.
`Resolve-GateStageExecutable` asks `Get-Command -CommandType Application` for the candidates and keeps only the first
whose extension is `.exe`, `.com`, `.cmd` or `.bat`. On this machine that restriction is what picks
`...\nodejs-lts\current\bin\npm.cmd`; without it the first candidate is the extensionless bash shim
`C:\Users\rgone\.local\bin\npm`, which `CreateProcess` cannot launch. Nothing is resolved when no candidate has one of
those extensions, and the resolver throws.

The same four outcomes were checked directly in bash for the Linux gate's coupled stage, with `npm` and `dotnet` replaced
by shell functions: both pass → 0; build fails → 1; UI check fails → 7; both fail → 3 (the build's code), with
`==> ui-check FAILED` and `==> build FAILED` both on stderr.

### Run 2's test failure is a load flake, not a regression

Run 2 failed at `test` on
`PostgresParameterTests.Matrix_measure_sets_watch_and_snapshot_use_the_postgres_reference_scan`
(`Assert.NotEmpty() Failure: Collection was empty`, `PostgresParameterTests.cs:298`, 9 ms). It is unrelated to this
task: the `test` stage command line is byte-identical before and after, no `src/` file changed, and the UI check was
joined before `test` started so no npm process was alive during the suite. Evidence that it is intermittent under load:

- Run 1 and run 3, same tree, both green (4,006 passed / 11 skipped and the equivalent).
- The test alone passed 5/5 in a row (`286 ms`, `391 ms`, `477 ms`, `505 ms`, `448 ms`).
- The whole `SqlHarness.Tests` project re-run afterwards: `Passed!  - Failed: 0, Passed: 4004, Skipped: 11, Total: 4015,
  Duration: 1m6s` — the expected count (4,006 from the Task 2 note less the 2 tests Task 5 removed).

`plans/history.md` does not record this test as a known flake, so the next occurrence is still undiagnosed. That is a
pre-existing condition, not one this task introduced.

### Focused runs on this task

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests|FullyQualifiedName~VerifyLinuxScriptTests'
# Passed!  - Failed: 0, Passed: 48, Skipped: 0, Total: 48

pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"
# Passed: 76 Failed: 0
```

(The first Task 4 commit recorded 68 here against an actual 69. The fix round added 7 tests, so the number on the
committed tree is 76.)
```

The Pester suite must be run under `pwsh`, not `powershell.exe`: the brief's literal command starts Windows PowerShell
5.1, and every `scripts/*.ps1` carries `#Requires -Version 7.0`, so all 35 PowerShell-5.1 failures are
`ScriptRequiresException` raised before any test body runs. `.github/workflows/ci.yml` already runs this lane with
`shell: pwsh`.

### The Linux gate: the changed stage passes, the gate as a whole does not go green here

`just verify-linux` is optional and not part of the completion criteria, but the clone under `~/src` happened to be
provisioned on this box, so it was run. **The stage this task changed passes. The gate does not go green, for two reasons
that both predate the change.**

Three runs:

| | sync | sdk | node | ui-install | build (ui-check + build) | test | summary |
|---|---|---|---|---|---|---|---|
| run 1 | 7,5s | 1,0s | 0,4s | 27,5s | **FAILED (exit 1)**, empty output | not reached | `FAILED stage=build exit=1 8m27s` |
| run 2 | 0,7s | 0,6s | 0,3s | 0,4s | **ok 5m46s** | **FAILED (exit 1)**, empty output | `FAILED stage=test exit=1 9m00s` |
| run 3 | 0,5s | 0,6s | 0,4s | 0,4s | **FAILED (exit 1)**, empty output | not reached | `FAILED stage=build exit=1 1m44s` |

Run 2 is the data point that matters: the coupled `npm run check &` + `dotnet build` stage passed inside one WSL
invocation.

**The `wsl exited with 1` failures with an empty log are WSL dying, not this change.** The identical stage script, run
through the identical `Invoke-NativeStageCommand` path the gate uses, returned **exit 0 with 423 captured lines in
48,6 s**. The same script also passed standalone under WSL earlier (vitest 50 files / 260 tests). The failure is not
stage-specific — it hit `build` twice and the **untouched** `test` stage once, while the trivial stages (sync, sdk, node,
ui-install) passed every time — and it only ever hit the stages running a heavy workload. The WSL VM has 4 cores and
about 3,9 GB of RAM on a box that is already running another agent's gate.

**`dotnet test` has two deterministic Linux failures, pre-existing on this branch.** Running the `test` stage's bash
directly (so its output is visible) gives `Test Run Failed. Total tests: 4015 / Passed: 4006 / Failed: 2`, on:

- `Cli.DashboardCommandTests.Restart_launches_once_in_effective_home_and_announces_only_its_publication`
- `Dashboard.DashboardLifecycleTests.Shutdown_uses_guarded_loopback_post_without_token_in_url_and_does_not_follow_redirects`

Both fail 3/3 in isolation, and both fail identically with the gate clone checked out at `24c4070c` — the commit
immediately before this one. This commit changes no `src/` file and neither of those test files, so the Linux gate was
already red here before Task 4.

### Task 4 fix round — failure-surface repairs

Five findings, all on the failure surface, plus three in-file corrections. Three were proved by mutation: the guard is
shown to turn red when the thing it guards is broken, and green again when it is restored.

**I-1 — the UI check's tail printed twice.** `Complete-BackgroundGateStage` printed the failure tail and
`Stop-Gate` printed it again. Printing is now owned by the stage that owns the result: `Add-Stage` prints the tail when
its stage failed, `Complete-BackgroundGateStage` prints its own, and `Stop-Gate` prints the summary line and the exit
code and nothing else. Probe, build passing and the UI check failing:

```
==> ui-install skipped (lockfile unchanged)
==> build ok 0,1s
==> ui-check FAILED (exit 2)
UI-CHECK-TAIL-MARKER
full log: D:\temp\opencode\probe-i1\ui-check.log
verify: FAILED  stage=ui-check  exit=2  0,4s
===== exit code: 2 =====
```

`UI-CHECK-TAIL-MARKER occurrences: 1`, `full log:` lines for ui-check: 1. Before the fix the transcript carried the tail
twice, as recorded in the first Task 4 report's §9. With both stages failing, both tails still print and the build's code
still wins:

```
==> build FAILED (exit 5)
BUILD-TAIL-MARKER
full log: ...\build.log
==> ui-check FAILED (exit 2)
UI-CHECK-TAIL-MARKER
full log: ...\ui-check.log
verify: FAILED  stage=build  exit=5  0,5s     ===== exit code: 5 =====
```

**I-2 — an unresolvable `npm` threw outside any try/catch,** so the run produced no stage line, no log and no summary.
The start is now wrapped and the failure is reported as a failed `ui-check` stage. The resolver's fail-closed intent is
kept: the message still says a stage that cannot start must never look green. Probe, with `-FilePath` pointed at a
command that does not exist:

```
==> ui-install skipped (lockfile unchanged)
==> ui-check FAILED (exit 1)
'sqlharness-no-such-npm-9f3a' could not be resolved to an executable on PATH. A stage that cannot start must never look green.
full log: D:\temp\opencode\probe-i2\ui-check.log
verify: FAILED  stage=ui-check  exit=1  0,3s
===== exit code: 1 =====
```

`ui-check.log` was written with that message in it. The build is not attempted in this case, which matches the gate's
behaviour before the overlap: a UI stage that fails stops the run before the .NET stages.

**I-3 — CI had lost its shape protection.** Re-keying the UI-order anchor from `dotnet restore` to `dotnet build`
changed the anchor, not the scope. CI is now in the guarded set twice over: the build-shape theory takes a third
`InlineData` for `.github/workflows/ci.yml`, and a dedicated `Ci_builds_with_warnaserror_and_never_skips_or_duplicates_the_restore`
fact asserts the same three claims independently, so trimming CI out of the theory can no longer leave CI unguarded.
`No_gate_runs_a_standalone_restore_stage` (renamed from `Neither_local_gate_…`) iterates all three files.

| mutation | result |
|---|---|
| M3: `- run: dotnet build --no-restore -warnaserror` in `ci.yml` | **`Failed: 2`** — `Every_gate_builds_with_warnaserror_and_lets_the_build_restore(path: ".github/workflows/ci.yml")` and `Ci_builds_with_warnaserror_and_never_skips_or_duplicates_the_restore` |
| M8: `- run: dotnet restore` step re-added to `ci.yml` | **`Failed: 2`** — `No_gate_runs_a_standalone_restore_stage` and `Ci_builds_with_warnaserror_and_never_skips_or_duplicates_the_restore` |
| extra: `dotnet build` moved above `npm ci` and `npm run check` in `ci.yml` | **`Failed: 1`** — `Every_gate_installs_and_checks_the_ui_before_the_dotnet_build(path: ".github/workflows/ci.yml")` |

All three were reverted with `git checkout .github/workflows/ci.yml`; the suite returns to 53/53.

**I-4 — the two `Complete-` tests re-derived their expectations.** They now capture the information stream
(`Invoke-CapturingReport` merges stream 6 into the success stream and splits `InformationRecord` from the returned
value) and assert on what the helper printed: exactly one `==> build ok <duration>` line, the `-Stream` echo, the
failure line plus its tail, and a new test that the tail appears **exactly once**. Mutation proof — the print block was
replaced with equivalent no-ops that keep the file parsing (`$null = Get-StageLine …`, `if ($Stream) { $null = Get-Content … }`,
`if ($failure) { $null = $failure }`):

```
[-] reports a background stage with the same one-line summary a foreground stage gets   Expected: {1}
[-] replays the log with -Stream so a watcher still sees a background stage              Expected: {2}
[-] returns the failing exit code and prints the stage line and the failure tail        Expected: {2}
[-] prints the failure tail exactly once for a failed background stage                  Expected: {1}
Passed: 24 Failed: 4
```

All four went red, and the file returned to 28/28 on restore. A first attempt that deleted the lines outright only
produced `Error occurred in test script` — a parse failure that proves nothing about which assertions have teeth — so
the no-op mutation was used instead.

One assertion had to be loosened while being rewritten: the duration separator is the machine's culture decimal
separator, so `==> build ok 0,0s` on this box, and the regex is `\d+([.,]\d+)?s$` rather than `\d+(\.\d+)?s$`.

**I-5 — a UI-only Linux failure was reported as `stage=build`.** The bash already named both failures in the log, but
`Invoke-LinuxStage` named the wrapper stage in the summary, so a UI-only failure read `FAILED stage=build` carrying the
UI check's exit code. The coupled bash now writes `stage-failure: <name>` for **only** the command whose exit code it
returns, and `Invoke-LinuxStage` resolves the reported name through the new `Get-ReportedFailureStage`, which falls
back to the stage name when the log names nothing. Real bash in WSL with `npm` and `dotnet` replaced by shell
functions, each captured log then fed through `Get-ReportedFailureStage` and `Get-GateSummaryLine`:

| outcome | bash exit | markers written | reported | summary line |
|---|---|---|---|---|
| both pass | 0 | none | `build` | (no summary — the stage is green) |
| UI check only fails | 7 | `==> ui-check FAILED (exit 7)`, `stage-failure: ui-check` | **`ui-check`** | `verify-linux: FAILED  stage=ui-check  exit=7` |
| build only fails | 5 | `==> build FAILED (exit 5)`, `stage-failure: build` | `build` | `verify-linux: FAILED  stage=build  exit=5` |
| both fail | 3 | both `==> …` lines, `stage-failure: build` | `build` | `verify-linux: FAILED  stage=build  exit=3` |

Before the fix the second row read `FAILED stage=build exit=7`.

**m-5 — a `finally` so the shared state hashtable cannot be left half-populated.** `Complete-BackgroundGateStage`
removes its entry in a `finally` wrapping the whole join rather than removing it before the work, so a throw anywhere in
the drain cannot leave a stale entry holding two pending read tasks and collide with the next process id. `Start-`
wraps its registration too: a process that cannot be registered is killed rather than left running with nobody draining
its pipes. Covered by `removes its state even when the join throws`.

**m-2 and m-3 corrected in this file:** the `npm` resolution is attributed to the deliberate `.exe/.com/.cmd/.bat`
restriction rather than to PATHEXT (see above), and the Pester count is 76.

### Verification on the fix-round tree

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests|FullyQualifiedName~VerifyLinuxScriptTests'
# Passed!  - Failed: 0, Passed: 53, Skipped: 0, Total: 53

pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"
# Passed: 76 Failed: 0

just verify -LogDirectory D:/temp/opencode/gate-t4-fix1
# ==> ui-install skipped (lockfile unchanged)
# ==> build ok 19,8s
# ==> ui-check ok 6m03s
# ==> test ok 1m42s
# ==> format ok 57,5s
# verify: OK  stages=5  8m43s
# ===== just verify exit code: 0 =====
```

The stage times are contended for the same reason as the Task 4 measurements above and carry no claim.

## Task 3 — the two test projects run at once (2026-10-16)

**Change:** `dotnet test SqlHarness.sln` ran the two test projects one after another. Both gates now
name the two projects explicitly and start them at the same time: `scripts/verify.ps1` through the
new `Invoke-ParallelGateStage` in `scripts/gate-output.ps1`, `scripts/verify-linux.ps1` by backgrounding
both `dotnet test` commands inside its test stage's bash block, and `.github/workflows/ci.yml` with one
step per project. No `src/` file changed.

### Step 10: sequential against concurrent, both orders, on this box

Sixteen runs, back to back, alternating which arm goes first (`S C C S S C C S`) so a drifting machine
cannot favour one of them. Eight runs per arm rather than the three the plan asks for, because one
failure appeared in the first eight and a bigger sample was worth the wall clock.

The sequential arm runs `dotnet test <project>` for each project in turn, timed from the start of the
first to the end of the second. The concurrent arm calls the real `Invoke-ParallelGateStage` with the
same two projects and the same `--no-build --filter FullyQualifiedName!~Integration` arguments, timed
across the whole call, so what is measured is the code path the gate now runs. Both arms read the
per-project result out of the stage log.

```powershell
# sequential, per project, in order
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter FullyQualifiedName!~Integration
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-build --filter FullyQualifiedName!~Integration
# concurrent, through the real helper
Invoke-ParallelGateStage -Name test -LogDirectory <dir> -Arguments @('--no-build','--filter','FullyQualifiedName!~Integration') `
    -Project @('tests/SqlHarness.Tests/SqlHarness.Tests.csproj','tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj')
```

| arm | runs, seconds | median |
|---|---|---|
| sequential | 153,9 · 129,3 · 128,2 · 132,3 · 143,2 · 132,8 · 129,2 · 127,3 | **130,8 s** |
| concurrent | 81,3 · 83,7 · 86,9 · 84,5 · 85,8 · 82,7 · 90,2 · 82,7 | **84,1 s** |

**Improvement 35,7 %, against the 15 % the plan sets as the bar for landing this. The change is
landed.** Every concurrent run beat every sequential run; the two distributions do not overlap, so the
verdict does not depend on which run of either arm is picked.

**Machine.** The foreign `CivicLens.Importer --resweep` job that inflated Task 4's runs by 9–10× had
**finished** before this measurement (`0` such processes at the start and end), so these absolute
numbers are far more comparable than Task 4's. They are still not the plan header's 076 baseline:
`SqlHarness.Tests` alone measured 67,2–77,7 s across the eight sequential runs, and the header records
the whole solution-level `test` stage at `1m22s`. The concurrent median of 84,1 s is about what that
whole stage used to cost, which is the honest reading — **the stage now costs roughly the slower
project alone instead of the sum of both.**

### Step 10b: the same comparison with a build in front of every arm

`just verify` reported `test ok 1m51s` on the first run of the new gate, which is well above the 84,1 s
above — so the gate runs its test stage under conditions this first comparison did not reproduce, the
most likely one being the MSBuild worker nodes a `dotnet build` leaves resident. Rather than quote a
number that does not describe the gate, the comparison was repeated with a build in front of every
arm, alternating `S C C S S C`, three runs each:

| arm | runs, seconds | median |
|---|---|---|
| sequential | 139,5 · 153,8 · 160,4 | **153,8 s** |
| concurrent | 94,9 · 101,0 · 107,3 | **101,0 s** |

**Improvement 34,3 %**, which is what the gate actually saves. The two measurements bracket the
answer at **34–36 %**, both well clear of the 15 % bar, and in both of them every concurrent run beat
every sequential run — 22 runs, no overlap between the distributions. Twelve project executions in
this round, no failures.

### The load-induced flakes: what was counted, in which project

This is the risk the plan named, so it is counted rather than characterised loosely. The counts below
cover **53 whole-project executions**: 32 from the Step 10 measurement, 9 from the follow-up
experiment, 12 from Step 10b.

- **`PostgresParameterTests.Matrix_measure_sets_watch_and_snapshot_use_the_postgres_reference_scan`,
  the test the ledger asked this task to watch first: 0 failures in 53 executions.** It did not fail
  once, in either arm.
- **`DashboardHousekeepingTests.External_journal_commit_during_matching_refreshes_the_idle_window`:
  1 failure in 11 concurrent executions**, in measurement round 3. This is the flake Task 1 made
  diagnosable, and the plan already records it as a known recurring race.
- **`DashboardHostTests.Second_invocation_reuses_the_running_dashboard`: 1 failure in 3 runs of
  `SqlHarness.Tests` ALONE**, in the follow-up experiment — the *sequential* arm.

That is two failures in 53 executions, one per arm, on two different dashboard tests, both in
`SqlHarness.Tests`. The follow-up experiment ran the two configurations six more times, alternating,
with the only difference being whether `SqlHarness.Mcp.Tests` ran at the same time: 0 failures in 3
concurrent runs, 1 in 3 alone runs, on the *other* dashboard test. `DashboardHostTests` then passes
17/17 in six consecutive focused runs, so neither failure is a deterministic break, and the single
concurrent failure was not reproduced in the eleven concurrent executions that followed it. No
`src/` file changed in this task and neither failing test file was touched by it.

**The failure rate does not track the concurrency this task adds.** That is an observation over 53
runs on one machine, not a proof that concurrency cannot affect these tests; the honest summary is
that no flake signal appeared in the direction this change could cause, and the known dashboard flake
family is untouched by it.

### Parity assertions this task changed, and why

Every one of these was proved by a live mutation; the table names the red test each mutation produced.
`GateParityTests` went from 44 to 56 cases and `VerifyLinuxScriptTests` from 8 to 9.

| assertion | change | why |
|---|---|---|
| `StageVerbs` | distinct in first-occurrence order, and each entry of the Windows `-Project` list contributes a `test` | a project list is one `dotnet test` command per project, and the Windows gate no longer spells them out, so `StageCommand(content, "test")` finds nothing there |
| `StageCommands` / `StageCommand` | plural added; the singular is its first element | every test-command assertion reads a list, because "the test command" of this gate is two commands |
| `TestCommands`, `CommandsFor`, `ArgumentsOf`, `TestProjectOf`, `SolutionTestProjects` | new | the Windows commands are composed from `-Project` and `-Arguments`, and the solution is read so a new test project fails a guard instead of being silently skipped |
| `Every_gate_runs_the_same_non_integration_test_selection` | every command; `.github/workflows/ci.yml` added | a gate can carry the right filter on one project and the wrong one on the other |
| `Every_gate_names_the_same_test_filter_value` | every command, all three surfaces | its previous form read the first `--filter` in two files |
| `Every_gate_runs_integration_tests_filtered` | every command, all three surfaces; now asserts exactly one `--filter` whose whole value is the exclusion | the old quoted-substring regex cannot tell an unquoted value or a second `--filter` from a correct one |
| `Every_gate_runs_the_windows_test_commands_plus_its_own_logger` | replaces `The_two_gates_have_equal_test_commands_modulo_the_linux_logger`; a theory over the Linux gate **and CI**, comparing argument vectors | comparing command *text* across three shells compares their quoting, not what they run; CI was outside every test-command assertion, which is the narrowing Task 4 was corrected for |
| `The_windows_gate_runs_no_test_logger` | every Windows test command | as above |
| `The_only_logger_occurrences_are_the_ones_that_surface_asks_for` | every command of every verb on all three surfaces | it previously read only the first command per verb and never looked at CI |
| `The_extractor_returns_the_expected_commands_for_the_real_gate_files` | per-project strings for all three surfaces | the Windows commands no longer exist as literal lines |
| `Every_gate_runs_build_and_format_exactly_once` | new; theory over all three surfaces, added to CI in the final-review fix wave | `StageVerbs` is now distinct, which would otherwise let a second `dotnet build` slip in; with CI outside the theory, a second build or format step in `ci.yml` passed every comparison, because `StageCommand` reads the first command of a verb |
| `Every_gate_names_every_test_project_in_the_solution` | new, theory over all three surfaces | Review Focus 4: a third test project would silently not run |
| `The_windows_gate_runs_its_test_projects_through_the_shared_parallel_stage` | new | one `Add-Stage` over a project list, not one per project, and the helper comes from `gate-output.ps1` |
| `The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both` | new | two `&`, two `$!`, two `wait`, counted inside the test stage's own bash block |
| `Ci_runs_one_dotnet_test_step_per_test_project` | new | the step-per-project shape; the solution-scan test above cannot see it |
| `Linux_gate_runs_the_three_ci_stages_in_order` | distinct verbs | the test stage holds two commands of the same verb |
| `Linux_gate_uses_the_shared_stage_commands` | two per-project command lines; asserts `dotnet test SqlHarness.sln` is gone | the solution-wide form is the sequential one |
| `Linux_gate_names_the_failing_project_of_its_parallel_test_stage` | new | both projects share one Linux log, so the `stage-failure:` marker is the only thing naming the project |
| Pester `runs the same three .NET stages plus the two UI stages` | whole-line comments stripped before the verb check; `Invoke-ParallelGateStage` counts as the test stage | it passed for the wrong reason: a `dotnet test` phrase in a comment satisfied `dotnet test ` |

### Mutation proofs

Each mutation was applied to the working tree, the suite run, and the tree restored from a file copy.
**M13 and M15 in the first pass turned nothing red**, which is why both the fake and the assertions
below were rewritten and both re-run; that is recorded rather than quietly fixed.

| # | mutation | red |
|---|---|---|
| M1 | Windows parallel stage grows `--logger` | `The_extractor_returns_the_expected_commands_for_the_real_gate_files`, `Every_gate_runs_the_windows_test_commands_plus_its_own_logger` (×2 surfaces), `The_only_logger_occurrences_are_the_ones_that_surface_asks_for`, `The_windows_gate_runs_no_test_logger` |
| M2 | Windows drops the Mcp project from `-Project` | `Every_gate_names_every_test_project_in_the_solution(verify.ps1)`, the logger theory (×2), `The_extractor_returns_…` |
| M3 | Windows reverted to one solution-wide `dotnet test` | 10 red, including `Every_local_gate_runs_the_same_three_dotnet_stages_in_the_same_order`'s sibling assertions, `The_windows_gate_runs_its_test_projects_through_the_shared_parallel_stage`, plus the Pester `starts every test project at once in both gates` |
| M4 | a second `dotnet build` in the Windows gate | `Every_gate_runs_build_and_format_exactly_once(verify.ps1)` |
| M5 | one Linux project stops being backgrounded | `The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both`, `Linux_gate_uses_the_shared_stage_commands`, plus the Pester shape test |
| M6 | the Linux gate filters one project differently | 6 red, including `Every_gate_runs_integration_tests_filtered(verify-linux.ps1)` |
| M7 | the Linux gate stops waiting for the second project | `The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both`, plus the Pester shape test |
| M8 | CI collapses to one solution-wide step | `Ci_runs_one_dotnet_test_step_per_test_project`, `Every_gate_names_every_test_project_in_the_solution(ci.yml)`, the logger theory, `The_extractor_returns_…` |
| M9 | CI drops `--logger trx` from one step | `Every_gate_runs_the_windows_test_commands_plus_its_own_logger(ci.yml)`, `The_only_logger_occurrences_are_the_ones_that_surface_asks_for`, `The_extractor_returns_…` |
| M10 | the Linux gate stops naming the failing project | `Linux_gate_names_the_failing_project_of_its_parallel_test_stage` |
| M11 | the Windows gate routes the parallel stage through `Invoke-GateStage` instead | `The_windows_gate_runs_its_test_projects_through_the_shared_parallel_stage`, plus the Pester stage-shape test |
| M12 | neither launch helper drains stderr | 11 Pester red, including both flood tests — the 38-minute deadlock cannot return |
| M13 | the parallel stage returns the **last** process exit code | `reports the first project that failed, not the last process it joined` — **nothing was red on the first pass**, because both projects failed with the same code |
| M14 | the parallel stage names the **last** failing project's log | `reports the first project that failed, not the last process it joined` |
| M15 | the per-project log's last line stops naming the project | `reports the exit code and the log of the project that failed` — **nothing was red on the first pass**, because the log's first line still named the project |
| M16 | the parallel stage ignores `-Skipped` | `reports a skipped parallel stage without starting anything`, `reports a skipped stage instead of pretending it ran` |
| M17 | the stage joins each project before starting the next | `runs the projects at once rather than one after the other` |
| M18 | the stage writes no per-project log | 4 Pester red |
| M19 | every project writes to the same log | 5 Pester red |
| M20 | a second `dotnet build` step appended to `ci.yml` | `Every_gate_runs_build_and_format_exactly_once(ci.yml)` — the widening the final review asked for |
| M21 | a second `dotnet format` step appended to `ci.yml` | `Every_gate_runs_build_and_format_exactly_once(ci.yml)` |

The fake now maps a **different exit code per project** (M13) and the log assertions read the **last
line** (M15), which is what those two mutations needed. M17's proof is a rendezvous: each fake project
blocks on a release file the test creates only once *both* have started, so a sequential stage shows
one start line. That is structural rather than a timing comparison, so it does not weaken on a loaded
box.

### Live failure path of the real gate

`scripts/verify.ps1` run with a fake `npm` and a fake `dotnet` on PATH, so only the two test projects
are real work: `SqlHarness.Tests` answers 0, `SqlHarness.Mcp.Tests` answers 4.

```
==> ui-install skipped (lockfile unchanged)
==> build ok 0,1s
==> ui-check ok 0,1s
==> test FAILED (exit 4)
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-build --filter FullyQualifiedName!~Integration
PASS! - Failed: 0, Passed: 4015, Skipped: 0, Total: 4015
==> test project: tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj exit 4
full log: D:\temp\opencode\t3-probe-logs\test-SqlHarness.Mcp.Tests.log
verify: FAILED  stage=test  exit=4  0,6s
===== verify.ps1 exit code: 4 =====
```

The exit code is the failing project's, not the last-waited one; the printed tail names the project;
the green project's own log is untouched beside it. Review Focus 1 holds on the real gate, not only in
the unit test.

### The gate itself

```powershell
just verify -LogDirectory D:/temp/opencode/gate-t3-final
# ==> ui-install skipped (lockfile unchanged)
# ==> build ok 48,5s
# ==> ui-check ok 59,9s
# ==> test ok 1m51s
# format: 2 changed C# file(s) (working-tree)
# ==> format ok 1m01s
# verify: OK  stages=5  3m53s
# ===== just verify exit code: 0 =====
```

Exit 0, which is the contract in `docs/agents/verification.md`. The `test` stage wrote two logs,
`test-SqlHarness.Tests.log` (`Duration: 1 m 39 s`, 4018 passed / 11 skipped) and
`test-SqlHarness.Mcp.Tests.log` (`Duration: 1 m 44 s`, 402 passed / 4 skipped), and printed one line.
`ui-check` at 59,9 s against Task 4's contended 5m51s is the clearest sign that the importer job was
gone for this run. This evidence note is appended after the gate run and changes no executable
behaviour, so the run stands for the product tree.

### Focused runs

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests|FullyQualifiedName~VerifyLinuxScriptTests'
# Passed!  - Failed: 0, Passed: 65, Skipped: 0, Total: 65

pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"
# Passed: 84 Failed: 0
```

(The plan header records the `test` stage at `1m22s` sequential; the 84 here is 76 before this task
plus 8 new Pester cases.)