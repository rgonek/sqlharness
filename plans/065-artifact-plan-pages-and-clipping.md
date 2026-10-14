# Plan 065: Budget-aware artifact plan pages and accurate clipping counts

> **For agentic workers:** Use superpowers:executing-plans or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox tracking.
>
> **HEAD drift check:** Compare `ed44649..HEAD` for the files below before execution. Adapt compatible drift; changed files alone are not a STOP condition.

**Goal:** Count each clipped output field once and return all saved plan nodes that fit the response budget, with complete advancing pagination.

**Architecture:** Reuse `ArtifactReader` and `AgentOutputProjection`; prepare common clipped fields once and separate fitting from omission accounting. Increase the reader's bounded source window while retaining the projection's byte-aware continuation. Keep one artifact tool and its existing selectors.

**Tech Stack:** .NET 10, C#, xUnit, existing CLI/MCP adapters.

**Spec:** [Agent contract review](../docs/superpowers/specs/2026-10-12-agent-contract-review-design.md), decisions 3 and 9 and the accepted 2026-10-14 follow-up. [Report observation](../docs/superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-14--system1-negative-optimization-result-and-plan-reading-cost).

## Status

- IN PROGRESS — implementation and verification are recorded locally; uncommitted, integration pending. No installation, publication or push authorization is implied.
- Planning base: `ed44649`, 2026-10-14.
- Priority: P1 for clipping correctness; P2 for page efficiency. Effort: M; risk: MEDIUM (shared projection and pagination).
- Owner: this follow-up; 055 and 058 are DONE historical prerequisites. Do not reopen their implementation status.
- Coordinate with pending plans touching projection or artifact interfaces; no dependence on unrelated plan numbering.

## Evidence

System1's reviewed investigation made **14 artifact calls**. This is an observed workflow count, not a modeled API workflow or a workload performance measurement:

| Purpose | Calls | Interpretation |
|---|---:|---|
| Required first pages: two variants across three cases | 6 | Necessary evidence retrieval under the existing contract |
| Required continuation pages for those same selections | 6 | Forced by the reader's fixed 32-node source page |
| Repeated first pages already available to the agent | 2 | Agent organization error; do not credit the tool with eliminating them |
| Total observed artifact calls | 14 | 12 necessary under current pagination, plus 2 avoidable agent repeats |

Two repeated first pages used a different text option; treat their extra output as avoidable retrieval for this review, not a new plan selection. All required node pages were eventually read. Most reads omitted predicates, so complete node coverage does not establish complete predicate review.

Preserve these counts in the implementation's final decision record. Compare a complete equivalent evidence workflow, rather than one response, and distinguish the six continuation calls from the two agent repeats. A hypothetical one-page result for each of the six selections is **6 calls**, conditional on all needed evidence fitting; it was not executed. Fewer calls alone do not prove byte or token savings.

The installed responses reported 32 omissions on a page with a clipped statement text. Source inspection found `StatementText` clipped once per candidate node in `AgentOutputProjection.cs`. An offline direct projection check on an existing local assembly used one synthetic 600-character statement and 1/2/32 short nodes: returned text length was 512 and omission counts were 1/2/32. Assembly provenance was not verified against HEAD; source on the planning base contains the same path. Confirm with new tests on the executor's HEAD.

## Current state

- `ArtifactReader.cs:473` fixes plan source pages at 32 nodes. Its saved-plan input remains file-size bounded.
- `AgentOutputProjection.cs:267-323` clips a common statement during each tentative node fit, then clips it again for the final return. Tentative work changes counters; final clipping occurs after accounting.
- Existing `ArtifactReaderTests` and `McpTokenBudgetTests` prove continuation under small budgets, but do not pin the single-field clipping invariant or one-page completion beyond 32 short nodes.

## Global constraints

- Keep argument names, selector identity, scope-owner checks, last-measured selection and missing-evidence behavior.
- Keep the 512-character cell limit and existing byte budgets. No raw XML access, new tool, user approval flow or database call is required.
- Count clipped retained fields once. Count omitted nodes separately; repeated fitting attempts must not change either count.
- Budget every rendered response; continuation advances by nodes actually returned. Retain the explicit no-node-fit outcome.
- Keep the reader bounded: a source window of at most 128 nodes, within existing saved-file limits. No unbounded materialization added.
- Preserve all observation history. Do not add private identifiers, paths, infrastructure, business data or workload metrics to public evidence.

## Review focus

- Short statement with one long predicate; count that retained field once (T1).
- Long statement with 1/2/32 nodes; node count cannot multiply statement clipping (T1).
- A clipped field on a rejected tentative node; fitting retries cannot count it as a retained-field omission (T1).
- Large or deep plans at 4096 bytes; no gaps, duplicate paths or stalled cursor (T2).
- Nonzero cursor, final page and no-node-fit; identity and termination remain explicit (T2).

### T1: Correct clipping accounting

**Files:** `src/SqlHarness.Core/AgentOutputProjection.cs`; `tests/SqlHarness.Tests/ArtifactReaderTests.cs`; `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`.

**Interfaces:** Preserve `AgentOutputProjection.Project` and `ArtifactPlanSection`. Prepare clipped common fields once; tentative serialization uses already prepared values without adding counters. Final omission accounting covers the actual returned projection.

- [x] Add failing tests: one 600-character statement, short fields, 1/2/32 nodes, 65536-byte budget: one omission and 512 returned characters. Short statement plus one 600-character predicate also gives one clipped retained field.
- [x] Add a small-budget fixture that rejects a tentative node containing a clipped predicate; count omitted nodes and retained clipped fields, without double counting the rejected predicate. Cover empty nodes with a long statement and the explicit no-node-fit result.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~ArtifactReaderTests` and `dotnet test tests/SqlHarness.Mcp.Tests --filter FullyQualifiedName~McpTokenBudgetTests`; confirm the new invariant fails on the current path.
- [x] Implement side-effect-free fitting and once-only accounting without changing cell bounds or unrelated projections.
- [x] Repeat the focused tests; verify expected counts in CLI and MCP envelope paths.

**Acceptance:** Omission count describes distinct omitted output content and is independent of the number of tentative node fits.

### T2: Return the largest fitting page within a bounded source window

**Files:** `src/SqlHarness.Core/ArtifactReader.cs`; projection file from T1; both test files above; `docs/mcp.md`; README artifact guidance only if behavior is described there.

**Interfaces:** Raise the plan reader source window from 32 to 128 nodes. Keep `cursor` and `Continuation` semantics; the shared projection chooses the fitting prefix. Other artifact section page sizes are outside scope.

- [x] Add failing synthetic reader-to-adapter tests: 37 short nodes fit one response at 65536 bytes, `continuation:null`, all unique paths present. This is a synthetic test, not a copied workload result.
- [x] Add a 257-node synthetic plan at 4096 bytes; reconstruct all paths through continuation with no gaps/duplicates and every response within budget. Cover `includeText:true/false`, nonzero cursor, final page and explicit no-node-fit.
- [x] Implement the bounded source window, retaining T1's fitting/accounting behavior.
- [x] Run the same focused test commands; update docs for budget-aware pages without promising a fixed returned node count or token savings.
- [x] Record a complete synthetic before/after workflow: input fixture, selectors, response budget, call count, emitted UTF-8 bytes and explicitly estimated tokens. Keep discovery/input/chat costs separate or label them unmeasured. More nodes per call may increase bytes; do not infer savings from call count.

**Acceptance:** Small fitting plans need one read; larger plans remain complete and bounded. The decision record retains the observed **14 = 6 first + 6 continuation + 2 repeated** calls and distinguishes this history from new synthetic measurements.

### T3: Verify and record the delivered decision

**Files:** `plans/README.md`, this plan and a scoped verification record; relevant docs from T2.

- [x] Review full scoped diff and privacy; run `git diff --check`.
- [x] After relevant code changes, run `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`, one gate at a time. Record exit codes and actual gate outcomes; partial output is not a pass. The Windows retry passed; the first requested Linux run stopped at its committed-tip UI timeout, then one retry passed at the same committed base `40a6871d`; the exact implementation snapshot's ext4 equivalent stages also passed after Git LF normalization.
- [x] Record whether P1's correctness proof and P2's complete-workflow results justify adoption. Preserve the historical artifact-call breakdown and any remaining evidence gaps. No live database proof is required for this projection-only change.
- [x] Update status/index using actual evidence. Keep delivered-change commits cohesive; no intermediate red/green or agent-loop commits. Commit, integrate, install or publish only under separate authorization.

## STOP conditions

Stop for incompatible selector/provenance semantics, loss of scope-owner enforcement, inability to preserve bounded responses or complete continuation, or work outside this accepted planning scope. Compatible HEAD drift is a reason to adapt, not an automatic STOP.
