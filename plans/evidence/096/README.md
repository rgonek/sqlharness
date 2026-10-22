# Plan 096 archived evidence

This directory holds raw, tracked verification output for Plan 096. SDD briefs, reports and review packages remain in the ignored `.superpowers/sdd/096-bounded-large-parameter-files` workspace.

- `task-0-baseline.log`: pre-change baseline.
- `task-1-red-*.log` and `task-1-fix1-*.log`: meaningful regression red and green runs.
- `task-2-green-*.log`: Core/CLI and MCP task 2 green runs.
- `task-3-*-focused-green.log` and `task-3-mcp-focused-final.log`: focused Core/CLI and MCP green runs.
- `task-3-offline-workflow.log`: detailed large-file fixture measurements, including byte counts and output/call counts.
- `task-3-catalog-budget.log`: served MCP catalog byte measurement.
- `task-3-format-*.log` / `.exit`: format normalization and direct branch check outputs.
- `task-3-just-verify-console.log`, `task-3-just-verify.exit`, and the `gate-*.log` files: final Windows gate output and stage logs.
