# 009-safety-contract-proof — final verification gates (closure)

HEAD: `29b3102` (`test(009/final): pin no read-only claim in CLI watch help and capabilities entry`),
main (work without a worktree — decision in the ledger: subagents inherit the workspace root;
other people's changes untouched, `git status` clean apart from the scope).
Gate runs: 2026-09-30, on main. No push (the plan forbids it without an instruction).

## Goal

The static safety boundary visible to clients: capabilities/validate carry
versioned metadata (`analysisKind: static-visible-effects`,
`analysisContractVersion: 1`, `hiddenEffectsVerified: false`,
`objectAndPermissionStatus: unknown`); the query/validate/inspect/watch
descriptions state the static check of visible effects and the DB role prepared
outside the harness; `ClassificationLabel` unchanged in meaning.

## Commits (0227180..HEAD, 11)

| Commit | Task | Subject |
|---|---|---|
| 13477be | T1 red | test(009/T1): red regression witness for versioned static-analysis boundary |
| a58c15c | T1 | feat(009/T1): additive versioned static-analysis boundary on capabilities and validate |
| 1cbafab | T1 | test(009/T1): update pinned byte observations for larger capabilities-validate JSON |
| 993e3c4 | T2 red | 009/T2: regression test pinning static-visible-effects query/validate descriptions |
| 84ec9f8 | T2 | 009/T2: query/validate descriptions state static-visible-effects boundary, DB role outside harness |
| 1d3052b | T2 docs | 009/T2: docs/mcp.md query/validate rows match static-visible-effects boundary |
| 7493ad9 | T3 red | test(009/T3): red witness for preflight field stability, denylist, no-connection, no-side-effects |
| 361280b | T3 | fix(009/T3): drop read-only overclaim from MCP inspect/watch descriptions |
| 914238c | T4 | docs(009/T4): strict profile assessment as design-only draft |
| 86c3b3e | final fix | fix(009/final): align CLI/capabilities watch wording with MCP static visible-effects check |
| 29b3102 | final test | test(009/final): pin no read-only claim in CLI watch help and capabilities entry |

Each task reviewed clean (spec ✅ + Approved; zero Critical/Important).
Final whole-branch review: With fixes (1 Minor: CLI watch `read-only`) →
fix wave → scoped re-review: ADDRESSED, no new breakage.
Scope corrections with justification: `byte-budgets.json` (byte pins for the larger
JSON; upper budgets unchanged), the CLI files `SqlHarnessCli.cs`/`ValidateCommand.cs`
(the CLI query/validate descriptions must live there — the plan omitted them from the list).

## Gates (2026-09-30, HEAD 29b3102)

- Plan filter
  `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests'`:
  SqlHarness.Tests 73/73, SqlHarness.Mcp.Tests 33/33 → exit 0
  (after the final fix: 107 passed in the plan filter + AgentWorkflow/Command).
- `dotnet build SqlHarness.sln --no-restore -warnaserror`: 0 warnings, 0 errors.
- Full suite without Integration: Core 1997 passed / 0 failed / 0 skipped;
  MCP 181 passed / 0 failed / 4 skipped (pre-existing: 2 foreign-RID smoke + 2 live opt-in).
- `git diff --check`: 0. `git status --short`: clean (only commits in scope).
- Red → green: T1 5 witness failures, T2 1 fail, T3 1 fail — each on unchanged
  code before the implementation; T4 a documentation step (green filter = no regression).

## Result documents

- `plans/009-strict-profile-assessment.md` — the stricter profile design
  (TEMP, SECURITY DEFINER, operators, external effects); without READ ONLY
  implementation and role switching.
- This evidence: `plans/009-safety-contract-proof.md`.

## Gaps and leftovers (explicit)

- No live DB (the plan forbids it; tests on fake/synthetic HOME; live tests skip).
- Parked by a ruling (ledger): the phrase `watch` / `bounded read-only query`
  in `AGENTS.md` — real, docs-only, the same overclaim as the fix wave; no second
  fix wave per the skill; a follow-up candidate. Nothing is built on it.
- Deferred minors (non-blocking): normalization of CRLF→LF line endings on
  touched files; the T3 red-witness methodology (reported honestly).
