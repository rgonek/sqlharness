# Plan 099 final documentation fix report

Date: 2026-10-20
Worktree: `D:\Dev\sqlharness\.worktrees\plan-099`
Base reviewed: `11d0f68`
Verified product revision: `dd8b9e93`

## Finding and change

The whole-branch review found that PowerShell could resolve `npm` to `npm.ps1`, causing the published `npm exec -- shadcn ...` command to print npm's version while exiting successfully. README now publishes the portable `npm exec` preview, diff, and apply commands and the equivalent tested Windows PowerShell `npm.cmd exec` commands. The PowerShell form is explicitly labeled for this shim behavior.

The review and Task 4 re-review are archived as [final-review-11d0f68.md](final-review-11d0f68.md) and [task-4-rereview.md](task-4-rereview.md). Task 4's re-review approved the prior evidence fixes. The final whole-branch review's Important command finding is addressed; two Minor observations are retained as separate follow-ups: byte-preserved raw log whitespace makes the full-range diff check fail, and unchanged CSS retains a 150 ms tooltip enter animation with reduced motion emulated. Their evidence remains intact.

## Exact read-only command checks

Run from `src/SqlHarness.Dashboard/ui` in Windows PowerShell, using only the already installed exact `shadcn@4.22.1`. No install, version resolution, or component application was run.

```text
npm.cmd exec -- shadcn --version
exit 0
4.22.1

npm.cmd exec -- shadcn add accordion --dry-run
exit 0
- Resolving items.
┌ shadcn add accordion (dry run)
│
├ Files (1) +1 new
│ + src\components\ui\accordion.tsx  create
│
├ Dependencies (1)
│ + cn
│
│ 1 file, 1 dep
│
│ Run with --diff to view changes.
│ Run with --view to view file contents.
└ Run without --dry-run to apply.
```

The preview proposed one new component file and the existing `cn` dependency. The dry run applied nothing.

## Review scope ledger

The reviewer declined to judge Plan 100's control inventory, native-to-shadcn conversions, and interaction behavior because this branch implements Plan 099's CLI, stylesheet import, and contributor workflow. Future registry-generated component safety was declined because no generated component was applied; any future apply requires its own review. Linux and live database behavior were declined because neither is required by this run; both remain unverified. Existing localhost:3000 `ECONNREFUSED` diagnostics were left to Plan 091 because the passing Windows gate provides no evidence tying them to this branch's dependency/import changes. These decisions and rationales are also recorded in [Plan 099 verification evidence](../../099-verification-evidence.md).

The seven new HIGH development-tool audit findings remain visible under the existing dependency-maintenance disposition. This wave does not alter that disposition. The product gate remains the already-passed `just verify` on `dd8b9e93`; product code is unchanged, so it was not rerun. The branch remains unintegrated, and final Sol low scoped re-review is pending.

## Scoped checks and self-review

Exact scoped check results after adding this report:

```text
git diff --cached --check -- README.md plans/README.md plans/099-shadcn-cli-restoration.md plans/099-verification-evidence.md plans/099-evidence/README.md plans/099-evidence/task-reports/final-review-11d0f68.md plans/099-evidence/task-reports/task-4-rereview.md plans/099-evidence/task-reports/final-fix-report.md
exit 0; no output

Local Markdown target scan across those eight files
broken-local-links=0

git diff --cached --exit-code -- src/SqlHarness.Dashboard
exit 0; no product diff
```

The documentation-only change does not warrant repeating the Windows product gate.

Self-review confirmed the PowerShell instructions cover preview, diff, and apply; the dry-run and version checks use the published syntax and exact installed version; the review scope boundaries remain explicit; and the final status does not claim integration or Linux/live verification.
