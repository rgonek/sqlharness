# CLI and stylesheet provenance

- `package.json` retains exact `shadcn@4.22.1`; installed package metadata reports version `4.22.1` and maps `./tailwind.css` to `./dist/tailwind.css`.
- `src/SqlHarness.Dashboard/ui/src/index.css` still imports `shadcn/tailwind.css`.
- The reviewed CLI generation and preview outputs from Task 1 are [shadcn-add.log](shadcn-add.log) and [shadcn-preview.log](shadcn-preview.log). Task reports and reviews are archived in [the evidence report set](../task-reports/).
- Comparing Plan 100's base `344e5b7` with tested product revision `3caacc3` shows no changes to `package.json`, `package-lock.json`, or `src/index.css`; there were no dependency changes in this plan.
- Plan 099's [audit evidence](../../099-evidence/audits/) records the registry and lockfile provenance. Plan 100's current audit snapshots and exit codes are archived here as [full](npm-audit-full.json), [production](npm-audit-production.json), and [exit codes](audit-exit-codes.txt).
