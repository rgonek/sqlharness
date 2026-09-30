# 008-mcp-output-schema-proof — final verification gates (closure)

HEAD: `b1e1985` (`test(mcp): final-review fix wave for plan 008 (M2/M3/R5 sweep)`),
branch `feat/plan-008-mcp-output-schema`, worktree `.worktrees/plan-008-mcp-output-schema`.
Gate runs: 2026-09-30, in the worktree. All commands use worktree paths.

## Goal

Discoverable versioned response contract: all 11 MCP tools publish the
existing agent-envelope `OutputSchema` (schemaVersion 1, free-form result)
on `tools/list` via `McpServerTool.Create` (SDK 2.2.0 unchanged);
transport-level conformance for success/error/partial/watch-7/snapshot-8
with correct `IsError`; serialized catalog within 32768 B and default
`CallToolResult` within 16384 B; per-tool result narrowing explicitly
declined with measurements (envelope free-form is the plan minimum).

## Commits (70a4ca2..HEAD, 5)

| Commit | Task | Subject |
|---|---|---|
| 5cc5f84 | T1 | mcp(008-t1): publish envelope outputSchema on all 11 tools |
| 14df2a2 | T2 | mcp(008-t2): transport conformance tests for envelope outputSchema |
| e456af4 | T3 | mcp(008-t3): serialize served-budget toolsets, guard catalog and result budgets |
| 837fa11 | T4 | mcp(008-t4): guard shared free-form envelope, decline per-tool narrowing |
| b1e1985 | fix | test(mcp): final-review fix wave for plan 008 (M2/M3/R5 sweep) |

Each task reviewed clean (spec compliant + Approved; zero Critical/Important
at completion). Final whole-branch review: With fixes → fix wave → scoped
re-review: all findings addressed, no new breakage. Ledger (rulings R1–R6,
deferred minors, R5 race evidence):
`.superpowers/sdd/008-mcp-output-schema/progress.md` (plan workspace,
git-ignored, on this branch's worktree).

## 1. Focused filter (plan gate)

Command:

```powershell
dotnet test D:\Dev\sqlharness\.worktrees\plan-008-mcp-output-schema\SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal
```

| Position | Result |
|---|---|
| Exit | 0 |
| `SqlHarness.Mcp.Tests` | Passed 48 / Failed 0 / Skipped 0 / Total 48 |

(Baseline pre-plan scoped: 40; +8 new: 3 T1, 3 T2, 1 T3, 1 T4 guard.)

## 2. Build with `-warnaserror` (plan gate)

Command:

```powershell
dotnet build D:\Dev\sqlharness\.worktrees\plan-008-mcp-output-schema\SqlHarness.sln --no-restore -warnaserror
```

| Position | Result |
|---|---|
| Exit | 0 |
| Warnings / errors | 0 Warning(s), 0 Error(s) |

## 3. Full suite without Integration (plan gate)

Command:

```powershell
dotnet test D:\Dev\sqlharness\.worktrees\plan-008-mcp-output-schema\SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal
```

| Position | Result |
|---|---|
| Exit | 0 |
| `SqlHarness.Tests` | Passed 1962 / Failed 0 / Skipped 0 / Total 1962 |
| `SqlHarness.Mcp.Tests` | Passed 172 / Failed 0 / Skipped 4 / Total 176 |
| Total | Passed 2134 / Failed 0 / Skipped 4 |

Skips (4, pre-existing, by design): 2x non-native RIDs
(`Published_single_file_server_passes_the_stdio_smoke_on_linux_x64`,
`..._on_osx_arm64`) + 2x live stdio opt-in
(`McpStdioLiveTests.Live_postgres_stdio_drive`, `Live_sqlserver_stdio_drive`).

## 4. Behavior checklist (from the plan)

- 11/11 tools publish outputSchema; serialized catalog within limit:
  served `tools/list` 24239/32768 B (11 × 973 B schemas, headroom 8529 B),
  minimal `CallToolResult` 994/16384 B — PASS (T1 wiring + T3 guards).
- Controlled outcomes do not become isError: watch exit 7 and snapshot
  exit 8 served with `IsError=false`; partial (matrix) with `IsError=true`;
  failures with `IsError=true` — PASS (T2 live envelopes + `ValidateEnvelope`
  + served-schema cross-checks).
- Tests cover adapter/runner behavior, not name lists or constants:
  schema-content equality, served wire shape, live envelope validation,
  real wire-byte measurement — PASS (per-task RED-then-GREEN evidence in
  task reports).
- Per-tool result narrowing only with budget fit + conformance test:
  declined with measurements (narrowing sketch +156 B/tool lower bound;
  honest narrowing converges back to free-form; strict narrowing would lie
  about null/degraded/raw shapes) — free-form envelope locked by a
  served-level drift guard — PASS (T4 branch b, R2).

## 5. No live/platform proof (explicit)

- No live DB: live stdio tests skipped (see §3); out of scope by plan.
  No secrets or user artifacts touched; synthetic `SQLHARNESS_HOME` only.
- Platform proof: runs on Windows x64 only; non-native RIDs skipped (see §3).

## 6. Deferred / parked items (from the SDD ledger)

- M1 (leave, final-review triage): tool count pinned symbolically
  (`ToolNames.Count`/`ExpectedTools.Length`) — single source of truth beats
  a magic 11.
- M2 (fixed in wave `b1e1985`): double `Scope()` → single shared scope
  local (no `using`: `McpScope` is not `IDisposable`); same pattern fixed
  at a second site.
- M3 (fixed in wave `b1e1985`): served contract test now pins the tool
  name set, not just the count.
- M4/M5 (leave, project-wide convention): `Assert.Single` text-block
  pattern in 3 spots; adapter contract emits exactly one text block.
- R5 race (contained, tracked residual): SDK input-schema inference can
  include injected `ctx` (+~12 KB) when toolsets are created concurrently
  in-process (4/4 full-run failures pre-T3, 0 reproductions since; isolated
  served catalog stable 24239 B; production single sequential `CreateTools`
  unaffected by construction). Containment: all in-process served-toolset
  creators joined to serialized `McpScopeHome` (T3 + fix wave). Known
  remainder: 3 in-process seam tests in `McpStdioProcessTests` must stay in
  the sibling serialized collection (xUnit one-class-one-collection);
  mitigation if it ever bites: move them to a `McpScopeHome` class.
- R6 (controller): declined the optional served `result:true` line —
  covered by T2 served pins (ruling R2).

## 7. Residue check

Main checkout (`D:\Dev\sqlharness`, branch `main`): this session ran only
read-only test runs there (BASE full-suite reference, §R5 evidence) and
created the ignored worktree directory. No source/test edits landed outside
the worktree. Closure commit (this proof + index row) is on the branch only;
nothing pushed (plan forbids push without order).

## 8. Controls

- `git diff --check` (70a4ca2..HEAD): exit 0.
- `git status --short` (worktree, pre-docs): clean — all work committed.
- `plans/README.md`: 008 row set to DONE by the closure commit (index entry
  points to this file).
