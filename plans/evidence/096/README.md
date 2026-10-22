# Plan 096 archived evidence

This directory holds tracked verification output and the final durable whole-branch review record for Plan 096. SDD briefs, implementation reports and the working ledger remain in the ignored `.superpowers/sdd/096-bounded-large-parameter-files` workspace.

- `task-0-baseline.log`: pre-change baseline.
- `task-1-red-*.log` and `task-1-fix1-*.log`: meaningful regression red and green runs.
- `task-2-green-*.log`: Core/CLI and MCP task 2 green runs.
- `task-3-*-focused-green.log` and `task-3-mcp-focused-final.log`: focused Core/CLI and MCP green runs.
- `task-3-offline-workflow.log`: detailed large-file fixture measurements, including byte counts and output/call counts.
- `task-3-catalog-budget.log`: served MCP catalog byte measurement.
- `task-3-format-*.log` / `.exit`: format normalization and direct branch check outputs.
- `task-3-just-verify-console.log`, `task-3-just-verify.exit`, and the `gate-*.log` files: final Windows gate output and stage logs.
- `final-fix-help-red.log` / `final-fix-help-green.log` and matching `.exit` files: generated CLI help assertion before and after the contract fix.
- `final-fix-format.log` / `.exit`, `final-fix-build.log`, `final-fix-ui-check.log`, and `final-fix-*-tests.log`: final format check and Windows gate stage output for product revision `478d2d2`.
- `final-fix-verify-summary.log` / `.exit`: final Windows gate command, tested product revision/tree, stage counts and direct exit.
- `final-fix-revision.txt` and `final-fix-tree.txt`: exact product revision and tree checked by the final gate.
- `whole-review-b9ea0fd.md` and `whole-rereview-752a653.md`: original whole-branch review and approved final scoped re-review. The review ledger and all implementation-time rulings are recorded in `plans/096-verification-evidence.md`.
