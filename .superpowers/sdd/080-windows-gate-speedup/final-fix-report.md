# Plan 080 — final-review fix wave

Scope: the two minors the whole-branch final review named as must-fix-before-merge. Nothing else was
touched. No `src/` file, no script, no workflow file, and none of the ten deferred minors.

## Finding 1 — README:608-611 claimed per-project logs for the Linux gate

### What the code actually does (read, not taken on the review's summary)

- **Windows gate.** `scripts/verify.ps1:178-181` runs the test stage through
  `Invoke-ParallelGateStage` (`scripts/gate-output.ps1:279`). That writes one log per project
  (`$Name-<project>.log`, line 368), each log naming its project on the first line and again on the
  last (`==> test project: <project> exit <code>`, line 366), and the result's `LogPath` is the first
  *failing* project's log (line 386). `Get-StageFailureText` tails that log, so the failure tail names
  the project that failed. Verified by reading the helper, not inferred.
- **Linux gate.** `scripts/verify-linux.ps1:274-289` runs both `dotnet test` commands in one bash
  block and the block writes to the single stage log `test.log` via `Invoke-GateStage`
  (`gate-output.ps1:96`). There is **no** per-project log: both processes inherit the block's stdout
  and stderr and interleave there. The script's own comment at lines 272-273 says so.
- **Why the failure is still findable.** Both markers are `echo ... >&2` and are reached only after
  both `wait`s (`set +e` around them, lines 281-283), and `Invoke-NativeStageCommand` appends the
  whole stderr block after the whole stdout block (`gate-output.ps1:56-58`), so the
  `==> <project> FAILED (exit N)` line and the `stage-failure: <project>` marker land at the very end
  of the log — inside the tail `Get-StageFailureText` prints. `stage-failure:` is also what
  `Get-ReportedFailureStage` turns into `stage=<project>` on the summary line.
- **CI.** `ci.yml:44-45` keeps one step per project, so the GitHub job summary names the project.

### The edit

`README.md` now attributes the per-project log to `just verify` only, states that the Linux block
writes no per-project log and that its two projects interleave in the one stage log, and says why the
failure is still findable (the `==>` line and the `stage-failure:` marker are written only after both
processes are joined, so they land at the end of the log and inside the failure tail). `ci.yml` is
described as before. Nothing now implies the Linux gate has per-project logs.

One precision fix on `stage-failure:`: when both projects are red only the first one's marker is
written (line 286 exits before line 287), so the text says "the marker for the project whose code the
gate returns" rather than implying one marker per red project.

### The same section's stage-summary example (review Minor 6)

`README.md:596` quoted `==> build ok 45.5s` / `verify: OK stages=5 2m03s`, which reads like a
measurement on a branch that deliberately refuses to claim one. Replaced with placeholders:
`==> build ok <stage seconds>` and `verify: OK  stages=5  <total>`. Two further corrections on that
same line, each checked against `gate-output.ps1` by running it rather than by reading it:

- the real summary line has two spaces either side of `stages=` and the duration (`Get-GateSummaryLine`,
  line 479); the old example showed one, which an agent matching gate output against the README would
  have missed;
- `5` is the Windows gate's count, so the text now says *each gate counting its own stages* (the Linux
  gate has 7). Left as a statement rather than a new number: no stage-count arithmetic was touched,
  per the brief.

Observation, not fixed (out of scope, and the placeholder removes it anyway): on this machine
`Get-StageLine` formats seconds through `-f` under the current culture, so `45.5` renders as `45,5s`
under `pl-PL`. The old README example was wrong for every comma-culture machine.

## Finding 2 — the build/format-once guard excluded CI

`tests/SqlHarness.Tests/GateParityTests.cs:34-46`: `Every_local_gate_runs_build_and_format_exactly_once`
was a theory over the two local scripts only, and `StageCommand` returns the *first* command of a
verb — which is what every other build/format comparison in this file reads. A second `dotnet build`
or `dotnet format` step in `ci.yml` therefore passed the whole suite.

Changed to `Every_gate_runs_build_and_format_exactly_once` with `.github/workflows/ci.yml` added to
the theory (the same rename convention this file already uses for the I-3 narrowing: `Every_gate_*`
names carry CI). No assertion was removed or weakened; the theory gains one case.

### Live mutations — both reverted

Each mutation was appended to `ci.yml`, the filtered suite was run, and `ci.yml` was restored from
HEAD (`git checkout --`); `git diff HEAD -- .github/workflows/ci.yml` is empty afterwards and the file
keeps its no-final-newline, CRLF form.

| # | mutation appended to `ci.yml` | red |
|---|---|---|
| M20 | `- run: dotnet build -warnaserror` | `GateParityTests.Every_gate_runs_build_and_format_exactly_once(path: ".github/workflows/ci.yml")` — `Assert.Single() Failure: The collection contained 2 items / ["dotnet build -warnaserror", "dotnet build -warnaserror"]`. 1 red, 65 passed. |
| M21 | `- run: dotnet format --no-restore --verify-no-changes` | the same test, same ci.yml case — `Assert.Single() Failure: The collection contained 2 items`. 1 red, 65 passed. |

Each mutation turned exactly one test red, which is the point: before the widening both passed.

`plans/080-verification-evidence.md` was updated for the rename (two references to the old test name
would otherwise have dangled) and the two new mutations were added as M20/M21 in the existing ledger
tables. No plan step or index status was changed.

## Verification

| command | result |
|---|---|
| `dotnet build tests/SqlHarness.Tests/SqlHarness.Tests.csproj -warnaserror` | 0 warnings, 0 errors |
| `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests\|FullyQualifiedName~VerifyLinuxScriptTests'` | Passed 66, Failed 0 (65 before the extra theory case) |
| `pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"` | Passed 84, Failed 0, Skipped 0, Pending 0, Inconclusive 0 |
| full `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build` | Passed 4025, Failed 0, Skipped 29 (pre-existing non-Windows skips), 1m41s |

Pester was run with `pwsh`, as the brief requires; `powershell.exe` cannot load the `#Requires -Version 7.0`
scripts and reports a false 10/34.

## Not touched, deliberately

The ten deferred minors: no `Get-StageLogPaths` test, no stage-count arithmetic (the overlapping
`$script:stageCount` double-bump in `verify.ps1:159-164` is left as it is), no change to how a start
failure is reported, no change to the Windows/Linux failure-naming asymmetry, no edit to any comment
the review called an overclaim — including `scripts/verify.ps1:15`, which says "Every project writes
its own log" and is true of that script; the inaccuracy was in the README, where the claim was made
for both gates. `plans/README.md` still lists 080 as IN PROGRESS, which the review flagged as a merge-
commit step, not a branch defect.

## Concerns

1. **The Linux gate's parallel test block still has never run live as committed** (carried from the
   review). The README sentence is now a claim about behaviour the branch has pinned structurally
   (`VerifyLinuxScriptTests.Linux_gate_names_the_failing_project_of_its_parallel_test_stage`,
   `GateParityTests.The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both`) but
   has not observed on a WSL run. Same for CI: `ci.yml` has never executed on this box.
2. **The guard is syntactic.** `StageCommands` counts `dotnet build` / `dotnet format` as they are
   written. A second build smuggled in as a `run: msbuild …` step, or as a shell variable
   (`$cmd = "build"`), is still invisible to it. The widening closes the hole the review named; it is
   not a proof against every shape.
3. **Two findings now share one test.** Both CI mutations go red through
   `Every_gate_runs_build_and_format_exactly_once(ci.yml)`. That is the intended behaviour, but it
   means the failure message for either drift is "the collection contained 2 items" with no mention
   of which verb — a reader has to look at the collection to tell a second build from a second format.
   Left alone: making the message name the verb is a change to `StageCommands`/`Assert.Single` that
   this wave was not asked for.
4. The `<stage seconds>` / `<total>` placeholders make the README less concrete as a worked example.
   If a reader later wants a real-looking sample, the right form is one labelled as illustrative, not
   one carrying the pre-branch `2m03s`.