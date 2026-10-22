# Plan 097: Allow a final one-case parameter batch without duplicate executions

> **Executor:** use staged implementation, task review and whole-branch review. This is a plan, not authorization to implement, merge, push, run paid agent trials or access a live database.
>
> **Drift check first:** `git diff --stat 32adaa7..HEAD -- src/SqlHarness.Core/ParameterSetResolver.cs src/SqlHarness.Core/SqlParameterMatrix.cs src/SqlHarness.Mcp/Tools src/SqlHarness.Cli tests`. Reconcile changed interfaces against the excerpts below; do not overwrite later features. Work in an isolated `.worktrees/plan-097` checkout.

## Status and authority

- IMPLEMENTED on branch `plan-097`; task reviews and whole-branch review approved subject to controller's final evidence check. Windows gate passed on product revision `6d658da`; documentation/evidence follow-up and scoped re-review are in progress. No integration or push. Planned at `32adaa7`, 2026-10-20.
- Priority P2; effort M; implementation risk MED; category perf.
- Dependencies: Existing 083 parameter semantics must be present. Execute after 096 to avoid concurrent CLI/parser edits; 096 is scheduling coordination, not a semantic predecessor.
- **Spec:** the Fix contract in this file is authoritative for this change. Preserve existing safety, scope ownership, persistent-mutation classification and approval.
- Follow `AGENTS.md`, `plans/README.md`, `docs/agents/verification.md`; MCP changes also read `docs/mcp.md`.
- Optimize correct completed tasks, including discovery, requests, responses, recovery calls and available provider usage. UTF-8 bytes and bytes/4 estimates are not measured model tokens.

## Why this matters

A 33-case investigation cannot use the natural 32+1 split because named-set collections and matrices currently require at least two cases. Users can reshuffle into 31+2, but the interface makes batching needlessly surprising and may encourage duplicate runs. Keep a bounded case count and permit the valid remainder.

## Current state and conventions

- `ParameterSetResolver.cs:18` declares `MaximumSetCount=32`; the named-set branch at lines 174-175 rejects `sets.Count is < 2 or > MaximumSetCount`.
- `SqlParameterMatrix.cs` parses/binds matrix values with the existing at-least-two rule. CLI and MCP may also validate file/set counts; find all enforcing paths before changing schemas.
- `McpToolCatalog.cs:664-667` advertises 2..32 files, sets and matrix values. A compare has two SQL variants regardless of how many parameter cases it has: permitting one case does not make it a one-variant comparison.
- Existing named-case fingerprints, labels, artifact case indices and failure-stop behavior remain authoritative. A set's name is not disposable display metadata.

## Fix contract

1. Accept **1..32** named sets, parameter-set files and matrix values for read-only measure/compare. Empty explicitly supplied collections remain invalid; absent collections retain the ordinary single-query behavior.
2. A named singleton remains named and uses the same set/matrix report and artifact family as a multi-case call. Preserve setName/valueHash or existing typed-value identity, matrix index, context fingerprint and reproduction data; do not silently collapse to the unnamed API.
3. Keep the upper bound 32, execution repetition/time ceilings, setup once per case/session, fixed-parameter collision checks, null/type equivalence, non-scalar limits and first-failure/completed-case accounting unchanged.
4. Do not automatically partition a submitted 33-case call or rerun failed SQL. Reject before connecting with actionable guidance: split into calls of at most 32, with a singleton remainder now supported. A larger count ceiling requires separate measured/operator-policy work.
5. Fix CLI, MCP fixed/request scopes, descriptions, schemas and capabilities together. Mutation continues rejecting unsupported parameter-set forms.
6. Results from separate calls are separate measurement contexts. Do not claim split calls are an atomic or perfectly equivalent experiment; the client retains their operation/case identity.

## Scope

Source: `ParameterSetResolver.cs`, `SqlParameterMatrix.cs`, `ParameterSetFileReader.cs` only if it enforces collection counts, `Capabilities.cs`, CLI `Commands/SqlHarnessCommands.cs`, MCP `McpOperationMapper.cs`, `McpInputReader.cs`, `Tools/McpToolArguments.cs`, `Tools/McpToolCatalog.cs`, `Tools/McpRequestToolHandlers.cs`.
Tests: existing Core `ParameterSetResolverTests.cs`, `SqlParameterMatrixTests.cs`, `MeasureParameterSetValidationTests.cs`, `MeasureParameterSetExecutionTests.cs`, `MeasureParameterSetReportTests.cs`, `MeasureParameterSetArtifactTests.cs`, CLI `MeasureParameterSetCommandTests.cs`; MCP `McpToolSchemaTests.cs`, `AgentOptimizationWorkflowTests.cs`, and new `tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs` for fixed/request singleton calls.
Docs: `docs/mcp/inputs.md`, `docs/mcp/tools.md`, relevant README parameter sections. Do not alter journal schemas, fingerprint algorithms or artifact formats merely to admit one case.

### Task 1: Freeze singleton semantics and boundary failures

- [x] Test 0/1/2/32/33 cases in inline named sets, named files and matrix forms. Singletons include scalar, typed NULL and supported TVP/list cases.
- [x] Use fake execution to prove one case executes its setup and measured repeats exactly once according to existing repeat semantics; a singleton compare still executes both variants.
- [x] Require singleton report/artifact to preserve the explicit name/index, supplied subject/scope, typed values and case identity. A failing singleton leaves no duplicate or phantom not-executed case.
- **Verify:** `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ParameterSet|FullyQualifiedName~SqlParameterMatrixTests"` has intended RED.

### Task 2: Admit one case through all enforcing surfaces

- [x] Lower only the collection minimum from 2 to 1. Match runtime, parser/CLI, JSON schema validation and descriptions; keep maxima and other validation unchanged.
- [x] Update exact contract pins/capabilities to 1..32 without dropping types, labels or scope requirements.
- **Verify:** Task 1 command GREEN; `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpParameterBatchTests"` GREEN; explicitly inspect that the filter selects the new fixed/request singleton cases.

### Task 3: Prove 32+1 completes without redundant cases

- [x] Run a deterministic fake-session or fixture-host workload of 33 uniquely identified cases split 32+1; assert every case is executed exactly once, all returned references are recoverable and the two operation identities remain separate.
- [x] Record calls/input/output bytes and zero live DB connections. No token-efficiency claim from arithmetic alone.
- [x] Remove obsolete 2..32 claims on affected current interfaces, retaining historical plan evidence unchanged.
- **Verify:** the new 32+1 workflow regression and Common verification pass.

## Maintenance notes

The minimum collection count is an interface decision; the maximum case count is an execution bound. Avoid conflating them. Future higher case ceilings need total case-by-repeat work estimates and operator policy, not a silent constant increase.

## Common verification and delivery

Before changing source in the isolated implementation checkout, record `$plan097Base = git rev-parse HEAD` and persist that exact SHA in evidence. On resume, reload the saved SHA rather than redefining it after commits. This implementation base can be newer than the planning drift-check base when predecessors have landed.

Run focused tests during iteration; retain the regression's meaningful RED and GREEN. Run affected suites once when ready, then scoped task review and independent whole-branch review. No live database is needed for acceptance.

Before the final gate, normalize only this implementation branch's changed C# files with `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan097Base -Fix`. Commit code, then run `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef $plan097Base` and retain its direct exit code and log. Run `just verify -LogDirectory D:/temp/sqlharness-plan097-verify`; expected exit 0 on the final product tree. Inspect failed stage logs before fixing or rerunning. Do not run Linux verification unless requested.

Record the tested revision/tree, commands, actual results, output bytes, call counts and evidence limits in `plans/097-verification-evidence.md`. Update this plan, `plans/README.md` and `plans/history.md` on completion. Use `fix(097): ...`, `feat(097): ...` and `docs(097): ...` commits as appropriate; integration/publication needs separate authorization.

## STOP conditions

Report a concrete design blocker if the live interfaces invalidate the Fix contract, a required safety/ownership invariant cannot be preserved, or implementation needs unrelated source changes. Ordinary drift, expected regression failures and already authorized task-local fixes are not reasons to restart the design or broaden scope.

## Done criteria

- [x] All task-specific regressions below pass and each task has spec/quality review.
- [x] Served schemas, descriptions and capabilities agree with runtime behavior; affected human docs are updated without duplicating interface contracts into skills.
- [x] Direct branch formatting and final Windows `just verify` exit 0; final whole-branch review is approved.
- [x] Evidence distinguishes deterministic fixture measurements from actual-agent or live-database results.
- [x] Changes stay within the listed source/test surfaces plus this plan, its evidence, plan index/history and explicitly listed docs.
