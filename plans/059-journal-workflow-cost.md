# Plan 059: Workflow cost, explicit evidence provenance and honest comparison baselines

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox tracking.
>
> **Drift check:** Read changes since 27d2a9a in src/SqlHarness.Core/Journal, Contracts.cs,
> BenchmarkCollector.cs, CompareCellRunner.cs, Artifacts.cs, ArtifactReader.cs, SqlHarnessModule.cs,
> src/SqlHarness.Mcp and src/SqlHarness.Dashboard. Plans 055, 056 and 058 are expected first.
> Adapt the implementation to drift; changed files alone are not a STOP condition.

**Goal:** Report measured response/workflow cost without presenting a constructed raw-output baseline
as real token savings; link every new artifact read to its producer by an explicit operation reference.

**Architecture:** Generate an opaque operation reference before execution and carry it through outcomes,
journal rows and benchmark manifests. Artifact reads use the manifest's producer reference, including matrix
cells. Costs retain byte measurements and explicitly estimated tokens; savings require a named comparable
baseline and exclude unmeasured input/discovery costs rather than silently claiming them.

**Tech Stack:** C#, .NET, xUnit, SQLite, React dashboard.

**Spec:** docs/superpowers/specs/2026-10-12-agent-contract-review-design.md, decision 11 and accepted refinements.

## Status

- Status: DONE — implementation and documentation through `ccbb4d7`; final evidence in [059 verification](059-verification.md).
- Priority: P2
- Effort: M
- Risk: MEDIUM (journal migration, provenance propagation, statistics semantics)
- Depends on: 055, 056, 058
- Planned at: 27d2a9a, 2026-10-12

## Global Constraints

- Raw reference output counts the first measured result/messages per variant and parameter set; no plan XML,
  warm-up or repeated executions. It is a named output-volume scenario, not actual avoided agent tokens.
- Byte measurements are authoritative. Estimated token counts use the existing utf8-bytes-div-4 method;
  every UI/API comparison labels this estimate. Never imply a provider tokenizer measurement.
- Observed output cost includes all measured responses in the workflow, including artifact and dry-run calls.
  Input tokens, tools/list, capabilities discovery and final chat are outside coverage unless separately measured.
- A failed operation, or one without a comparable raw baseline, has no savings value. Overhead may be positive.
- Generate OperationRef once per top-level invocation, before journal Begin or artifact publication.
  A matrix and its cell manifests share the top-level producer reference. A read has its own new reference.
- Existing artifacts/rows remain readable; absence of producer reference means unavailable provenance,
  never a guessed parent derived from paths or timestamps. No historical backfill.
- Journal schema v7 adds operation_ref TEXT NULL (unique when present), parent_operation_ref TEXT NULL,
  dry_run INTEGER NOT NULL DEFAULT 0 and measured raw_bytes/emitted_bytes INTEGER NULL.
  Existing rows retain NULL/0 and are displayed as legacy accounting. Keep old token columns readable.
- Optional/disabled journal must not prevent SQL or artifact reads. References alone do not grant scope access.

## Review Focus

- Read a matrix cell from a later session: producer reference points to the root benchmark exactly.
- Paths containing %, _, similar prefixes or moved directories never influence producer identity.
- Journal disabled/missing producer row: read works, provenance is explicit, parent details may be unavailable.
- Increase repeat with identical synthetic data: reference volume is unchanged and output cost remains observable.
- Small queries/failed calls show measured overhead or cost without a fabricated positive savings percentage.

### Task 1: Reference output volume is independent of repetition count

**Evidence.** BenchmarkCollector currently adds plan XML and repeated outputs to raw footprint.
**Current state.** This is an artifact-volume accumulator, not a counterfactual of what a model would read.

**Files:** Modify src/SqlHarness.Core/BenchmarkCollector.cs, CompareCellRunner.cs,
MeasureParameterSets.cs and the measure path in SqlHarnessModule.cs.
Test tests/SqlHarness.Tests/CompareTests.cs, MeasureTests.cs.

**Interfaces:** Existing CanonicalResultAccumulator is unchanged; raw accumulation is optional after
the first measured execution of each variant/set. Result hashing and equivalence remain independent.

- [x] Step 1: Add synthetic tests with identical results and large saved plans.
  Assert reference output bytes for repeat=5 equal repeat=20; XML and warm-up contribute zero;
  each variant/set contributes exactly its first measured rows/messages; result hashes are unchanged.
- [x] Step 2: Run dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Compare|FullyQualifiedName~Measure".
  Expected: FAIL on current accumulated raw footprint.
- [x] Step 3: Count only first measured output, removing plan text from this accumulator.
  Apply SQL Server and PostgreSQL paths consistently; preserve saved artifacts and plan diagnostics.
- [x] Step 4: Run the same filter; expected PASS.
- [x] Step 5: Commit fix(059): comparable single-execution reference output volume.

**Acceptance:** Repeat count and plan size cannot inflate the reference output baseline.

### Task 2: Explicit producer identity and journaled artifact reads

**Evidence.** Artifact reads bypass the module journal; an inferred parent based on a directory pattern
does not reliably identify a matrix producer or a concurrent operation.
**Current state.** SqlHarnessOperation has no reference; manifests carry scope ownership but no producer.

**Files:** Modify src/SqlHarness.Core/Contracts.cs, SqlHarnessModule.cs, Artifacts.cs,
ArtifactReader.cs, ArtifactDirectoryPublisher.cs, CompareMatrixRunner.cs,
Journal/JournalModels.cs, Journal/JournalSchema.cs, Journal/ActivityJournal.cs, Journal/JournalingModule.cs;
src/SqlHarness.Mcp/McpOperationMapper.cs, McpResultAdapter.cs, and CLI artifact command.
Test tests/SqlHarness.Tests/ArtifactReaderTests.cs, ArtifactWriterTests.cs,
Journal/ActivityJournalTests.cs, Journal/JournalingModuleTests.cs, tests/SqlHarness.Mcp.Tests/McpJournalTests.cs.

**Interfaces:**
- Produces: SqlHarnessOperation.OperationRef and SqlHarnessOutcome.OperationRef (string? additive properties).
  A server-generated opaque reference is assigned once if absent at the module boundary; client MCP
  arguments cannot choose it. Journaling wrapper and inner module preserve the same assigned reference.
- Produces: ArtifactManifest.ProducerOperationRef (string?); manifest version increments additively.
  Publication receives the operation's reference before it writes/publishes the manifest.
- Produces: OperationStart.OperationRef, OperationEnd.ParentOperationRef and DryRun metadata.
- Produces: SqlHarnessArtifactReadOperation, executed through ISqlHarnessModule; its resolved report
  carries ProducerOperationRef from the owner-checked manifest.
- Produces: optional operationRef in agent outcome envelopes and parentOperationRef in artifact-read
  evidence. A reference is an identity, not proof that a journal row exists.

- [x] Step 1: Add tests asserting:
  producer and all matrix-cell manifests have the same root reference; later-session reads link exactly;
  similar paths and wildcard characters do not affect identity; concurrent producers never cross-link;
  legacy manifests remain readable and give null producer; disabled journal still executes and publishes;
  an unknown/wrong-scope artifact read is journaled as failed without attributing it to a producer.
- [x] Step 2: Run dotnet test SqlHarness.sln --filter "FullyQualifiedName~Artifact|FullyQualifiedName~Journal".
  Expected: missing producer/reference contract.
- [x] Step 3: Add the v7 migration and propagate references explicitly through the execution/publication path.
  Artifact read becomes a module operation. Resolve ownership before reading producer metadata.
  Use exact reference equality in journal joins, not directory LIKE, prefix, newest-row or timestamp matching.
  Include the new fields in CLI/MCP emission accounting; record failed reads without inventing a parent.
- [x] Step 4: Run the Step 2 filter; expected PASS; legacy artifact tests continue passing.
- [x] Step 5: Commit feat(059): explicit producer references and journaled artifact reads.

**Acceptance:** New provenance is deterministic even across sessions and without a live journal.
Historical provenance is unavailable rather than guessed.

### Task 3: Cost coverage and optional comparable savings

**Evidence.** Current savings aggregate raw_tokens minus emitted_tokens and hides missing workflow costs.
**Current state.** A byte/4 estimate and a constructed baseline are displayed as token savings.

**Files:** Modify src/SqlHarness.Core/Journal/JournalModels.cs, ActivityJournal.cs, OperationJournalDescriber.cs,
JournalingModule.cs; src/SqlHarness.Dashboard/JournalReader.cs, DashboardModels.cs and ui/src statistics/detail.
Test tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs,
tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs and neighboring UI tests.

**Interfaces:**
- Produces: WorkflowCost(root operation reference, output bytes, estimated output tokens, call count,
  tokenEstimationMethod, coverage, optional comparison).
- Comparison has baselineKind=single-measured-execution-output, baseline bytes/tokens, outputOverheadBytes
  (= observed output minus baseline), optional estimatedOutputTokenDelta (= baseline minus observed output).
  It never reports actual token savings or includes unavailable input cost.
- Coverage marks input/discovery/chat unavailable unless measured. Legacy rows use legacy-artifact-volume
  and remain outside new comparable totals.
- Dry runs have dry_run=1, count in output cost/call count and are excluded from database-execution counts.

- [x] Step 1: Synthetic reader tests:
  root output 400 estimated tokens plus two reads of 300 each yields total 1000;
  reference baseline 1000 yields zero estimated output delta, not claimed avoided agent tokens;
  a larger emitted query output reports positive overhead; missing baseline has comparison=null;
  mixed legacy/new windows do not sum incompatible baseline kinds; dry runs cost output but no DB execution.
  Producer outside the selected time window remains linkable; count each in-window response once.
- [x] Step 2: Run dotnet test SqlHarness.sln --filter "FullyQualifiedName~Journal|FullyQualifiedName~Dashboard".
  Expected: old savings contract fails the coverage/comparability assertions.
- [x] Step 3: Persist actual raw/emitted byte counts alongside estimates, expose coverage and baseline kind,
  group by explicit references and update UI labels. Do not fabricate input/tool-discovery measurements.
  Capabilities remains unjournaled in this plan and is explicitly excluded from reported coverage.
- [x] Step 4: Run the filter and UI tests; expected PASS.
- [x] Step 5: Commit feat(059): workflow output cost with explicit estimate and baseline coverage.

**Acceptance:** The dashboard distinguishes measured bytes, estimated tokens and a named output comparison;
an incomplete cost measurement is visibly incomplete.

### Task 4: Documentation, gates and index

**Files:** README.md, docs/mcp.md, skills/sqlharness/SKILL.md, tests/SqlHarness.Tests/DocsSyncTests.cs,
plans/README.md.

- [x] Step 1: Document refs, producer provenance, v7 fields, baseline kind, estimation method, excluded costs,
  legacy display and dry-run execution counts. Report/storage implementation remains outside this plan.
- [x] Step 2: Run dotnet build SqlHarness.sln -warnaserror; pwsh scripts/verify.ps1;
  pwsh scripts/verify-linux.ps1 sequentially. Expected: 0 warnings, verify: OK and verify-linux: OK.
- [x] Step 3: Commit docs(059): provenance and honest workflow cost; mark DONE with evidence in the index.

## Done criteria

- [x] Repeat and plan XML size cannot inflate the named reference output baseline.
- [x] New matrix-cell artifacts reference their root producer exactly; legacy reads do not infer one.
- [x] Output cost includes linked reads and dry runs, without double counting.
- [x] Byte measurements, token estimates and coverage are distinct; no unsupported claim of actual savings.
- [x] Both gates pass; index updated.

## STOP conditions

- Result equivalence/hash uses raw footprint: separate the accumulators before changing accounting.
- Producer identity cannot be supplied before atomic publication: refactor the execution context in Task 2;
  do not fall back to rewriting manifests after publication or matching paths.
- Stored journal compatibility requires another migration number on changed HEAD: adapt the migration once,
  keep legacy data and identify its accounting semantics.
