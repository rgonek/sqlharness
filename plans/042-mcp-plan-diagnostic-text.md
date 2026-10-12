# Plan 042: Expose useful statement text and predicates from caller-supplied MCP plans

> Read current source before editing. Inspect drift since `729d88f` in the scope below and adapt implementation details within this authorized intent. A changed file alone is not a STOP condition. Escalate an incompatible contract or missing required proof. This file plans future implementation; writing it runs no SQL.

## Status

- Status: DONE — merged locally to main `3f425ef` (branch 1938f5f) on 2026-10-11, not pushed; Windows and Linux gates green on the merged result; evidence in plans/README.md. Its credential-column literal rule is removed by plan 049 (plan 048 extended it and was reverted).
- Priority: P2
- Effort: M
- Risk: MED
- Depends on: 043
- Planned at: `729d88f`, 2026-10-11

## Why this matters

MCP's offline plan tool receives a document supplied by its caller, but removes the very statement text and predicates needed to explain scans, seeks and filters. The user approved planning a replacement for unconditional stripping on 2026-10-11. Keep bounded structured output and credential protection; do not treat all SQL literals as credentials.

## Current state

- src/SqlHarness.Mcp/McpOperationMapper.cs:36-61: McpResultSanitizer replaces StatementText and Predicate with null on every DistilledPlan.
- src/SqlHarness.Mcp/McpResultAdapter.cs:171 invokes the sanitizer; fixed and request tool handlers also sanitize plan results.
- src/SqlHarness.Core/PlanDistiller.cs defines PlanStatement.StatementText and PlanNode.Predicate; AgentOutputProjection already clips these fields.
- CLI plan can expose these fields. The offline MCP tool has no resolved credential-bearing target, so it cannot claim to scrub every unlabelled secret from arbitrary imported XML.

## Scope

src/SqlHarness.Mcp/McpOperationMapper.cs; McpResultAdapter.cs; Tools/McpToolCatalog.cs; Tools/McpRequestToolHandlers.cs; Tools/McpToolArguments.cs if used for schema; src/SqlHarness.Core/AgentOutputProjection.cs only for necessary projection wiring; tests/SqlHarness.Mcp.Tests/McpMappingTests.cs, McpOutputTests.cs, McpToolSchemaTests.cs, McpTokenBudgetTests.cs, McpSecretRedactionTests.cs; tests/SqlHarness.Tests/PlanDistillerTests.cs; README.md; AGENTS.md; docs/mcp.md.

No dashboard API, user configuration edits, live production queries, commit, merge, installation or push is authorized by this plan. Match existing xUnit and fake-provider conventions; use synthetic fixtures, not operational SQL/data. Persistent-mutation controls, target locking, timeout and byte limits retain their separate purpose.

## Steps and acceptance

1. Add includeText to the existing MCP plan tool in both modes, default true for this caller-supplied document surface. False retains metadata-only statement/operator output. Do not add another tool.
2. Replace unconditional stripping with a policy-aware transformation. With includeText=true retain bounded StatementText and Predicate after credential-pattern filtering from plan 043; do not alter names, metrics or source objects. With false omit text and predicates. Make policy survive the adapter without a second unconditional sanitizer undoing it. Keep errors credential-filtered.
3. Cover SQL Server and PostgreSQL distilled plans, inline and allowed-file inputs. A plan containing a join/filter literal must retain it in diagnostic mode; password/token syntax must be scrubbed. Metadata mode omits it. Validate true defaults and explicit false through actual tool binding.
4. Verify full serialized CallToolResult budgets, escaping, clipping and omission indicators. The original XML is never returned or persisted by this change. Add concise tool descriptions explaining that imported plan text may contain data and the caller chooses that surface.
5. Document the changed default and remove conflicting 'sanitized only/no predicates' statements. Scope-owned artifact sections remain their existing projections; adding raw Query Store text or artifact XML is outside this plan.

After each logical implementation change, run the relevant subset of the focused test command below. Build first when needed. All selected tests must pass; a skipped required provider scenario is not proof.

## Verification

- Build: `dotnet build SqlHarness.sln -warnaserror` — exit 0, no warnings/errors.
- Focused: `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~McpMapping|FullyQualifiedName~McpOutput|FullyQualifiedName~McpToolSchema|FullyQualifiedName~McpTokenBudget|FullyQualifiedName~McpSecretRedaction"` — exit 0, at least one selected test, no failed selected tests; confirm the intended regression tests actually ran.
- Final Windows gate: `pwsh -NoProfile -File scripts/verify.ps1` — exit 0 and literal `verify: OK`.
- Final Linux gate: `pwsh -NoProfile -File scripts/verify-linux.ps1` — exit 0 and successful completion of its full gate on ext4.
- `git diff --check` — exit 0.

Run one final gate on each required platform after the final relevant code change. Repeat or broaden verification only for a remaining failure or risk. Record literal results and skipped live checks separately.

## Done criteria

- [ ] Default diagnostic output retains useful bounded predicates and statement text for both engines.
- [ ] includeText=false omits both fields and binds in both MCP modes.
- [ ] Credential fixtures remain protected; no claim of complete sanitization of arbitrary data.
- [ ] Actual wire budgets and source immutability are verified.
- [ ] Focused and required platform gates pass, and plans/README.md records evidence.
- [ ] Source, CLI/MCP descriptions and documentation agree on the final behavior.

## Stop conditions and maintenance

Stop if a proposal requires raw artifact access or target credentials for this target-free tool. Resolve additive schema/budget conflicts within scope; do not silently add an unbounded text surface.

Future changes must preserve the specific boundary tested here rather than adding generic hiding or syntax restrictions. Update the plan if implementation evidence changes its premise; historical tests are evidence to examine, not authority to preserve obsolete policy.
