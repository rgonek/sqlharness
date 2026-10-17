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
   **Closed as not attributable by [the review](integration-fix-review.md), which ran the project four more
   times (including both projects concurrently, and once with no filter at all) without reproducing it, and
   named the pre-existing families it most likely belongs to — `DashboardHostTests` and
   `DashboardHousekeepingTests`. Not chased further here, deliberately.**
2. **The marker read is text, like every other read in this file, and the review found my direction claim
   here wrong.** The original text of this item said a marker expressed some other way "would be treated as
   a test project — the loud direction (the guards demand a step), not the silent one". **That is false as a
   blanket statement.** The pattern matches the marker wherever it appears in the file text, so three shapes
   are *silently excluded* even when the real value is `true`:

   | Project text | Matches? | Direction |
   |---|---|---|
   | `<PropertyGroup Condition="…"><IsTestProject>false</IsTestProject></PropertyGroup>` | **yes** | **silent** — excluded even when the condition is false |
   | `<!-- <IsTestProject>false</IsTestProject> -->` (commented out) | **yes** | **silent** — excluded |
   | `<IsTestProject>false</IsTestProject><IsTestProject>true</IsTestProject>` (MSBuild: last wins → `true`) | **yes** | **silent** — excluded, and wrong |
   | `<IsTestProject Condition="…">false</IsTestProject>` (condition on the element) | no | loud — included, the guards demand a step |
   | marker set by an imported `.props` | no | loud — included, the guards demand a step |

   So the regex is unanchored to a `PropertyGroup` and does not model MSBuild's last-wins evaluation: it
   answers a slightly different question from the one it stands in for, and it can under-report. The
   failure it produces is the pre-fix one — a guard that demands a step for a project `dotnet test` skips —
   not a new class of surprise, which is why the review accepted the code for the merge. It cannot bite
   plan 077: its real `.csproj` declares the marker in an unconditioned `PropertyGroup` directly in the
   file, and the guards are green on that exact file. Closing this properly means parsing the project as
   XML and evaluating `PropertyGroup` conditions, which needs MSBuild evaluation to do honestly; not worth
   it for this defect, and it is **recorded here as open**. If it is closed later,
   `Directory.Build.props` is the file to look at first — it exists and today sets nothing of the sort.
   No comment in `GateParityTests.cs` claimed the loud direction, so no code comment needed correcting.
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

---

# Fix round 1

Requested by [the review](integration-fix-review.md) (changes requested; the shipped filter logic confirmed
correct and left alone). Three items: pin the wiring, correct a claim in this report, delete a dead `throw;`.

## F1 — the filter's wiring is now pinned by tests

The review's proof stands: deleting `.Where(...)` left `GateParityTests|VerifyLinuxScriptTests` green, because
the shipped theory pinned the predicate and nothing pinned the step that applies it, and no project on this
branch declares the marker. Fixed in the shape the review named.

**The seam** (`tests/SqlHarness.Tests/GateParityTests.cs`):

```csharp
internal static string[] SolutionTestProjects() =>
    SolutionTestProjects(ReadRepositoryFile("SqlHarness.sln"), project => ReadRepositoryFile(project.Split('/')));

internal static string[] SolutionTestProjects(string solution, Func<string, string?> readProject) =>
    Regex.Matches(solution, @"""(?<project>tests\\[^""]+\.csproj)""", RegexOptions.IgnoreCase)
        .Select(match => match.Groups["project"].Value.Replace('\\', '/'))
        .Where(project => DotnetTestWouldRun(project, readProject))
        .OrderBy(project => project, StringComparer.Ordinal)
        .ToArray();
```

The parameterless method is unchanged as the production entry point — same name, same `internal`, same
`SqlHarness.sln` read, same `ReadRepositoryFile` seam — so both consuming guards are untouched. The reader is
supplied per call, so `DotnetTestWouldRun` takes it as a parameter and treats "no text" as the loud failure
it always was.

**The theory**, `The_filter_demands_only_the_projects_dotnet_test_would_run_and_reports_one_it_cannot_read`,
3 rows, no file on disk and no edit to `SqlHarness.sln`:

| Row | Synthetic solution | Synthetic reader | Expected |
|---|---|---|---|
| A | Tests, Mcp.Tests, WorkflowHost | refuses `…Mcp.WorkflowHost…` | throws, message names the project |
| B | Tests, Mcp.Tests, WorkflowHost | all four texts, host declares `<IsTestProject>false</IsTestProject>` | exactly `[Mcp.Tests, Tests]` |
| C | the above plus `Mcp.Tests.Extra` (no marker) | as B | exactly `[Mcp.Tests.Extra, Mcp.Tests, Tests]` |

Two details worth knowing, both of which cost me a red run while writing it:

- The synthetic `Project` lines are spelled with **backslashes**, as `SqlHarness.sln` spells them, because
  the filter's regex requires them; the expected lists use the `/` the filter normalises to. `Entry()` does
  the `Replace('/', '\\')`, so the theory now also pins that normalisation. Written the other way round, the
  filter returns `[]` and all three rows fail — which is how the mistake surfaced.
- The expected lists are in **Ordinal** order (`Mcp.Tests.Extra` < `Mcp.Tests` < `Tests`), so the
  comparisons pin the ordering as well as the membership.

The unreadable row's reader **throws** `FileNotFoundException("Could not locate repository file: …")`, which
is exactly what `RepositoryFile.Locate` throws, so row A exercises the production path rather than a
stand-in for it. The seam's `null` return is handled too (`{failure ?? "the reader returned no text for it."}`),
for a reader that returns nothing instead of throwing.

### F1 teeth proof

Removed the single `.Where(project => DotnetTestWouldRun(project, readProject))` line, rebuilt, ran the gate
filter:

```
Build succeeded.  0 Warning(s)  0 Error(s)
Failed SqlHarness.Tests.GateParityTests.The_filter_demands_only_the_projects_dotnet_test_would_run_and_reports_one_it_cannot_read(unreadable: null, thirdTestProject: False, expected: [...])
Failed SqlHarness.Tests.GateParityTests.The_filter_demands_only_the_projects_dotnet_test_would_run_and_reports_one_it_cannot_read(unreadable: null, thirdTestProject: True,  expected: [...])
Failed SqlHarness.Tests.GateParityTests.The_filter_demands_only_the_projects_dotnet_test_would_run_and_reports_one_it_cannot_read(unreadable: "tests/SqlHarness.Mcp.WorkflowHost/…", thirdTestProject: False, expected: null)
Failed!  - Failed: 3, Passed: 72, Skipped: 0, Total: 75
```

**All 3 rows of `The_filter_demands_only_the_projects_dotnet_test_would_run_and_reports_one_it_cannot_read`
go red, and they are the only failures** — rows B and C on `Assert.Equal() Collections differ` (the marked
host is no longer excluded, and the third test project is joined by a host that should not be there), row A on
`Assert.ThrowsAny() Failure: No exception was thrown`. The build is still warning-free, which is exactly why
the pin was needed. Restored the line: `Passed! - Failed: 0, Passed: 75, Skipped: 0, Total: 75`.

### The production path, re-proven after the refactor

The refactor moved the read behind a delegate, so the real-sln proofs were re-run rather than assumed
(`SqlHarness.sln` edited and restored; the temporary project created and deleted):

| Case | Result |
|---|---|
| sln entry for the host, no file on disk | `Failed! - Failed: 4, Passed: 71, Total: 75`, same 4 guards, same message naming the project |
| the marked `.csproj` on disk (merged-`main` shape) | `Passed! - Failed: 0, Passed: 75, Total: 75` |
| entries and files removed | `Passed! - Failed: 0, Passed: 75`, `git status --porcelain -uall` shows only the test file |

## F2 — the report's claim, corrected

Concern 2 above now carries the review's table and says plainly that "the loud direction, not the silent
one" was wrong as a blanket statement: a `Condition`ed `PropertyGroup`, a commented-out marker, and a
duplicate whose last value disagrees all **match** and are silently excluded, while a conditioned *element*
and an imported `.props` do not match and fail loud. The gap is recorded as **open**, with the reason it is
accepted for the merge (plan 077's real `.csproj` declares the marker in an unconditioned `PropertyGroup`
directly in the file) and the reason closing it properly needs MSBuild evaluation.

No comment in `GateParityTests.cs` made that claim, so no code comment needed correcting. The code is
unchanged in this item — the review accepted it.

Concern 1 also now points at the review's four-run negative result and names the families the flake belongs
to, so the next reader does not re-chase it.

## F3 — the dead `throw;` is gone

`Assert.Fail` is annotated `[DoesNotReturn]` in xunit 2.9.3, so the `throw;` after it was never needed. The
read is now captured rather than returned, the message is built from what went wrong, and the assertion ends
the method:

```csharp
    private static bool DotnetTestWouldRun(string project, Func<string, string?> readProject)
    {
        string? projectFile = null;
        string? failure = null;

        try
        {
            projectFile = readProject(project);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message;
        }

        if (projectFile is null)
        {
            Assert.Fail($"SqlHarness.sln lists {project}, but reading it failed, so whether `dotnet test` runs it is unknown. {failure ?? "the reader returned no text for it."}");
        }

        return !IsMarkedNotATestProject(projectFile);
    }
```

The build is the evidence that `[DoesNotReturn]` is doing its job: `projectFile` is still `string?` at the
return, so without the annotation this would be CS8604 — a warning, and `TreatWarningsAsErrors` is on.
`dotnet build` reports `0 Warning(s)  0 Error(s)`.

## Verification (round 1)

| Check | Result |
|---|---|
| `dotnet test … --no-build --filter 'FullyQualifiedName~GateParityTests\|FullyQualifiedName~VerifyLinuxScriptTests'` | `Passed! - Failed: 0, Passed: 75, Skipped: 0, Total: 75` (72 before round 1, +3 theory rows) |
| `dotnet build … -warnaserror` (via the above) | `0 Warning(s)  0 Error(s)` |
| F1 teeth: `.Where` removed, rebuilt | `Failed! - Failed: 3, Passed: 72, Total: 75` — all 3 rows of the new theory |
| F1 teeth: `.Where` restored | `Passed! - Failed: 0, Passed: 75` |
| Real-`SqlHarness.sln` proofs A / B / after cleanup | 4 guards red / 75 green / 75 green, tree clean |
| `dotnet format SqlHarness.sln --no-restore --include tests/SqlHarness.Tests/GateParityTests.cs --verify-no-changes` | exit 0 |
| `pwsh -NoProfile -Command "Import-Module Pester -MaximumVersion 3.99; Invoke-Pester -Path tests/scripts -EnableExit"` | `Passed: 84 Failed: 0 Skipped: 0 Pending: 0 Inconclusive: 0`, exit 0 |
| `dotnet test … --no-build --filter 'FullyQualifiedName!~Integration'` (whole project, gate filter) | `Passed! - Failed: 0, Passed: 4028, Skipped: 11, Total: 4039` (4036 + 3 rows) |

## Round-1 concerns

1. **The seam is pinned; the one-line delegation to it is not.** The theory proves the two-argument overload
   filters; it cannot prove that the parameterless overload still *calls* it, because on this branch both
   paths return the same two projects (no project declares the marker). Removing the delegation rather than
   the `.Where` would still be green here, and would be found on the merge. Cheap to close only by adding a
   marker-declaring project to the repository, which is the tree that does not exist yet.
2. **Row A pins the loud failure, not which exception.** `Assert.ThrowsAny<Xunit.Sdk.XunitException>` accepts
   any xunit assertion failure from the seam and the assertion then checks the message names the project, so a
   future change that fails for a different reason still has to name the file. `Assert.Throws<T>` in xunit is
   an exact-type match, so this is deliberately the `Any` overload.
3. **The synthetic solution is not parsed as one.** The rows build text with `\n` endings, put the
   `Project` lines before `Global`, and add no configuration rows — enough for a regex over project lines,
   which is all the filter reads. It is not a solution `dotnet` would accept.
4. **`Assert.Fail` being `[DoesNotReturn]` is load-bearing now.** If a future xunit drops the annotation,
   CS8604 appears at the `return` and — because `TreatWarningsAsErrors` is on — the build fails rather than
   passing a possibly-null string into the regex. That is the failure mode I want; it is still a coupling to
   the test framework worth knowing about.
5. Still true from round 0 and unchanged by this round: `just verify` is not run (its UI stages need npm),
   and the merged tree is still unverified beyond the real-file proof B.