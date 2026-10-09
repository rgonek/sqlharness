# W6 report — guard local dashboard credentials from agent shells

## Result

Added a shared Python pre-tool hook with Claude Code and Codex configuration examples. It denies common shell read commands targeting `dashboard.json` or the `.sqlharness/*.json` glob, and leaves an explicitly named `targets.json` read allowed. Added the agent-facing warning and setup notes to `docs/mcp.md`.

## RED / GREEN

RED was recorded before the hook existed:

```text
python scripts/hooks/test_protect_sqlharness_config.py
Ran 3 tests
FAILED (failures=4)
AssertionError: '' is not true : hook must emit a JSON decision
```

The failures were the two blocked-read cases and the allowed `targets.json` case for each client adapter; the missing hook emitted no JSON decision.

After implementing the hook:

```text
python scripts/hooks/test_protect_sqlharness_config.py
Ran 3 tests in 0.781s
OK
```

The tests execute the hook as a subprocess with real hook event JSON. They assert Claude's `permissionDecision: deny` for `cat ~/.sqlharness/*.json`, Codex's `decision: block` for `type %USERPROFILE%\.sqlharness\dashboard.json`, and empty allow output for `cat ~/.sqlharness/targets.json` under both clients.

## Verification

- `python scripts/hooks/test_protect_sqlharness_config.py` — passed, 3 tests.
- `git diff --check` — passed.
- `pwsh ./scripts/verify.ps1` — UI checks passed (25 files, 103 tests); build passed with 0 warnings and 0 errors; .NET tests passed (SqlHarness.Tests: 3,221 passed; SqlHarness.Mcp.Tests: 255 passed, 4 skipped). The format stage failed on existing missing-final-newline issues in `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:606` and `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1005`. Neither file is part of W6, and neither was changed.
- `pwsh ./scripts/verify-linux.ps1` — stopped at `sync` with exit 128 because WSL resolved the worktree gitfile to `/mnt/d/Dev/sqlharness/.worktrees/plan-038/D:/Dev/sqlharness/.git/worktrees/plan-038`, which is not a repository. No Linux verification stages ran.

## Self-review and limits

- The hook output contains only a fixed reason and never reads or prints file contents or token values.
- Protection applies to common shell read commands (`cat`, `type`, `Get-Content`/`gc`, `more`, `less`, `head`, `tail`) when the command text includes `dashboard.json` or `.sqlharness/*.json`. It is an optional agent guardrail, not a complete filesystem policy; other tools or indirect reads can bypass it.
- Client configurations are examples to merge and review. The Codex sample assumes the script has been copied to `~/.codex/hooks/`; Claude uses the repo-local path.
- Dashboard token rotation remains out of scope.
