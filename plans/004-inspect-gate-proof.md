# Plan 004 — evidence: inspect under the shared DB-operation gate (R1)

Branch: `fix/plan-004-mcp-inspect-gate` (worktree `.worktrees/plan-004-mcp-inspect-gate`,
start `8349fa7`). No merge/push — waiting for a decision, like 001/002.

Commits: `ff5d0b6` T1 RED tests, `32f0cc2` T2 gate fix GREEN,
`9735b1a` T3 release tests, `bc5758c` T3 docs.

## Change (5 files, +249/−11)

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — `InspectAsync` via
  `RunDbAsync("sqlharness_inspect", ctx, null, null, …)`; mapping
  (`MapInspect` + `ThrowIfUnknown`) and timeouts per kind (ping 5, others 30)
  untouched; signature unchanged, tool count 11.
- `src/SqlHarness.Mcp/McpExecutionGate.cs` — `DbTools += "sqlharness_inspect"`;
  `IsDbTool` covers exactly six DB tools.
- `tests/.../McpLifecycleTests.cs` — blockages: query↔inspect, inspect↔query+inspect,
  capabilities/validate available, six-classification, release after rejection
  of mapping / a Core error / host-shutdown.
- `tests/.../McpCancellationTests.cs` — rename midflight-cancel to inspect
  (superset: `CancelObserved` + `TryEnterDb` + next-inspect), deadline 1 s.
- `docs/mcp.md` — inspect in the gate list, safe-local without inspect (3 sentences).

## Evidence (controller, worktree, 2026-09-29)

- Focus `McpLifecycleTests|McpCancellationTests`: 28 passed, 0 failed.
- `dotnet build --no-restore -warnaserror`: 0 warn, 0 error.
- Full `--filter 'FullyQualifiedName!~Integration'`: Core 1915/0/0,
  MCP 154 passed, 0 failed, 4 skipped (2 foreign-RID + 2 live opt-in).
- `git diff --check`: 0. Discriminating regression evidence: T1 RED 3/24 on the old
  code (inspect answered success instead of BUSY) → T2 GREEN 24/24 without test edits.
- Final review of the whole branch: Ready to merge, 0 Critical/Important.

## Rulings and deferrals (ledger `.superpowers/sdd/004-mcp-inspect-gate/`, removed after review)

1. Preflight clean — start of T1 (no conflicts with the plan and no 003 drift).
2. T3 Important (no RED phase for the release tests) parked without a fix round:
   the release assertions pass vacuously on the old code (without a gate there is no slot
   to leak), so a RED run does not discriminate; the final review confirmed.
3. Deferred minor items (none block the merge): holder-assertion without `Text()`,
   the `local*` prefixes, the `discovery` phrase in `McpExecutionGate.cs:71`,
   EOF only via hostShutdown, report wording budget-vs-timeout.

## Gaps

No live DB (explicit; live tests skip). Flake
`Progress_fires_only_for_a_client_token…` pre-existing — green in these runs
(focus 28/28, full 154/0/4); the baseline diagnoses it in plan 005.
