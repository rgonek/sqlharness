# Task 3 report — run the two test projects concurrently

Branch `opt-windows-gate`, worktree `opt-windows-gate-2`. Implemented against
`.superpowers/sdd/080-windows-gate-speedup/task-3-brief.md`, on top of Task 4's landed gate shape
(`c8e7bb9`, no `restore` stage, stage verbs `build` / `test` / `format`, `ui-check` overlapped with
the build).

**Status: DONE_WITH_CONCERNS.** The change is landed because it is 34–36 % faster under the gate's own
conditions and every parity guard has been proved by a live mutation. The concerns are in §8: a known
dashboard flake family produced one failure in each arm during a 53-execution sweep, and the Linux gate
was not run.

---

## 1. What landed

| file | change |
|---|---|
| `scripts/gate-output.ps1` | `Invoke-ParallelGateStage` (+136) and `Get-StageLogPaths` |
| `scripts/verify.ps1` | `Add-Stage` gained `-Project` / `-Arguments`; the test stage became a parallel call |
| `scripts/verify-linux.ps1` | the `test` stage's bash block backgrounds both `dotnet test` commands and waits for both |
| `.github/workflows/ci.yml` | one `dotnet test` step per test project |
| `tests/SqlHarness.Tests/GateParityTests.cs` | extractor rewrite + 5 new facts, 11 rewritten; 44 → 56 cases |
| `tests/SqlHarness.Tests/VerifyLinuxScriptTests.cs` | 2 rewritten, 1 new; 8 → 9 cases |
| `tests/scripts/gate-output.Tests.ps1` | 8 new cases for the new helper |
| `tests/scripts/gate-scripts.Tests.ps1` | 1 new shape test, 1 re-keyed |
| `README.md` | one paragraph: the parallel test stage and why CI keeps two steps |
| `plans/080-verification-evidence.md` | the Task 3 evidence section |

No `src/` file changed. No test lost an assertion.

---

## 2. Step 10 — the decision gate

Two independent measurements, each with both orders alternating so a drifting box cannot favour an
arm. The concurrent arm calls the **real** `Invoke-ParallelGateStage`, so what is timed is the code
path the gate runs.

**Measurement 1 — bare comparison, 16 runs, 8 per arm, order `S C C S S C C S`:**

| arm | runs (s) | median |
|---|---|---|
| sequential | 153,9 · 129,3 · 128,2 · 132,3 · 143,2 · 132,8 · 129,2 · 127,3 | **130,8 s** |
| concurrent | 81,3 · 83,7 · 86,9 · 84,5 · 85,8 · 82,7 · 90,2 · 82,7 | **84,1 s** |

**Improvement 35,7 %.**

`just verify` then reported `test ok 1m51s` — well above 84,1 s — because the gate runs its test stage
after a `dotnet build`, which leaves MSBuild worker nodes resident. Quoting 35,7 % as the gate's saving
would have been wrong, so I re-measured under gate conditions.

**Measurement 2 — a `dotnet build` in front of every arm, 6 runs, 3 per arm, order `S C C S S C`:**

| arm | runs (s) | median |
|---|---|---|
| sequential | 139,5 · 153,8 · 160,4 | **153,8 s** |
| concurrent | 94,9 · 101,0 · 107,3 | **101,0 s** |

**Improvement 34,3 %.** The two measurements bracket the answer at **34–36 %**, both far above the 15 %
bar. In both, every concurrent run beat every sequential run — 22 runs, no overlap between the
distributions, so the verdict does not depend on which run of either arm is picked.

**Decision: LAND.** Not close to the line: the worst concurrent run (107,3 s) is still 32 s faster than
the best sequential run (127,3 s).

**Machine caveat.** The foreign `CivicLens.Importer --resweep` job that inflated Task 4's runs by 9–10×
had **finished** (`0` such processes at the start and end of measurement 1; `ui-check` at 59,9 s in the
gate run against Task 4's 5m51s). These absolute numbers are therefore far more comparable than Task
4's, but they are still not the plan header's 076 baseline (`test 1m22s`), which was taken on another
machine. **The ratio is the claim.**

---

## 3. The flake I was told to watch first

**`PostgresParameterTests.Matrix_measure_sets_watch_and_snapshot_use_the_postgres_reference_scan`:
0 failures in 53 whole-project executions.** It never failed, in either arm.

Across all 53 executions (32 + 9 + 12, see the evidence note) there were **2 failures, one per arm**,
on two *different* dashboard tests, both inside `SqlHarness.Tests`:

| test | arm | executions | failures |
|---|---|---|---|
| `DashboardHousekeepingTests.External_journal_commit_during_matching_refreshes_the_idle_window` | **concurrent** | 11 | 1 (measurement 1, round 3) |
| `DashboardHostTests.Second_invocation_reuses_the_running_dashboard` | **sequential, running alone** | 3 | 1 |

**What I did to test that this is not "a pre-existing flake" hand-wave:**

1. **A/B with one variable.** Six further runs alternating, differing only in whether
   `SqlHarness.Mcp.Tests` ran at the same time: 0 failures in 3 concurrent, 1 failure in 3 alone — on the
   *other* dashboard test.
2. **Is it deterministic?** `DashboardHostTests` then passed 17/17 in six consecutive focused runs.
3. **Is it reproducible under concurrency?** The single concurrent failure was not reproduced in the
   eleven concurrent executions that followed it.
4. **Did this task touch the code?** No `src/` file changed; neither failing test file was touched.
   The failing test's own file was last modified by Task 1.
5. **Scope of the claim.** The failure rate does not track the concurrency this change adds. That is an
   observation over 53 runs on one machine, **not** a proof that concurrency cannot affect these tests.

---

## 4. Parity assertions changed, each with its reason and its mutation

**Every row below was proved by mutating the real file and observing the red test.** Full table in
`plans/080-verification-evidence.md` §Task 3.

| assertion | change | why |
|---|---|---|
| `StageVerbs` | distinct in first-occurrence order; each `-Project` entry contributes a `test` | a project list is one command per project, and the Windows gate no longer spells them out, so `StageCommand(content,"test")` finds **nothing** there |
| `StageCommands` / `StageCommand` | plural added; singular = its first element | every test-command assertion now reads a list |
| `TestCommands`, `CommandsFor`, `ArgumentsOf`, `TestProjectOf`, `SolutionTestProjects` | new | the Windows commands must be composed from `-Project` + `-Arguments`; the solution must be read so a third test project fails a guard |
| `Every_gate_runs_the_same_non_integration_test_selection` | every command; **CI added** | a gate can carry the right filter on one project and the wrong one on the other |
| `Every_gate_names_the_same_test_filter_value` | every command, all three surfaces | its old form read the first `--filter` in two files |
| `Every_gate_runs_integration_tests_filtered` (was `Neither_gate_runs_integration_tests_unfiltered`) | every command, all three surfaces; now asserts **exactly one** `--filter` whose whole value is the exclusion | the old quoted-substring regex cannot tell an unquoted value or a second `--filter` from a correct one |
| `Every_gate_runs_the_windows_test_commands_plus_its_own_logger` (replaces `The_two_gates_have_equal_test_commands_modulo_the_linux_logger`) | theory over the Linux gate **and CI**, comparing **argument vectors** | see §5 |
| `The_windows_gate_runs_no_test_logger` (was `…does_not_include_a_logger`) | every Windows test command | ditto |
| `The_only_logger_occurrences_are_the_ones_that_surface_asks_for` (was `…is_in_the_linux_test_command`) | every command of every verb on all three surfaces | it read only the first command per verb and **never looked at CI** |
| `The_extractor_returns_the_expected_commands_for_the_real_gate_files` | per-project strings for all three surfaces | the Windows commands no longer exist as literal lines |
| `Every_local_gate_runs_build_and_format_exactly_once` | **new** | the dedupe would otherwise let a second `dotnet build` slip in — this restores the count teeth the dedupe removed |
| `Every_gate_names_every_test_project_in_the_solution` | **new**, theory over all three surfaces | Review Focus 4 |
| `The_windows_gate_runs_its_test_projects_through_the_shared_parallel_stage` | **new** | one `Add-Stage` over a project list; the helper comes from `gate-output.ps1` |
| `The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both` | **new** | two `&`, two `$!`, two `wait`, counted inside the test stage's own bash block |
| `Ci_runs_one_dotnet_test_step_per_test_project` | **new** | the step-per-project shape; the solution-scan test cannot see it |
| `Linux_gate_runs_the_three_ci_stages_in_order` | distinct verbs | the stage holds two commands of the same verb |
| `Linux_gate_uses_the_shared_stage_commands` | two per-project lines; asserts `dotnet test SqlHarness.sln` is **gone** | the solution-wide form is the sequential one |
| `Linux_gate_names_the_failing_project_of_its_parallel_test_stage` | **new** | both projects share one Linux log, so the `stage-failure:` marker is the only thing naming the project |
| Pester `runs the same three .NET stages plus the two UI stages` | comments stripped before the verb check; `Invoke-ParallelGateStage` counts as the test stage | **it was passing for the wrong reason** — see §6 |
| Pester `starts every test project at once in both gates` | **new** | the Pester-side counterpart of the two shape facts |

### Surfaces that must stay in scope

The brief re-keyed the parity comparisons without naming the scope, and Task 4's brief did exactly that
and cost a round. Explicitly, these are the surfaces every test-command assertion now covers:

- `scripts/verify.ps1` — **the only surface with no literal `dotnet test` text**; its commands are
  composed from the parallel stage's `-Project` and `-Arguments`.
- `scripts/verify-linux.ps1` — two single-quoted bash lines, each ending in `&`.
- `.github/workflows/ci.yml` — two `run:` steps. **CI was outside every test-command assertion before
  this task** and is now inside six of them.

CI's scope was also the subject of mutation **M9** (dropping `--logger trx` from one CI step), which is
the same narrowing that Task 4 had to undo for the build stage. It goes red in three tests.

---

## 5. The parity comparison compares arguments, not text

The three surfaces quote the same argument three ways — PowerShell passes it bare, bash and yaml quote
it. Comparing command **text** across them compares their shells, not what they run. My first version
did exactly that and went red for the wrong reason (the `&` and the quotes). `Every_gate_runs_the_windows_test_commands_plus_its_own_logger` now compares `ArgumentsOf(command)` — quoted and bare spellings collapse to the same argument — as sets, because which project a surface runs first is not a parity property. `StageCommands` strips a trailing `&` and a trailing `# comment` with the command, so what is compared is the command and not how the shell schedules it; the backgrounding itself is pinned separately by `The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both`.

---

## 6. Failing-then-passing runs

**Step 2 (the brief's red run).** Five new Pester cases against a non-existent command:

```
[-] runs every project and writes a log per project          CommandNotFoundException: The term 'Invoke-ParallelGateStage' is not recognized
[-] reports the exit code and the log of the project that failed
[-] reports the first project that failed when more than one fails
[-] reports a skipped parallel stage without starting anything
[-] drains both pipes of a project that floods stderr while writing to stdout
Passed: 29 Failed: 5
```

**Step 4.** `Passed: 34 Failed: 0`.

**The parity suite after rewiring the three surfaces but before rewriting it:** `Failed: 11, Passed: 41`
— recorded so the failing→passing transition is visible.

**Final:** `GateParityTests + VerifyLinuxScriptTests` → `Failed: 0, Passed: 65` (was 52).
`tests/scripts` (Pester) → `Passed: 84 Failed: 0` (was 76).

**`just verify` on the final tree:** `verify: OK stages=5 3m53s`, exit 0.

### Two guards that were passing for the wrong reason

1. **Pester `runs the same three .NET stages…`** checked `$gate -notmatch "dotnet test "`, and after my
   change the only `dotnet test` in `verify.ps1` was a **comment inside `Add-Stage`**. The suite was
   green on a gate with no test stage. Fixed by stripping whole-line comments before the verb check and
   by accepting the parallel helper as the test stage; the comment that satisfied it by accident was
   reworded. Mutation **M3** (revert the whole change) now turns it red.
2. **`The_windows_test_command_does_not_include_a_logger`** read `StageCommand(content, "test")`, which
   under the new shape finds **zero** commands in `verify.ps1`. Every assertion in it would have passed
   over an empty collection. Fixed by routing the `test` verb through `TestCommands` for all surfaces.
   Mutation **M1** (add a `--logger` to the Windows stage) now turns it red.

---

## 7. Mutation proofs

19 mutations, each applied to the working tree and reverted from a **file copy** (never `git checkout` —
see §9). All produce red tests; the table is in the evidence note. The two that did not, and what I did
about them:

- **M13** (the stage returns the **last** process's exit code instead of the first failure's) turned
  **nothing red**, because my fake failed *both* projects with the same code, so "first" and "last" were
  the same answer. The fake now maps a **different exit code per project** (A→3, B→4) and the test
  asserts 3. Re-run: `reports the first project that failed, not the last process it joined` goes red.
- **M15** (the per-project log's last line stops naming the project) turned **nothing red**, because the
  log's *first* line still named it. The assertions now read the **last line** specifically — the gate
  prints a 40-line tail, so a header at the top of a long log is never in it. Re-run: red.

The concurrency claim is proved structurally, not by timing: each fake project blocks on a release file
that the test creates only once **both** have started, so a sequential stage shows one start line
(**M17** → red).

**The drain** is proved by **M12** (neither launch helper reads stderr): 11 Pester red, including both
flood tests. The 38-minute deadlock at `gate-output.ps1:46-48` cannot return.

**Review Focus 1 proved on the real gate**, not only in a unit test — `scripts/verify.ps1` with a fake
`npm` and a fake `dotnet`, the two projects answering 0 and 4:

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

The exit code is the failing project's, not the last-waited one; the tail names it; the green project's
own log sits beside it untouched; and a green stage prints exactly one line.

---

## 8. Concerns

1. **The known dashboard flake family produced one failure in each arm** during a 53-execution sweep
   (§3). The rate does not track concurrency, and nothing here is a deterministic break, but this
   change does add load to a suite that already has two flaky dashboard tests. The gate got *faster*;
   that is not free. I would not claim the flake question is closed — I would claim the evidence points
   away from this change as the cause.
2. **The Linux gate was not run.** It is optional and not completion criteria
   (`docs/agents/verification.md:7`). The changed bash block is pinned structurally and by mutation, but
   **it has not been executed**, so "it keeps working" is an inference from the shape, not a result.
   Task 4 recorded that the gate clone is provisioned on this box, so this is closeable — but it needs a
   run, and its two known pre-existing `dotnet test` failures (recorded at `24c4070c`) mean a green Linux
   gate is not reachable here anyway.
3. **The two Linux test projects write into one log and interleave there.** Windows writes one log per
   project. I did not add per-project redirection on the Linux side because it would put a shell
   redirect inside the extracted command and force the parity extractor to strip it. The failure is
   named by `==> <project> FAILED (exit N)` and `stage-failure:`; the interleaving is stated in a
   comment at the block. A future reader looking for a per-project Linux log will not find one.
4. **`Invoke-ParallelGateStage`'s `LogPath` is `$null` when the stage is green** — it names the first
   *failing* project's log, as the brief specifies. That is why `Get-StageLogPaths` exists; anything else
   consuming a stage result must use it rather than `LogPath`, or `-Stream` will print nothing for a
   green parallel stage.
5. **A CI failure uploads only the failing project's trx.** The `upload-artifact` step is `if:
   always()`, but the second `dotnet test` step is skipped when the first fails. That is the right
   trade (you want the red project's results) and it is not what the previous single step produced.
6. **CI is sequential by choice.** Two steps, not concurrent steps, so the job summary names the project.
   CI therefore gets no speedup from this task. A `strategy.matrix` per project is the natural follow-up
   and is not in scope here.
7. **Placement deviation from the brief.** `Invoke-ParallelGateStage` sits after
   `Complete-BackgroundGateStage`, not after `Invoke-GateStage` (line 130), because it calls
   `Resolve-GateStageExecutable`, which Task 4 placed with the other launch helpers. Putting it above
   the function it calls reads worse.
8. **`Add-Stage` dispatches on `-Project` vs `-Action`**, rather than the brief's "a single `Add-Stage`
   whose action calls `Invoke-ParallelGateStage`". Routing it through `Invoke-GateStage`'s scriptblock
   discards the returned `ExitCode` and `LogPath` — which is the whole point of the interface — and
   re-introduces the `$LASTEXITCODE` hand-off that Review Focus 1 names. A wrong call (both parameters,
   or neither) throws rather than reporting green.

---

## 9. Process note

My first mutation script restored mutated files with `git checkout --`, which **discarded the
uncommitted changes to five files** (`verify.ps1`, `verify-linux.ps1`, `ci.yml`, and both C# test
files). I rebuilt them and switched to a file-copy backup, which was then itself stale once and
silently reverted two test improvements, making two mutations appear toothless when they were not. The
consequence was wasted time, not a wrong result: every mutation was re-run against a verified-identical
tree afterwards (`0 files differing from backup`), and the mutation numbers in §7 are from the runs
against that verified state.

---

## 10. Reproduce

```powershell
just verify -LogDirectory D:/temp/opencode/gate-t3-final
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests|FullyQualifiedName~VerifyLinuxScriptTests'
pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"
```

Measured tree: the commit that landed this task, on top of `c8e7bb9`. A commit cannot contain its own SHA, so read it from `git log -1`.

---

# Fix round 1

One Important finding, one guard. Nothing in §1–§10 changed.

## The finding

`Linux_gate_names_the_failing_project_of_its_parallel_test_stage` computed `testStage` and then never
used it. All four of its assertions searched the **whole** `verify-linux.ps1`, and `'set +e',` occurs
**twice** — in the build stage and in the test stage — so the assertion was answered by the build
stage's copy and said nothing about the test stage. Deleting `'set +e',` from the test stage left C#
65/65 and Pester 84/84 green. Without it the block runs under `set -euo pipefail`, so
`wait "$tests_pid"` aborts before `wait "$mcp_pid"`: the second project is orphaned and neither the
`==>` marker nor the `stage-failure:` marker is printed — Review Focus 1's exact failure mode, unguarded.

## The fix

Every assertion meant to describe the test stage now reads the test stage's own block, extracted the
way the C# twin does (from `Invoke-LinuxStage -Name 'test'` to the next `Invoke-LinuxStage -Name`), and
the whole test was audited rather than just line 58:

| assertion | before | after |
|---|---|---|
| `set +e` | `Assert.Contains(…, script)` — satisfied by the build stage | `Assert.Single(Regex.Matches(block, @"'set \+e',"))` |
| `wait` per pid | not asserted here | `Assert.Equal(2, …)` — both projects are joined before either code is read |
| `set -e` restored | not asserted here | `Assert.Contains("'set -e',", block)` |
| the two `==>` markers | not asserted here | `Assert.Contains(…, block)` — added; on Linux these are the only thing naming the project in the log |
| the two `stage-failure:` markers | `Assert.Contains(…, script)` — unique to the test stage today, unbounded nonetheless | `Assert.Contains(…, block)` |

Nothing was deleted; three assertions were added.

**The cut is asserted, not assumed.** The stage after the test stage is format, so the block must not
contain `dotnet format`, and the script must still contain it — the pair stops the guard passing for the
wrong reason (a format stage that stopped existing must not look like a clean cut):

```csharp
Assert.Contains("dotnet format", script, StringComparison.Ordinal);
Assert.DoesNotContain("dotnet format", block, StringComparison.Ordinal);
```

`GateParityTests.The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both` had the
same vacuity exposure — both `dotnet test … &` lines live in that stage, so every count there still
reads 2 over the whole file — and got the same two assertions.

## Mutation proofs

| # | mutation | result |
|---|---|---|
| **M-A** | `'set +e',` deleted from the **test** stage, left in the build stage — the reviewer's mutation | **RED** `SqlHarness.Tests.VerifyLinuxScriptTests.Linux_gate_names_the_failing_project_of_its_parallel_test_stage` (xunit 64/65, Pester 8/8 green) |
| **M-B** | control: `'set +e',` deleted from the **build** stage, left in the test stage | **GREEN** 65/65 and Pester 8/8 — so the bound counts only the test stage's copy, and is not satisfied by occurrence count |
| **M-C** | the test stage's extraction stops cutting (`if (next > 0 && block.Length < 0)`), so the block holds the test stage *and everything after it* | **RED** `…Linux_gate_names_the_failing_project_of_its_parallel_test_stage` |
| **M-D** | the same, in the C# twin | **RED** `…The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both` |

M-A is the reviewer's mutation and the teeth proof. M-B is the control that shows the new bound is not
just an occurrence count. M-C and M-D are the non-vacuity proofs: an extraction that stopped cutting at
the next stage would let every search read the file tail and pass silently, and both are now caught by
the `dotnet format` pair.

**Two of these mutations proved nothing on the first attempt and were fixed before being accepted:**

- M-C/M-D were first written as `if ($false) { … }`, which is not valid C#. The build failed, the
  previous test binary stayed in place, and the suite reported green — a mutation that measures the
  unmutated code. The harness now fails loudly on a build error, and the mutations were rewritten as
  compiling C#.
- `Copy-Item` preserves the source timestamp, so a restored file could look **older** than the binary
  built from the previous mutation; MSBuild skipped it and a "restored" run reported the last
  mutation's red. `Restore` now touches every restored file. All four results above are from runs after
  that fix, and the restored tree was re-confirmed from `dotnet clean` + a full rebuild: 65/65.

**The first version of the non-vacuity guard named the wrong stages.** It asserted the block contained
no `npm run check` and no `dotnet build` — but both live in the *build* stage, which precedes the test
stage and therefore cannot appear in a substring starting at it. Those two assertions could never fail.
They were replaced with the format-stage pair above, which can.

## Verification after the fix

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~VerifyLinuxScriptTests|FullyQualifiedName~GateParityTests'
# Passed!  - Failed: 0, Passed: 65, Skipped: 0, Total: 65     (after dotnet clean + full rebuild)

pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"
# Passed: 84 Failed: 0

just verify -LogDirectory D:/temp/opencode/gate-t3-fix1
# ==> ui-install skipped (lockfile unchanged)
# ==> build ok 25,2s
# ==> ui-check ok 2m20s
# ==> test ok 2m14s
# format: 2 changed C# file(s) (working-tree)
# ==> format ok 1m17s
# verify: OK  stages=5  5m53s
# ===== just verify exit code: 0 =====
```

**The `powershell.exe` Pester command in the fix-round instructions reports `Passed: 10 Failed: 34`,
and that is not this round's doing.** Every `scripts/*.ps1` carries `#Requires -Version 7.0`, so
Windows PowerShell 5.1 raises `ScriptRequiresException` before any test body runs. This is recorded
under Task 4 ("The Pester suite must be run under `pwsh`, not `powershell.exe`") and is why
`.github/workflows/ci.yml` runs that lane with `shell: pwsh`. Under `pwsh` the same path is 84/84, and
it was 84/84 before this round too — the count is unchanged, because this round changed two C#
assertions and added no Pester case.