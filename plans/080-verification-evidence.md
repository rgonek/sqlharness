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