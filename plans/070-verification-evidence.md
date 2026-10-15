# Plan 070 verification evidence

The final committed implementation and README revision checked by the Windows gate was `64bbec52b872cfcf7ec2988bf4008e7ea8e0bbec` on branch `feat/plan-070-agent-transcripts`. The plan, index, spec status, and this evidence note were updated afterward in a documentation-only commit; no executable files changed after the tested revision.

## Windows verification

`just verify -LogDirectory .superpowers/sdd/070-dashboard-agent-transcript-matching/task-8-verify-formatted` exited 0 at `64bbec5`. Output ended with `verify: OK  stages=6  3m55s`. The six stage results were UI install skipped because the lockfile was unchanged, UI check passed, restore passed, build passed, tests passed, and the working-tree format stage skipped because it found zero uncommitted C# files. Test totals were 3,598 passed and 11 skipped in `SqlHarness.Tests`, and 381 passed and 4 skipped in `SqlHarness.Mcp.Tests`.

The committed C# changes were checked separately with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef ec20a52`. The first run found formatting diagnostics in 25 branch-changed C# files. The repository formatter was run with `-Fix` in that same branch scope, and the subsequent check returned `format: OK`. The resulting whitespace and final-newline corrections are in `64bbec5`; they do not change behavior. The Windows gate above ran after that correction.

The first Windows gate, `just verify`, exited 1 at test stage on `5f25e19`. The failure was `DashboardHousekeepingTests.Matcher_commits_do_not_keep_an_idle_dashboard_up`: it expected the exact snapshot `title-3` and observed `title-4` because the pass hook can append another transcript title before the assertion. The operation-link assertion had passed, but the exact-title assertion prevented that run from reaching its idle-shutdown assertion. The test now asserts that at least two matcher-owned title updates occurred and still asserts shutdown at the idle deadline. `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardHousekeepingTests"` then passed 14/14 on the correction. The final full gate passed at `64bbec5`.

The final gate output and stage logs were inspected in the ignored SDD workspace; their results are summarized above, and a case-sensitive search for `FAILED` found no matches. The initial failure and focused regression runs are also summarized above. This evidence is self-contained and does not depend on retaining those scratch logs.

## Linux verification and whole-branch review

On 2026-10-15, the operator explicitly waived the Linux gate (`bez bramki linux`). `just verify-linux` was not run; Linux remains unverified. This waiver supersedes the original Plan 070 gate requirement to run both platforms.

The final whole-branch review by Sol at low effort approved the branch: spec compliance and code quality both passed, with no critical or important findings. It recorded two minor test coverage gaps: no endpoint-level assertion for HTTP 503 while SQLite is locked, and no assertion of raw call values plus effective effort when call and transcript effort coexist. These are coverage improvements, not demonstrated defects. Recommend adding the focused checks when this area next changes. The review relied on the Windows result above and does not establish Linux behavior. No merge, push, live database operation, install, or publication was performed.
