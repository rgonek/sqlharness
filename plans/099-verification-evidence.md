# Plan 099 verification evidence

Date: 2026-10-20
Implementation branch: `plan-099`
Verified product revision: `dd8b9e93` (`ui: restore shadcn stylesheet export`)
Documentation revision present during verification: `79d4ead8` (`docs: restore shadcn contributor workflow`)

## Windows repository gate

The controller ran `just verify -LogDirectory D:\Dev\sqlharness\.worktrees\plan-099\.superpowers\sdd\099-shadcn-cli-restoration\scratch\final-gate` from the repository root. It exited 0 on the product tree at `dd8b9e93`. No product code changed after that revision; subsequent changes are documentation and retained evidence. Per [verification guidance](../docs/agents/verification.md), the gate was not repeated for those documentation-only edits.

All required stages passed: `ui-install` (25.8 s), `build` (1m25s), `ui-check` (4m08s), and both .NET test projects (2m26s); formatting was skipped because zero changed C# files. The UI check reported 62/62 test files and 331/331 tests passing. The MCP test project passed 462, skipped 4, failed 0; the main test project passed 4,562, skipped 11, failed 0. The build reported zero warnings and zero errors from .NET; Vite retained its existing large JavaScript chunk warning. UI test output included the existing `DEP0190` warning and refused requests to `::1:3000` and `127.0.0.1:3000`; the stage nevertheless exited 0. The test logs identify skipped live database, platform-specific, and Unix-permission tests. The raw gate logs are tracked as text artifacts under [gate evidence](099-evidence/gate/): `ui-install.txt`, `build.txt`, `ui-check.txt`, `test-SqlHarness.Mcp.Tests.txt`, and `test-SqlHarness.Tests.txt`.

This is feature-branch verification, not verification of an integrated tree. `just verify-linux` was not run. No live database or SQL test was performed.

## npm audit and dependency disposition

The captured baseline and installed-tree `npm audit --json` and `npm audit --omit=dev --json` commands all exited 1 because advisories were present; none is counted as a successful audit. Baseline full audit was 2 LOW, 0 HIGH. After adding the pinned CLI it was 2 LOW, 7 HIGH. The seven new HIGH package names are `shadcn`, `@shadcn/registry`, `fast-glob`, `micromatch`, `braces`, `ts-morph`, and `@ts-morph/common`; the new paths are within the local CLI/registry/glob/AST dependency graph. The reports include the `braces` deeply nested-pattern stack-exhaustion advisory. The production-only audit remained at 2 LOW, 0 HIGH before and after (`monaco-editor` and transitive `dompurify`). These findings are not described as unreachable or risk-free.

Disposition: keep the exact current stable CLI version required by the plan and expose the new development-tool findings for ongoing dependency review; do not mask them with overrides or treat the advisory-bearing exit as success. Task 1's independent review approved the documented disposition. Full JSON reports and their exit codes are in [audit evidence](099-evidence/audits/).

## CLI version, lockfile and preview

The registry returned `latest: 4.22.1` on 2026-10-20; prerelease tags were excluded. The installed devDependency is exact `shadcn@4.22.1`. The lockfile resolves `https://registry.npmjs.org/shadcn/-/shadcn-4.22.1.tgz` with integrity `sha512-LbB4Lro+b0qSVki/tgL5edtKylEnXe1ppxMJC12d7NHPgaT+eSgJGMQULwP72V7X9tbiUq7xfx3VDAfB7jdGoA==`, and `npm ls shadcn --all` showed the single root installation. The dependency comparison recorded 288 new lock entries, no removals, and no version changes to pre-existing packages; `tslib@2.8.1` changed from optional to required through the CLI's `recast` dependency. The retained new-package lifecycle review found no added `hasInstallScript` entries.

The saved dry run `npm.cmd exec -- shadcn add accordion --dry-run` exited 0 and proposed one new file, `src/components/ui/accordion.tsx`, and one dependency entry, `cn`; it proposed no stylesheet or other source edits and applied nothing. `cn` already exists in the manifest. Registry-generated component output remains independent of the pinned CLI tarball. The exact output is tracked in [accordion-dry-run.txt](099-evidence/cli/accordion-dry-run.txt); registry provenance is in [CLI evidence](099-evidence/cli/).

## CSS and browser evidence

The app's three production CSS assets were byte-identical to the Task 1 baseline after switching to the package export. Their byte sizes and SHA-256 values are retained in [CSS evidence](099-evidence/css/css-sha256.txt); tracked Task 2 logs are [task2-build.txt](099-evidence/css/task2-build.txt) and [task2-check.txt](099-evidence/css/task2-check.txt). Task 2 reviewed the installed export and emitted representative checked/unchecked, disabled, open/closed, active-tab, and horizontal/vertical selectors.

Browser inspection used an isolated disposable dashboard home/backend, with no live database or SQL connection. The real Settings page was checked in light and dark themes, including checked and unchecked switches, the theme menu, and an unconfirmed dialog. A synthetic session response enabled tab, keyboard focus, separator, and tooltip inspection; other API calls remained on the disposable backend. Screenshots are retained under [browser evidence](099-evidence/browser/).

Coverage limits: the Settings page had no disabled switch and the app had no vertical separator instance, so those were checked in emitted CSS only. With reduced motion emulated, the existing tooltip still used a 150 ms enter animation; this behavior was present in the unchanged emitted CSS and is recorded rather than claimed as a reduced-motion pass. No page errors were collected.

## Reviews and status

Task 1, Task 2, and Task 3 independent reviews approved their changes; reports and review notes are retained in [task review evidence](099-evidence/task-reports/). The Task 4 re-review approved its evidence fixes; its original review, re-review, and fix report are archived there. The whole-branch review at `11d0f68` found one Important Windows PowerShell command issue, fixed in the documentation-only commit `19e82a3`. The final Sol low scoped re-review approved the correction with no new breakage; its report is archived in [task review evidence](099-evidence/task-reports/). The reviewers recorded two nonblocking observations: byte-preserved raw logs carry whitespace that causes the full-range `git diff --check` to fail, and unchanged emitted CSS retains a 150 ms tooltip enter animation under reduced-motion emulation. Both remain separate follow-ups with their original evidence preserved. Plan 099 implementation, Windows verification, and all reviews are complete on branch `plan-099`; it is not integrated. Plan 100 control conversions remain outside this branch and are blocked until Plan 099's implementation is actually integrated. No merge, push, publication, or live database operation is authorized by this evidence.

### Review scope ledger

The whole-branch reviewer declined to judge Plan 100's control inventory, conversions, and interaction behavior because this branch covers Plan 099 tooling, stylesheet import, and contributor documentation. Future registry-generated component applies were not judged because none was applied here; applying output remains a separately reviewed change. Linux and live database behavior remain unverified because this plan run did not require those checks and no live database operation was performed. Existing localhost:3000 connection-refused diagnostics remain assigned to Plan 091 because the passing Windows gate gives no evidence that they arise from these changes. These boundaries and reasons are retained from the review in [final review](099-evidence/task-reports/final-review-11d0f68.md).

The Important finding is resolved by publishing both portable `npm exec` commands and Windows PowerShell `npm.cmd exec` commands. With the existing installed `shadcn@4.22.1`, the PowerShell version command printed `4.22.1` and the accordion dry run exited 0, proposing one new component file and the existing `cn` dependency; neither command installed or applied anything. See the exact outcomes in the [final fix report](099-evidence/task-reports/final-fix-report.md). The remaining audit disposition is unchanged: seven new HIGH development-tool findings remain visible for dependency maintenance, as described above.

## Evidence index

- [Audits](099-evidence/audits/): baseline/final full and production JSON plus exact exit codes.
- [CLI](099-evidence/cli/): stable tag, package integrity, local version/tree, lock additions, lifecycle metadata, and exact dry-run output.
- [CSS](099-evidence/css/): Task 2 UI check/build logs and CSS hashes/byte sizes.
- [Browser](099-evidence/browser/): Task 2 screenshots from the disposable dashboard fixture.
- [Gate](099-evidence/gate/): four required-stage logs from `just verify`; formatting was skipped because no C# files changed.
- [Task reports and reviews](099-evidence/task-reports/): implementation evidence and independent approvals for Tasks 1–3.
- [Final reviews and fix report](099-evidence/task-reports/): archived whole-branch findings, Task 4 and final Sol low re-review verdicts, and the PowerShell command correction with scoped checks.
