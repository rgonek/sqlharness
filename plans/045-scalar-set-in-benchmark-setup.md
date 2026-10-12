# Plan 045: Allow proven same-batch scalar assignments in SQL Server setup

> Read current source before editing. Inspect drift since `729d88f` in the scope below and adapt implementation details within this authorized intent. A changed file alone is not a STOP condition. Escalate an incompatible contract or missing required proof. This file plans future implementation; writing it runs no SQL.

## Status

- Status: DONE (implementation merged to main `3e8236f`; both branch gates passed on `eb64089`)
- Priority: P2
- Effort: S
- Risk: LOW
- Depends on: none
- Planned at: `729d88f`, 2026-10-11

## Why this matters

A scalar assignment proven safe in query is rejected in setup even though it affects only a local variable and the same RHS analysis already runs. This unnecessary asymmetry forces benchmark preparation to be rewritten. The approved change is only the existing plain scalar assignment rule.

## Current state

- src/SqlHarness.Core/SqlSafety.cs:370 classifies a proven SetVariableStatement as ReadOnly.
- Setup loop at :334 accepts only session-local writes or SelectStatement, therefore rejects that classification.
- Global inspection already checks external/stateful/cross-database sources on the RHS.
- Named table types, compound assignments, session SET options and cross-batch variables are distinct unsupported contracts.

## Scope

src/SqlHarness.Core/SqlSafety.cs; SqlValidation.cs only if required for parity; SetupSqlExecution.cs only for demonstrated split-analysis integration; tests/SqlHarness.Tests/SqlSafetyTests.cs and setup execution tests; tests/SqlHarness.Mcp.Tests/McpMappingTests.cs; Capabilities.cs; README.md; AGENTS.md; docs/mcp.md; plans/011-syntax-contract.md.

No dashboard API, user configuration edits, live production queries, commit, merge, installation or push is authorized by this plan. Match existing xUnit and fake-provider conventions; use synthetic fixtures, not operational SQL/data. Persistent-mutation controls, target locking, timeout and byte limits retain their separate purpose.

## Steps and acceptance

1. Add a regression where DECLARE @v int; SET @v = 2; is allowed in setup, plus a same-batch reassignment used by temp population. Reuse the proven assignment classifier; do not approve all SET statements by syntax.
2. Let setup accept that proven scalar assignment while preserving global RHS inspection and declaration-before-use. Keep setup-to-query variable lifetime unchanged.
3. Cover missing declaration, scalar/table confusion, compound/member assignments, session/transaction SET, unsafe RHS and cross-batch references as denials.
4. Cover the setup parameter split introduced by 038: a local declared before the split cannot be used after it. Do not move/reorder SQL or interpolate parameter values to support such a case.
5. Verify CLI validate --usage setup, MCP validation and execution preparation agree. Update capability/docs statements that SET is forbidden in setup.

After each logical implementation change, run the relevant subset of the focused test command below. Build first when needed. All selected tests must pass; a skipped required provider scenario is not proof.

## Verification

- Build: `dotnet build SqlHarness.sln -warnaserror` — exit 0, no warnings/errors.
- Focused: `dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~SetupSql|FullyQualifiedName~SqlValidation|FullyQualifiedName~McpMapping"` — exit 0, at least one selected test, no failed selected tests; confirm the intended regression tests actually ran.
- Final Windows gate: `pwsh -NoProfile -File scripts/verify.ps1` — exit 0 and literal `verify: OK`.
- Final Linux gate: `pwsh -NoProfile -File scripts/verify-linux.ps1` — exit 0 and successful completion of its full gate on ext4.
- `git diff --check` — exit 0.

Run one final gate on each required platform after the final relevant code change. Repeat or broaden verification only for a remaining failure or risk. Record literal results and skipped live checks separately.

## Done criteria

- [x] Proven same-batch plain scalar SET is allowed in setup.
- [x] Existing unsafe SET variants remain denied.
- [x] Setup split and separate measured-batch variable lifetimes remain correct.
- [x] Offline validation and execution preparation apply the same rule.
- [x] Focused and required platform gates pass, and plans/README.md records evidence.
- [x] Source, CLI/MCP descriptions and documentation agree on the final behavior.

## Execution evidence — 2026-10-12

- Commits: `869cf01` safety/lifetime regressions; `6302c8b` CLI/MCP parity and documentation; `c36a68b` capability byte golden; `eb64089` agent digest. Incoming main was preserved at `b607466`; implementation was independently merged to main at `3e8236f` during verification.
- Regression evidence: 8 expected RED failures before the safety change, then all 25 Plan045 cases passed. Two new CLI/MCP split-lifetime cases failed before the SqlValidation parity correction. Final focused selection: 755 passed, zero failed/skipped; byte workflow fix: 5 passed.
- Final Windows gate on `eb64089`: exit 0, literal `verify: OK`; Core 3705 passed, MCP 390 passed/4 skipped; build zero warnings/errors and format passed.
- Final Linux gate on the same `eb64089`, sequentially on WSL ext4: exit 0, literal `verify-linux: OK`; Core 3705 passed, MCP 391 passed/3 skipped; all stages including format passed.
- Both task reviews and whole-branch review approved after scoped re-reviews. Initial Windows failure was a stale capability byte golden; a later UI check could not find `tsc`, then direct typecheck and the full fresh gate passed. Neither failed attempt is counted as a pass.
- No live database checks; provider execution uses synthetic fixtures. Skips are opt-in live MCP and foreign-platform smoke tests. These gates prove the branch at `eb64089`, not newer main changes from other plans. No push performed by this execution.

## Stop conditions and maintenance

Stop if supporting the assignment requires weakening batch-scope or parameter-split lifetime checks. Such SQL needs a clear unsupported-lifetime diagnostic.

Future changes must preserve the specific boundary tested here rather than adding generic hiding or syntax restrictions. Update the plan if implementation evidence changes its premise; historical tests are evidence to examine, not authority to preserve obsolete policy.
