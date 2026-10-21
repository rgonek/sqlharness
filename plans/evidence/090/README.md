# Plan 090 review and verification archive

This folder preserves the compact text evidence needed to audit feature-branch completion without relying on ignored SDD scratch files. The original reports and logs are copied under their original basenames. Ten raw `.log` files are explicitly tracked in Git. The scoped `.gitattributes` rule disables line-ending conversion and trailing-blank checks for these captures; `git hash-object --no-filters` matches the indexed blob for all 10 files.

- `baseline.log`, `task-1-fix-tests.log`, `task-1-fix-format.log`, `format.log`, and `gate-console.log`, with `ui-install.log`, `ui-check.log`, `build.log`, and both test-project logs: focused baseline/fix runs, the initial formatter failure and passing repair, and the Windows gate. `gate-console.log` records the full-gate direct exit code; the focused and formatter logs preserve their own direct exit codes.
- `task-1-report.md` and `task-2-report.md`: implementer evidence and final closure notes.
- `task-1-review.md`, `task-1-rereview.md`, `task-2-review.md`, `whole-review.md`, and `final-rereview.md`: task and branch review verdicts, including the final Sol Minor findings and the scoped review confirming both fixes. The final re-review's log-packaging Important was resolved by force-tracking the ten exact `.log` files; its historical finding remains in the archived report.
- `progress.md`: the SDD ruling and chronological status ledger, including the source-based staging lifetime decision.

Diffs, task briefs, generated build output, and large artifacts are intentionally excluded. The durable evidence summary is [Plan 090 verification evidence](../../090-verification-evidence.md).
