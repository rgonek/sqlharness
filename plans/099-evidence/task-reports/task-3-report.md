# Plan 099 Task 3 report

Date: 2026-10-20
Worktree: `.worktrees/plan-099`
Starting revision: `dd8b9e9` (`ui: restore shadcn stylesheet export`)

## Documentation changes

- Replaced the current README workaround with the maintainable local CLI workflow: shadcn is a devDependency, `src/index.css` uses the package stylesheet import, `components.json` selects base-nova, and `src/components/ui` is owned editable source. The add workflow previews with `--dry-run`, inspects source changes with `--diff`, then applies after review without blanket overwrite.
- Documented explicit stable CLI upgrades, package and lockfile review, registry source review, relevant UI checks, and that registry output can change independently of the CLI lockfile.
- Added a supersession link before the preserved historical shadcn instructions in the 2026-10-08 plan. Plan 071 contains no matching shadcn guidance, so it needed no edit. Existing historical statuses, results and command transcripts were left intact.
- Updated the plan index: Plan 099 is implemented on this branch with final verification/review pending and is not integrated; Plan 100 remains plan-only and depends on actual integration of Plan 099.

## Stale-guidance classification

Ran `rg -n "shadcn-tailwind|npx shadcn|never edited|stay unchanged" README.md docs plans`.

- `README.md` contained the current contributor workflow and was replaced.
- `docs/superpowers/plans/2026-10-08-dashboard-settings-errors-theme.md` contains historical implementation guidance. Its old text remains unchanged and now links to the revised superseding design.
- `docs/superpowers/plans/2026-10-07-activity-journal-phase4.md` contains historical generated-file language and old command transcripts; these were retained as historical evidence.
- Plan 071 had no matching guidance. Other matches (for example, `plans/003-scope-contract.md`) are unrelated uses of the search terms.

## CLI preview and provenance

The installed CLI is shadcn `4.22.1`, as specified for this plan. `src/SqlHarness.Dashboard/ui/package.json` pins `"shadcn": "4.22.1"`; `package-lock.json` resolves `https://registry.npmjs.org/shadcn/-/shadcn-4.22.1.tgz`. `components.json` selects `base-nova`, neutral, and has no custom registries.

From `src/SqlHarness.Dashboard/ui`, ran:

```text
npm.cmd exec -- shadcn add accordion --dry-run
```

The preview reported one new file, `src/components/ui/accordion.tsx`, and one dependency entry, `cn`. The `cn` package is already present in `package.json`; the preview proposed no stylesheet or other source edits. No files were applied by the dry run. Registry-generated component output remains independent of the npm lockfile's pinned CLI tarball.

## Checks and self-review

- Confirmed the worktree remained on branch `plan-099` and checked status after the preview; the dry run did not mutate UI files.
- Manually checked the new supersession link resolves from the 2026-10-08 plan to `docs/superpowers/specs/2026-10-18-shadcn-cli-and-primitive-adoption-design.md` and that the plan report link resolves to this file.
- `git diff --check`: passed with exit 0.
- Independent task review: approved with no findings; see `.superpowers/sdd/099-shadcn-cli-restoration/task-3-review.md`.
- No UI check or full repository gate was run, as this task changes documentation only and its instructions explicitly exclude those checks.

## Review

The task review approved the committed documentation with no Critical, Important or Minor findings. Task documentation commit: `79d4ead` (`docs: restore shadcn contributor workflow`).
