# Integration fix report — 080 gates vs. plan 077's non-test project

Branch `opt-windows-gate` · parent 5733e70 · machine date 2026-10-17.

**Status: DONE.** One file changed: `tests/SqlHarness.Tests/GateParityTests.cs`.

## The defect

`SolutionTestProjects()` returned every `tests\*.csproj` line in `SqlHarness.sln`. Plan 077 adds
`tests\SqlHarness.Mcp.WorkflowHost/SqlHarness.Mcp.WorkflowHost.csproj`, which declares
`<IsTestProject>false</IsTestProject>` — the marker the SDK honours and that `dotnet test` skips on. Both
consumers of the method, `Every_gate_names_every_test_project_in_the_solution` (3 cases) and
`Ci_runs_one_dotnet_test_step_per_test_project`, would then demand a `dotnet test` step for a project that
must not be tested, and merged `main` goes red.

## The change

`SolutionTestProjects()` keeps its signature and its `internal` visibility, keeps reading the solution
rather than a list, and gains one `.Where(...)` step:

```csharp
internal static string[] SolutionTestProjects() =>
    Regex.Matches(ReadRepositoryFile("SqlHarness.sln"), @"""(?<project>tests\\[^""]+\.csproj)""", RegexOptions.IgnoreCase)
        .Select(match => match.Groups["project"].Value.Replace('\\', '/'))
        .Where(DotnetTestWouldRun)
        .OrderBy(project => project, StringComparer.Ordinal)
        .ToArray();
```

Supporting members, all in the same file:

- `DotnetTestWouldRun(project)` reads the project's own text through the file's existing
  `ReadRepositoryFile(project.Split('/'))` seam (the `\` → `/` spelling the method already normalises to,
  split back into path parts — the same call shape as `ReadRepositoryFile("scripts", "verify.ps1")`), and
  asks `IsMarkedNotATestProject`. No new file-reading helper was introduced.
- `DeclaredNotATestProject` is a `private static readonly Regex` next to the file's other two
  (`ParallelTestStage`, `TestProjectArgument`): `<IsTestProject\s*>\s*(?:false|0)\s*</IsTestProject\s*>`,
  `RegexOptions.IgnoreCase`. `0` counts because MSBuild booleans spell false that way.
- `IsMarkedNotATestProject(projectFile)` is the pure read, `internal` like the file's other seams
  (`StageCommand`, `ArgumentsOf`, `FilterValueOf`).

**The filter is the marker, never a name.** `WorkflowHost` is not spelled anywhere in the change, so the
next project that sets `IsTestProject=false` is handled without anybody editing the test. A project that
declares nothing is a test project, because that is the SDK default — which is why the two live test
projects, which set nothing and rely on `Microsoft.NET.Test.Sdk`, stay in.

**One new test, no assertion lost.** `Only_the_marker_dotnet_test_skips_on_marks_a_project_as_not_a_test_project`
is a 6-case `[Theory]` over `IsMarkedNotATestProject`: `false`, `False`, `0` marked; `true`,
`<IsPackable>false</IsPackable>`, and empty not marked. It exists because nothing in the repository
declares the marker, so without it the filter would be code no test ever runs and a pattern that stopped
matching would only be found by the merged tree — which is the one tree this branch cannot run. The last
three cases are what stop the filter from decaying into a name filter. Nothing else was added or removed;
no existing assertion was weakened.

## Behaviour chosen for an unreadable or missing `.csproj`

**It fails loudly: the guard goes red and names the file.** It does not fall back to including the project
and it does not fall back to excluding it.

```csharp
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Assert.Fail($"SqlHarness.sln lists {project}, but reading it failed, so whether `dotnet test` runs it is unknown. {ex.Message}");

    throw;
}
```

Both fallbacks are quiet in the direction that matters. Defaulting to *not a test project* would drop a
real test project from the guard's expectations — the exact drift the two guards exist to catch — and the
suite would go green on a tree where a test project no gate runs. Defaulting to *is a test project* would
demand a step for a project no gate can run, which is the merged-`main` defect again, just triggered by an
unreadable file instead of a missing marker. Neither guess is safe, so a solution entry this test cannot
read is itself drift: red, with the path and the underlying `FileNotFoundException` message. The alternative
of letting `RepositoryFile.Locate` throw uncaught was rejected — it is also red, but it reads as a broken
test rather than a guard that could not do its job, and its message does not say why the file was needed.

The catch is `IOException or UnauthorizedAccessException`, not every `Exception`, so a real defect in the
read path still surfaces as itself.

## Verification

### Step 1 — the required run, green on this branch

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter 'FullyQualifiedName~GateParityTests|FullyQualifiedName~VerifyLinuxScriptTests'
```

`Passed! - Failed: 0, Passed: 72, Skipped: 0, Total: 72`. 66 before the change, 72 after (the 6 new theory
cases). Baseline before the change on the same command was 66/66.

### Step 2 — proving the filter works

`SqlHarness.sln` was edited for each case (a `Project(...)`/`EndProject` pair inserted before `Global`,
BOM and CRLF preserved) and restored with `git checkout -- SqlHarness.sln` afterwards; `git diff --name-only
HEAD -- SqlHarness.sln` is empty and the file is byte-identical to HEAD.

**A. sln entry, no file on disk (the case the brief names).** Entry for
`tests\SqlHarness.Mcp.WorkflowHost\SqlHarness.Mcp.WorkflowHost.csproj`, which does not exist on this branch:

```
Failed!  - Failed: 4, Passed: 68, Skipped: 0, Total: 72
```

All 4 are the two guards (3 `Every_gate_names_every_test_project_in_the_solution` cases +
`Ci_runs_one_dotnet_test_step_per_test_project`), each with:

```
SqlHarness.sln lists tests/SqlHarness.Mcp.WorkflowHost/SqlHarness.Mcp.WorkflowHost.csproj, but reading it
failed, so whether `dotnet test` runs it is unknown. Could not locate repository file:
tests/SqlHarness.Mcp.WorkflowHost/SqlHarness.Mcp.WorkflowHost.csproj
```

This is the behaviour described above: loud, not a silent include. A run reported as passing "because the
filter fell back to including it" would be indistinguishable from the merged-`main` defect.

**B. the same entry with the real `.csproj` on disk — the shape merged `main` will have.** The temporary
project declared `<IsTestProject>false</IsTestProject>`:

```
Passed!  - Failed: 0, Passed: 72, Skipped: 0, Total: 72
```

Green — which is the whole point of the change. Without the filter this state is the red merge.

**C. that marked project plus a genuine third test project.** Added `tests/SqlHarness.Mcp.Tests.Extra` with
`Microsoft.NET.Test.Sdk` + `xunit` and no marker:

```
Expected: 3
Actual:   2
Expected: ["tests/SqlHarness.Mcp.Tests.Extra/…", "tests/SqlHarness.Mcp.Tests/…", "tests/SqlHarness.Tests/…"]
Actual:   ["tests/SqlHarness.Mcp.Tests/…", "tests/SqlHarness.Tests/…"]
```

The marked project is excluded and the unmarked one is demanded, so the filter does not weaken the guard's
purpose: it still fails on the day a real third test project appears.

**After removing both temporary projects and the sln entries:** `Passed! - Failed: 0, Passed: 72`, and
`git status --porcelain --untracked-files=all` shows only ` M tests/SqlHarness.Tests/GateParityTests.cs`.
The temp directories and the sln edits are gone from the worktree, not merely reverted.

### Step 3 — Pester, with `pwsh`

```powershell
pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"
```

`Passed: 84 Failed: 0 Skipped: 0 Pending: 0 Inconclusive: 0`, exit 0.

### Step 4 — the diff

`git diff --stat` → `tests/SqlHarness.Tests/GateParityTests.cs | 54 ++++…` (1 file changed, 54 insertions,
0 deletions). No `src/`, no scripts, no other test file, no `.editorconfig` newline change
(`insert_final_newline = false` respected — nothing added a trailing newline).

### Beyond the required list

- `dotnet format SqlHarness.sln --no-restore --include tests/SqlHarness.Tests/GateParityTests.cs --verify-no-changes`
  → exit 0. This is the gate's own format stage scoped to the changed file, so the change is format-clean.
- Both test projects, gate filter: `SqlHarness.Tests` 4025 passed / 11 skipped / 0 failed (4036 total) and
  `SqlHarness.Mcp.Tests` 402 passed / 4 skipped / 0 failed (406 total).

## Concerns and limits

1. **One unreproduced failure in the full `SqlHarness.Tests` run.** The first full run reported
   `Failed: 1, Passed: 4024, Skipped: 11, Total: 4036`. The test name was not captured — I piped the run to
   the tail and lost it. Two subsequent full runs of the same binary were clean (4025/0/11), and the
   failure was not in `GateParityTests` or `VerifyLinuxScriptTests`, which were green in five separate runs.
   This change reads `SqlHarness.sln` and two `.csproj` files; it cannot affect an unrelated test. Flagged
   because it is unaccounted for rather than because the evidence points at this fix.
2. **The marker read is text, like every other read in this file.** A `Condition` on the `PropertyGroup`, or
   an `IsTestProject` set by an imported `.props` rather than declared in the `.csproj`, is not seen.
   `Directory.Build.props` exists and today sets nothing of the sort, and `dotnet format`/`dotnet build`
   would be the surfaces where that bit matters. A project that expressed the marker some other way would be
   treated as a test project — the loud direction (the guards demand a step), not the silent one.
3. **The loud path assumes the sln entry is real.** If a project is deleted but its `Project(...)` line is
   left behind, the guards now go red naming it. That is intended (a stale sln entry breaks
   `dotnet build SqlHarness.sln` anyway), but it is a new way for this suite to fail, so plan 077 must land
   the `.csproj` and the sln entry together.
4. **A marker exclusion has to be earned.** Any project can now opt out of `dotnet test` by adding one
   property, and the two guards will stop requiring it. That is the correct reading of the SDK's own
   contract, but it means "add `IsTestProject=false`" is now a way to silence two gates — reviewable in the
   diff of the `.csproj`, and no easier than any other project property, but worth knowing.
5. **Not run: `just verify`.** [docs/agents/verification.md](../../../docs/agents/verification.md) asks for
   it on implementation work. Its `ui-install`/`ui-check` stages need npm and the dashboard `node_modules`,
   which are outside this defect, and the rest of it is covered above: build with `-warnaserror` via
   `dotnet test` (the test project builds its dependencies), both test projects with the gate's own filter,
   and the format stage verbatim on the changed file. Re-run `just verify` on the merged tree.
6. **The merged tree is still unverified.** Everything above is `opt-windows-gate` plus temporary files. The
   proof that the merge is green is experiment B, which reproduces plan 077's project shape but not plan
   077's actual `Program.cs`/`WorkflowScenarios.cs`, and not whatever else that plan changes in the gates
   themselves.
