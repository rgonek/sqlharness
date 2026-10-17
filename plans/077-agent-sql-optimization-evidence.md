# Plan 077: Actionable agent responses for SQL optimization Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans or, when the operator selects delegation, superpowers:subagent-driven-development. Steps use checkbox tracking.
>
> **Drift check:** Compare `cd7bab8280259dc1f41679db973019120d3f0681..HEAD` for the files in the task map before execution. Adapt compatible drift; do not reopen completed plans 065, 066, 067, 068 or 076.

**Goal:** Give an agent enough trustworthy, directly addressable evidence to evaluate a SQL optimization with fewer unnecessary reads, while shrinking redundant MCP JSON.

**Architecture:** Keep execution, safety, result comparison and measurement semantics in Core. Extend the existing projection with an explicit audience rather than copying its budget/clipping algorithm into a second projector. MCP gets its own lean versioned envelope; CLI presentation changes only where the same evidence actually benefits its users. Additional diagnostic evidence is captured from already available runs/plans and served offline through the existing artifact tool.

**Tech Stack:** .NET 10, C#, System.Text.Json, ModelContextProtocol SDK, xUnit, existing artifact storage and offline fixtures.

**Evaluation approach:** Actual MCP requests against a local, test-only server using deterministic synthetic evidence, plus paired task-based agent trials. Historical journal counters select realistic workload patterns; they are not replay payloads. Pure DTO/projector tests are necessary but do not replace end-to-end requests.

**Spec:** The Design decisions and Acceptance sections below are the specification. Origin: the operator's accepted response-review directions P1–P6 and the second independent consilium focused on SQL optimization; see the [decision record](../docs/observations/sql-optimization-response-consilium.md).

## Status and dependencies

- IN PROGRESS — Tasks 0–7 implementation recorded; actual-agent acceptance and efficiency remain unverified because all nine candidate attempts stopped at provider HTTP 402. No installation, publication or push.
- Planning base: `cd7bab8280259dc1f41679db973019120d3f0681`, 2026-10-16.
- Priority P1–P2; effort L; risk MEDIUM (model-facing wire shape and additive saved evidence).
- Prerequisites already implemented: 055–061 (agent/output/artifact foundations), 065 (saved-plan paging/clipping), 066 (regression policy v2), 067 (cell limits), 076 (partial matrix journal runs). Coordinate with 063 on plan DTOs; it remains a separate TODO. Coordinate with 073 if its new mutation tool lands first; this plan does not add mutation capability.

## Global constraints

- MCP–CLI compatibility only where useful: share semantic evidence and safety, not an identical presentation contract. Preserve CLI text and full JSON unless a task explicitly changes useful evidence; do not remove CLI exit codes or paths to match MCP.
- Exactly the existing eight MCP tools at the planning base. No new optimization executor, database-analysis agent, report lifecycle or automatic SQL rewrite.
- Preserve credential protection, persistent-effect classification, target ownership and no-mutation behavior. Caller SQL, parameters, results, plan predicates and target names remain readable within output bounds.
- No added database commands, repeated executions, EXCEPT queries, automatic plan fetching, catalog inventory, statistics collection or session-option changes merely to populate a response.
- No weakening of ordered/multiset/set/nullability requirements. A diagnostic is not permission to change the comparison mode.
- No new improvement/confidence score or regression verdict. Preserve policy v2 and `pass`/`fail`/`inconclusive`; pass is absence of an elapsed regression under that policy, not proof of improvement.
- Default call text budget remains 65,536 UTF-8 bytes, accepted range 4,096..1,048,576 under the process ceiling. Cell bounds remain 0..4096, default 2048. Tool catalog stays within its existing 32 KiB budget.
- Stored old artifacts remain readable under existing ownership rules; missing new evidence is unknown/unavailable, never inferred success. No migration or rewriting of historical runs.
- All evaluation inputs, scenario definitions and launch instructions travel with the repository. No source-machine database, transcripts, temporary paths, user configuration or credentials are prerequisites. Keep fixture hosting test-only; no fixture flags, safety bypasses or mock routes in the production executable.

## Design decisions

### D1. Lean MCP envelope, one projection algorithm

MCP envelope v3: required `schemaVersion`, `status`, `result`; `error` and `truncation` appear only when populated. Remove MCP `command`, `exitCode`, `operationRef`, `parentOperationRef` from model text. Preserve internal operation references and emission receipts for journal attribution; MCP `isError` and controlled watch/snapshot outcomes retain their semantics. Keep `schemaVersion`: its small cost is justified by a deliberate breaking contract update. Give `McpResultAdapter` a dedicated envelope DTO and version constant instead of changing the CLI envelope just for parity.

Add `AgentProjectionProfile` (`Cli`, `Mcp`) to the existing Core projector as a final optional argument to `Project(..., int maximumBytes = 65_536, AgentProjectionProfile profile = AgentProjectionProfile.Cli)`. Pass it through recursive calls; MCP selects Mcp explicitly. Reuse clipping, byte fitting, query rows and cursors. Keep audience-specific output records separate from stored report DTOs. CLI may consume additive semantic diagnostics without being forced into the MCP envelope.

Emit model-facing enums as documented strings: comparison mode `ordered`, `multiset`, `set`, `off`; watch `condition-met`, `unchanged`, `max-duration`; snapshot `stored`, `identical`, `different`. Do not globally change stored enum serialization. Preserve classification/risk-tier distinctions and repair offline/internal error classification so it does not falsely claim SQL execution.

### D2. Direct artifact identity and honest metrics

MCP benchmark results expose `artifactId`, not `artifactDirectory`; share validation with the reader and never clip an opaque id into a different valid id. Missing/unusable id is explicit unavailable evidence, not an empty string. Keep a concise resolved target with `server`, `database`, `engine`; only include requested identity/transport detail when it adds diagnostic meaning. Do not replace target names with hashes.

Model-facing metric distributions use the same `min`, `median`, `max` shape for CPU milliseconds, elapsed milliseconds and logical reads. All values are nullable; missing metric evidence makes the distribution null rather than numeric zero. Use decimal elapsed evidence when available; do not keep competing rounded and exact medians in the MCP result. Retain availability, result-row source and nonadditive buffer semantics; move repeated definitions to the authoritative interface description once. Keep warnings and min/max. Retain computed deltas as a useful safeguard against model arithmetic, but do not duplicate variant medians in regression diagnostics. Preserve legacy rounding and stored data for CLI/storage consumers where required.

Measure-set results gain actual repetition counts and elapsed min/max beside their existing medians; no raw per-run arrays in default output. Cross-set extrema are meaningful only with their availability and complete source inventory; do not delete them just because a projected subset can be enumerated. Hashes are audit data unless an actual selector/correlation use is documented.

### D3. Explain failed result comparison without inventing domain equivalence

Extend observed result-equivalence evidence with nullable `baselineStable`, `candidateStable`, `schemaEquivalent` and a bounded `mismatchReasons` list. Stability is within the selected comparison mode across all measured runs of that side, including result-set schema; schema equivalence uses the existing schema comparison and preserves the separate nullability policy. Fewer than two observed runs yields unknown stability, not a positive stability claim. `off`/unavailable leaves diagnostics unknown.

Reason codes: `baseline-unstable`, `candidate-unstable`, `schema`, `rows`, `nullability`. Emit every established reason in that order, without hiding simultaneous failures behind a single prioritized cause. Preserve the legacy `mismatchReason` meaning for old readers as needed; never fill new reasons by guessing from old aggregate counters. Compute from already captured comparison evidence. Do not add `order-only`: proving it would require an explicit, bounded comparison, not an inference from a few counters. Do not automatically suggest that relaxing ordered comparison is correct.

### D4. Decision-first matrix and explicit execution coverage

Each case retains its identity, medians/deltas, equivalence availability and warnings, and gains `regression: { verdict, reasons }`, the proven mismatch diagnostics from D3 and elapsed min/max for each side. No default `details` duplication. Full variants/operators remain available through the case artifact's existing summary/metrics sections. The same case schema is returned by `matrix-cells`; if a budget cannot fit all cases, inventory pages must preserve each case's decision fields rather than silently dropping verdicts.

Separate execution coverage from output paging: `coverage: { requestedCases, completedCases, notExecutedCases, failedCase? }`. On partial failure, failedCase carries the zero-based `index`, applicable `setName` or typed `parameterValue`, and known failure `phase`; phase is unknown when not established. completedCases excludes the failing cell, notExecutedCases excludes both completed and attempted failed cells. With an identified attempted failure, `requestedCases = completedCases + 1 + notExecutedCases`; otherwise identify unknown coverage rather than inventing a failed case. Existing omittedCaseCount/continuation describe only completed cases absent from this response. A first-cell failure keeps the existing error status and null result, with this coverage under optional `error.matrixStop`; do not fabricate a completed-case report just to expose counts. Do not automatically rerun cases or claim that the original entire parameter space was tested.

Store additive coverage/decision evidence in versioned artifact metadata/index where required for offline reads. Old indices without it remain readable, with missing evidence omitted/unknown. Completed-run journaling from 076 remains unchanged.

### D5. Address diagnostic evidence, not a universal readiness verdict

Keep existing regression gates and scoped evidenceGaps; do not introduce a global `evidence.sufficient` boolean. A saved plan is useful to diagnose a cause, but is not always required to measure a regression or compare rows. Missing evidence must name what claim it limits.

For the bounded noteworthy-operator list, add `planRefs` only when an exact source is known: `{ artifactId, variant, parameterSet?, statement, repetition, nodePath, cursor }`. The cursor is the actual saved-plan preorder position accepted by `artifact(section=plan)`; no invented new selector that the tool cannot consume. Carry source statement/repetition identity from extraction and store optional SQL Server `nodeId` in distilled nodes. Node ids are local to one statement and plan; paths are local to one selected plan. For `side=both`, keep separate references/attention flags for each side rather than presenting a baseline id or unioned warning as shared proof. Bound refs to one representative per side and disclose that the noteworthy source is capped.

Never reconstruct a plan link from a bare nodeId, timestamp, object name, hash or earliest/last plan guess. If exact provenance was not retained (including legacy artifacts), omit planRefs and report the limitation. Existing plan pages still expose available repetitions, shape stability and gaps. Fix identity/continuation before considering any additional root-summary endpoint. Do not treat XML hash changes as proof of a changed plan shape.

### D6. Honest clipping and task-specific guidance

Reuse the compact query row/schema/clippedCells logic in watch. Retain source and projection omission counts separately enough to distinguish missing history, rows, columns and text; projection must not make a truncated node look like a complete leaf. Offline plan output records omitted-child/subtree evidence at the affected node; saved plans use existing advancing cursors. No claim that unsaved query/watch rows can be recovered with artifact.

Retain useful `truncation.maxCellChars` and semantic omission counts; remove internal detailLimit from MCP. Keep plan 065's once-only retained clipping and byte-aware paging tests. An unfit page reports the existing no-node-fit condition without pretending that increasing cursor recovered it.

Tool descriptions/schemas own output meaning and callable selectors; capabilities own runtime limits/policy; the installed skill owns optimization completion criteria. Explain `pass != improvement`, observed equivalence != domain equivalence, and actual/estimated row semantics with executions where exposed. Do not add free-form server-generated optimization advice or blanket nextActions. Any exact continuation hint must be derived from an available saved artifact and accepted tool arguments.

### D7. Two complementary evaluations through real MCP requests

**Deterministic protocol workflows:** Build a minimal test-only stdio fixture host from the production `McpHost.CreateServerOptions`, `McpToolCatalog.Wire`/module-factory seam and SDK transport, reusing the patterns in `McpProtocolTests`, `McpStdioSeamTests` and `McpStdioProcessTests`. Test fixtures replace the database-facing evidence provider, not the MCP handlers, adapter, serializer, budget logic or artifact reader. A scenario-aware module may return synthetic query/benchmark outcomes; artifact/plan operations must use the production offline reader/distiller and real temporary fixture files written by the normal artifact writer. Keep Core semantic tests for calculation/extraction correctness separate: synthetic module outcomes do not prove SQL execution or result-comparison calculations.

The SDK client performs initialize, tools/list, capabilities, tools/call and actual follow-up calls. No replay of handwritten final JSON and no direct projector invocation counted as a tool request. Persist request arguments, `isError`, final response text, available omissions/continuations, terminal condition and actual selected artifacts. Record model-visible UTF-8 text cost separately from protocol-frame bytes and tools/list discovery. A single rendered response remains one text block. Validate fixed and request-scoped mapping with synthetic profiles; database connection attempts must fail the fixture run immediately. The scenario provider rejects unknown SQL/scenario inputs instead of fabricating successful outcomes. These fixture tests do not prove the synthetic module's safety classification or the published CLI startup path; retain the existing Core safety and published-process smoke coverage for those boundaries.

**Task-based agent trials:** Give a fresh agent only the task, synthetic SQL inputs, the version's served MCP interface and installed operator skill. The agent chooses calls and writes its conclusion; it cannot inspect expected outcomes, fixture internals, baseline measurements, other runs or this implementation plan. The coordinator/runner holds a separate scoring oracle with required claims/evidence and allowed selectors. Use the test-only host as a session-scoped MCP connection; do not edit the user's persistent MCP configuration or replace a normal server globally.

Run the same tasks on pre-change and post-change versions with the same model/version, reasoning setting, prompt, fixture seed and time/call bounds; record any provider drift. Use three paired fresh runs for each of these three task families: no improvement/invalid comparison, partial 32-case matrix, and multi-statement operator diagnosis with colliding ids. At most 20 tool calls and five minutes per run; reaching either cap is incomplete, not success. Keep the initial pass to these 18 trials, report every result and do not rerun until a preferred answer appears. If uncertainty remains, report it and agree on an extension rather than escalating model spend silently. Record input/output usage separately when actually reported, without treating byte estimates as provider token counts.

The primary measure is correct conclusion with the required evidence and no unsafe/unsupported claim. Required evidence is claim-specific: a latency comparison need not fetch every plan, but a claim naming a causal operator must identify its actual saved source. Evaluate call/byte improvements only among trials that meet that evidence standard. Deterministic workflows provide controlled byte comparisons; agent trials provide a distribution of choices rather than a single guaranteed savings number. A fully scripted request sequence is protocol evidence, not evidence that an agent selected better calls.

**Version control for the experiment:** Freeze the fixture host, inputs, scoring rules and scripted workloads before product changes. Use separate baseline/candidate checkouts, builds, fixture roots and journals; pin their commit/tree identities, including a hash of any uncommitted test-harness diff. A workflow-only checkpoint commit is not required. Run the same scenario inputs under both versions. Predeclared version-specific argument translation may accommodate intentionally changed schemas, but must not choose the candidate's next read or rewrite its verdict. A protocol or interface difference outside the planned contract must be investigated rather than normalized away.

**Optional database lane:** A separately authorized disposable local SQL Server scenario may verify the real collection-to-MCP integration. It is not required for response-contract completion and cannot substitute for controlled before/after evidence. No UAT/production access is implied, and variation in live plans/data/load must never be counted as a response-format improvement.

## Review focus

1. A faster candidate with unstable baseline, schema differences or unrequested equivalence must not be accepted as an optimization (Tasks 3, 4, 7).
2. A 32-case matrix at 4096 bytes cannot show every case in one page; all decisions must remain reachable without treating paging as unexecuted work (Tasks 1, 4, 7).
3. PostgreSQL unavailable CPU and submillisecond elapsed evidence must not become zero or lose precision; true measured zero must remain zero (Task 2).
4. Repeated nodeId across statements/variants and a warning on only one side must never produce a false exact link (Task 5).
5. Old artifacts, scope mismatches and a plan page fitting zero nodes must remain explicit and safe, with no synthetic evidence/backfill (Tasks 1, 4–7).

## Task map and implementation steps

### Task 0: Freeze a portable real-MCP evaluation harness before product changes

**Files:** consume the repository-local [portable baseline](evidence/077-workflow-baseline.json); extend `tests/SqlHarness.Tests/AgentWorkflowTests.cs`; create `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs`, `tests/SqlHarness.Mcp.WorkflowHost/SqlHarness.Mcp.WorkflowHost.csproj`, `tests/SqlHarness.Mcp.WorkflowHost/Program.cs`, `tests/SqlHarness.Mcp.WorkflowHost/WorkflowScenarios.cs`, `tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/manifest.json` and `docs/verification/plan-077-evaluation.md`. Reuse existing protocol/stdio test helpers and artifact fixtures. Keep prompts/hidden scoring expectations as separate fixture files. No source-machine database/transcripts are required; real session ids, SQL, targets and result/plan contents are excluded from the portable export.

**Interfaces:** immutable historical call graphs/costs plus runnable synthetic MCP fixtures consumed by Task 7. The export contains four anonymous sessions, eleven compare-plus-artifact workflows and forty calls, but is `replayable: false`: its missing payloads/revision/budgets must not be invented. The new test-only host accepts `--scenario <id> --home <isolated-directory>` and communicates only MCP frames on stdout. Its database-facing provider is synthetic, while production tool mapping/output and artifact reads remain real. The manifest pins scenario ids/seeds, synthetic input files, expected evidence/claims, scripted requests and budgets. It has no machine-specific paths; expected claims are withheld from trial agents.

- [x] Export the verified journal workflows into the portable anonymous baseline. Validate per-call sums, workflow totals and producer/read edges; preserve original observations without source-machine identifiers. No live SQL execution was repeated.
- [x] Read the portable file and retain its distinction between whole-session and compare/artifact cost. Discovery/input/final chat are outside journal output cost; do not manufacture those costs from raw_bytes or byte/4 estimates. Do not reconnect to the source machine to complete this task.
- [x] Build minimal synthetic report/artifact scenarios for the relevant recorded patterns using existing fixture builders, including 32-case paging, unavailable metrics and colliding operator ids. Keep prompts, inputs and hidden scoring expectations separate. Do not reconstruct real SQL/results from byte counts or require historical redundant calls as optimal behavior.
- [x] Implement the D7 test-only host and SDK-client driver with bounded startup/cancellation and temporary homes/artifact roots. Prove actual protocol handshake/tools/list/tools/call, real offline artifact reading, pure protocol stdout and zero database connections. Reuse the existing catalog/module seam rather than adding a fixture mode to product startup.
- [x] Before Task 1, execute the scripted MCP workflows at 4096 and 65536 bytes, freeze their request/response records and pre-change source/build identities, and run the nine pre-change agent trials from D7. Preserve original evidence without candidate-derived hints. A repeatable SDK test invocation is `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter FullyQualifiedName~AgentOptimizationWorkflowTests`; the execution record must also give the tested host launch and session-scoped agent connection commands for the available runtime.
- [x] Store a portable synthetic baseline manifest under the fixture directory and the results/methodology in `docs/verification/plan-077-evaluation.md`, with detailed run logs as generated artifacts. Verify a clean checkout can build/start the host and execute the fixture suite without the original journal, agent sessions, credentials or network database access. Keep external model-service availability separate from local protocol-fixture reproducibility.

### Task 1: MCP v3 envelope and audience-aware rendering

**Files:** create `src/SqlHarness.Mcp/McpAgentEnvelope.cs`; modify `McpResultAdapter.cs`, `McpOperationMapper.cs`, `src/SqlHarness.Core/AgentOutputProjection.cs`; tests `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`, `McpTokenBudgetTests.cs`, `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`.

**Interfaces:** produce `AgentProjectionProfile` and the optional Project parameter from D1; `McpAgentEnvelope` exposes exactly D1. Existing CLI call sites use the default profile. Keep artifact producer references outside emitted model text but inside outcome/receipt accounting.

- [ ] Add failing tests `Mcp_v3_preserves_partial_and_controlled_outcomes_without_cli_fields`, `Mcp_v3_uses_named_enums_and_omits_empty_optional_fields`, `Cli_envelope_is_not_changed_for_mcp_parity`, `Offline_internal_failure_does_not_claim_sql_was_executed`.
- [ ] Implement D1, explicitly covering success, dry-run, validation/storage/auth/internal errors, partial compare, watch deadline and snapshot differences. Validate the actual served text, not only DTOs.
- [ ] Keep one valid JSON text block and existing per-call budget enforcement; measure journal emitted bytes from the new final text.
- [ ] Run the focused MCP output/budget and CLI agent contract tests. No new tests asserting identical CLI/MCP envelope keys.

### Task 2: Artifact ids, resolved target and truthful metric projection

**Files:** modify `src/SqlHarness.Core/BenchmarkSummary.cs`, `AgentOutputProjection.cs`, `ArtifactReader.cs`, `Artifacts.cs`; add focused output DTOs under `src/SqlHarness.Core/AgentBenchmarkOutput.cs`; tests `BenchmarkSummaryTests.cs`, `ArtifactReaderTests.cs`, `Cli/AgentOutputTests.cs`, `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`.

**Interfaces:** shared artifact-id validation used by writer/reader/projection; audience-specific metric DTOs implement D2 without changing raw stored measurements. Measure-set projection includes repetition counts and elapsed range from its existing Metrics distributions.

- [ ] Add failing tests for full id round-trip including dots, deep/long parent paths, no empty/clipped ids, true zero vs unavailable CPU/reads, decimal elapsed below 1 ms, and multiple same-named databases on different servers.
- [ ] Implement D2, retaining availability/row-source/buffer semantics and separating single-response clipping from cross-set source extrema.
- [ ] Verify artifact reads use the emitted id with existing owner checks and that no additional database execution/catalog query is introduced.

### Task 3: Disambiguate observed equivalence failure

**Files:** modify `src/SqlHarness.Core/ResultEquivalence.cs`, `BenchmarkSummary.cs`; tests `tests/SqlHarness.Tests/ResultEquivalenceTests.cs`, `BenchmarkSummaryTests.cs`, `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`.

**Interfaces:** additive nullable diagnostics on `ResultEquivalenceReport` and mismatchReasons from D3, consumed by Task 4. Preserve existing Equivalent and comparison modes and regression policy v2 decisions.

- [ ] Add failing synthetic cases: stable equal results; unstable baseline; unstable candidate; both unstable; schema mismatch; stable row mismatch; strict nullability-only mismatch; off; one run per side; legacy artifact without diagnostics.
- [ ] Derive reasons and side stability from the existing bounded canonical evidence, without another SQL execution or unbounded second fingerprint representation. Verify simultaneous reasons and unchanged verdicts/equivalence for existing fixtures.
- [ ] Prove no `order-only` inference or automatic change of comparison mode; add a workflow case where ordered semantics are required.

### Task 4: Matrix decisions and partial execution coverage

**Files:** modify `src/SqlHarness.Core/Contracts.cs`, `CompareMatrixRunner.cs`, `SqlHarnessModule.cs`, `BenchmarkSummary.cs`, `AgentOutputProjection.cs`, `ArtifactReader.cs` and matrix-index write path; tests `CompareMatrixTests.cs`, `ArtifactReaderTests.cs`, `BenchmarkSummaryTests.cs`, `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`, `McpTokenBudgetTests.cs`.

**Interfaces:** produce `MatrixExecutionCoverage`/`MatrixFailedCase` DTOs implementing D4, carried from the runner to the outcome even without a completed-case report; MCP projects them as result.coverage or error.matrixStop, never both. Decision-first case shape is shared by emitted inventory and saved matrix-cells pages. Additive saved-index fields have explicit legacy absence handling; no inferred historical coverage.

- [ ] Add failing tests for 32 cases with mixed pass/fail/inconclusive, all pages at 4096 bytes, first/middle/last failure, typed NULL versus empty string, named sets, SQL/artifact phase failure, and old saved indices.
- [ ] Carry requested/completed/failed counts from actual runner outcomes; omit full default details; retain per-case verdict/reasons/range and exact artifact ids under fitting.
- [ ] Traverse all continuation pages and assert unique complete case identity/decisions, advancing cursor and unchanged completed-run journaling from 076. Fetch full summary only for explicitly selected cases.

### Task 5: Exact operator-to-plan provenance

**Files:** modify `src/SqlHarness.Core/Diagnostics.cs`, `BenchmarkRunner.cs`, `CompareCellRunner.cs`, `Artifacts.cs`, `PlanDistiller.cs`, `StatementMetrics.cs`, `BenchmarkSummary.cs`, `ArtifactReader.cs`; tests `PlanDistillerTests.cs`, `BenchmarkSummaryTests.cs`, `ArtifactReaderTests.cs`, `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`; create `tests/SqlHarness.Tests/StatementMetricsTests.cs` for extraction/provenance fixtures.

**Interfaces:** optional source identity fields survive extraction and artifact write; `planRefs` uses only existing callable plan selectors plus returned nodePath from D5. Persist new nodeId without requiring it for old files or PostgreSQL plans.

- [ ] Add failing fixtures with two statements reusing nodeId, different variant ids, multiple repetitions, shared operator name/object with a warning on only one side, capped noteworthy lists and a legacy plan without ids.
- [ ] Preserve precise origin before aggregation; select proven representatives instead of assigning a unioned flag to an arbitrary node. Produce a valid reference only when the saved source is unambiguous.
- [ ] Execute emitted reference arguments through the real offline reader and verify the selected page contains the identified node. Test small budgets and missing evidence; never fall back to another run.
- [ ] Coordinate DTO changes with 063 but do not add overlap/index inventory. Recheck stored plan size and pagination costs after adding identity fields.

### Task 6: Completeness across query/watch/plan and dry-run

**Files:** modify `src/SqlHarness.Core/AgentOutputProjection.cs`, `PlanDistiller.cs`, `Contracts.cs`, `SqlHarnessModule.cs`; tests `Cli/AgentOutputTests.cs`, `ArtifactReaderTests.cs`, `WatchTests.cs`, relevant dry-run tests and `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`.

**Interfaces:** reuse query row/schema clipping for watch and explicit node-local omission metadata for offline plan. Dry-run response clearly identifies validation, retaining actual setup/query or setup/baseline/candidate classifications and aggregate risk tier without claiming execution.

- [ ] Add failing tests for wide watch rows, omitted histories, clipped Unicode/binary cells, deep offline plan subtrees, no-node-fit saved pages, and setup/candidate classifications differing from baseline in dry-run.
- [ ] Preserve row/schema alignment, source totals and local clipping identity; distinguish page omissions from source absence. Keep the completed 065 accounting behavior.
- [ ] Ensure dry-run remains connection-free, a query duration is not presented as benchmark evidence, and repeated fields/hashes are omitted only when their meaning remains available.

### Task 7: Interface guidance, end-to-end evidence and final verification

**Files:** modify `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `McpOperationMapper.cs`, `src/SqlHarness.Core/Capabilities.cs` only for affected runtime policy, `docs/mcp/runtime.md`, `docs/mcp/artifacts.md`, `docs/mcp/tools.md`, `README.md` and `skills/sqlharness/SKILL.md`; extend Task 0's workflow tests/fixtures and `docs/verification/plan-077-evaluation.md`, `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs` and `McpTokenBudgetTests.cs`.

**Interfaces:** task-level output shapes/meaning live in the served interface, runtime limits in capabilities, optimization completion criteria in the installed skill. Use bsi-sqlharness-sync-skills only when an authorized implementation changes the contract; this planning task does not update consumer skills.

- [x] Rebuild/start the candidate test-only MCP host and execute the same frozen SDK-client workloads and inputs as Task 0 at both budgets. Compare actual returned text, decisions, continuations, recovered evidence and model-visible UTF-8 bytes; record tools/list discovery and protocol frame bytes separately. Direct calls to Adapt/Project or replayed final JSON cannot satisfy this gate. Keep historical observations separate; bytes/4 is only a token estimate.
- [x] Update the served interface with pass-not-improvement, dry-run, query timing/rows, watch no-continuation and offline-plan omission semantics. Task 6 already updated the installed skill and authoritative MCP docs; Task 7 reused those changes and added a README pointer without duplicating contract prose.
- [x] Attempt D7's nine post-change agent trials through the real fixture MCP server using the predeclared model/settings/prompts/bounds. All nine failed before a model turn or MCP call with HTTP 402; record usage as unavailable and leave behavioral acceptance/efficiency unverified. No retries.
- [x] In the evaluation record distinguish unit semantics, deterministic MCP protocol workflows, model-driven task trials and separately authorized live-database integration; record skipped/unavailable classes explicitly. Fixture/server results are not described as live-database or actual-agent proof.
- [x] Run `just verify` on final product commit `2ce4870` (Task 7 semantic capture at `617153c`, then formatting-only cleanup). UI-check passed (50 files / 260 tests), Core 4060 passed / 11 skipped, MCP 425 passed / 4 skipped; restore/build passed; format skipped on clean committed tree. Branch-scoped C# formatting passed across 50 files. See `docs/verification/plan-077-evaluation.md` for the full log path and scope. Actual-agent acceptance remains unverified; no push/install/publish.

## Acceptance

| Scenario | Required outcome |
|---|---|
| Same timing, equivalent rows, pass | Agent reports no demonstrated improvement, despite successful execution/pass. |
| Lower reads but materially worse elapsed | Existing latency verdict remains authoritative; no claim of success from reads alone. |
| Fast candidate, unstable baseline or different schema | Agent can distinguish the established reason and does not accept the optimization or silently relax ordering. |
| Unavailable CPU/reads and submillisecond elapsed | Missing is null, true zero remains zero, elapsed precision survives; PostgreSQL buffer meaning remains correct. |
| 32 cases, 4096-byte responses, failure in the middle | Each completed case's decision is recoverable; requested/completed/failed/not-executed are distinct from paged-out inventory. |
| Warning in one of two statements with colliding ids | A proven reference selects the actual saved variant/statement/run/node; legacy ambiguity is explicit. |
| Wide/deep/clipped query/watch/plan | No apparently complete truncated cell/schema/leaf; existing saved paging progresses without gaps or double-counted clipping. |
| Old/scope-mismatched artifact and no-node-fit page | Existing safety/legacy behavior remains; no guessed provenance, backfill, fake cursor progress or database access. |
| Real MCP request path | SDK client initializes the fixture server, lists its served tools and executes actual tool calls; production mapping/adapter/serialization/budgets and offline artifact reads are exercised. Zero database connections. Handwritten JSON or projector-only tests do not satisfy this row. |
| Portable execution | Clean checkout on another machine can build/run the fixture host and SDK suite from repository instructions, without the originating journal, real target configuration or temporary files. |
| Controlled workflow cost | Each response/catalog meets budgets. Frozen single-pair and 32-case MCP workflows to the same evidence completion should not grow producer-plus-read bytes or required calls versus identical pre-change inputs; exact diagnostic-link scenarios avoid irrelevant statement/plan reads. Task 7 measured an unavoidable 6,775-byte increase for the 32-case workflow at 4096 bytes (calls unchanged) because each recovered case now carries its policy verdict and coverage accounting; do not remove those decisions to meet the earlier byte target. The same workflow at 65536 bytes is 29,454 bytes smaller; single-pair is 52 bytes smaller and diagnostic-link is 36 bytes larger than baseline. These are explicit tradeoffs under the plan's allowance for unavoidable truthful evidence costs, not universal byte savings. Historical observations are not a denominator for savings on synthetic payloads. No agent-efficiency claim follows from the scripted measurements. |
| Actual agent behavior | Three paired fresh trials for each of three task families, same model/settings/bounds, all outcomes reported. No false optimization acceptance or relaxed comparison requirements in candidate trials. Candidate evidence-complete success count is no lower than baseline; cost claims use only comparable evidence-complete trials and show median/range. A correct refusal for unavailable evidence is distinct from an incomplete timeout. No measurable behavioral gain means no claim of agent-efficiency gain; report deterministic contract gains separately. |

## Deferred / excluded

- Missing-index overlap/inventory (063); mutation broker (073); automatic query rewrites or executions; live UAT; Query Store correlation; new statistics/parameter-sniffing/server-resource capture.
- Universal evidence-sufficient flags, confidence scores, claimed domain equivalence, estimated speedups, per-run arrays by default and raw XML dumps.
- Hash-only plan stability shortcuts: reuse shape evidence and expose unknown when unavailable. Journal phase timings remain where 068 put them unless a separately justified on-demand contract is approved.
- A separate new artifact/context discovery section is not required by this plan; add one only if the recorded workflow demonstrates an otherwise unavoidable discovery gap and amend this specification first.
