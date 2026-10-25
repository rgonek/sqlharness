# Plan 116: Specify loaded producer identity for MCP evidence

> **For agentic workers:** Use `superpowers:executing-plans`, or delegated execution when the operator selects it. Execute the checked tasks in order. This is a design study, not product implementation. Read this file in full and update its index row when the study lands.
>
> **Drift check:** `git diff --stat 5f7342a7..HEAD -- src/SqlHarness.Mcp src/SqlHarness.Core/Capabilities.cs src/SqlHarness.Core/Journal src/SqlHarness.Core/ArtifactReader.cs docs/mcp tests/SqlHarness.Mcp.Tests`
> Reconcile changed excerpts against current source. Compatible drift is routine; stop for changed authorization, an incompatible contract, or missing evidence that would require a broader study.

## Status

- **Status:** RESEARCH COMPLETE; locally integrated into `main` at `6ba811d5` with operator authorization; candidate A accepted as a separately selected implementation direction, B/C deferred; this study authorizes no implementation, installation, restart, live database, merge or push.
- **Priority:** P2; direction proposal 1 selected on 2026-10-25.
- **Effort / risk / confidence:** M / MED / HIGH grounding; benefit is not measured.
- **Depends on:** no new hard predecessor; 107/113 diagnostic records already exist.
- **Planned at:** `5f7342a7f5d48e03268491059949d1edfc24c65c`, 2026-10-25.
- **Goal:** Decide the minimum producer-identity contract that makes future runtime/source divergence attributable.
- **Architecture:** Extend existing capability/provenance concepts; do not introduce a health endpoint. Compare capabilities-only visibility with bounded persistence at evidence creation.
- **Tech stack:** .NET 10 C#, System.Text.Json, existing MCP SDK and PowerShell for offline study fixtures.
- **Spec:** The candidate contract and constraints below are the study specification. The resulting `116-evidence/design.md` is the design deliverable, not approval to implement it.

## Execution setup

Use an isolated `plan-116` checkout; inspect `git status --short` and `git worktree list --porcelain` first. At study start run `$studyStart = (git rev-parse HEAD).Trim()` and record that revision and the initial status in the evidence. Preserve unrelated work; commit only selected study files if separately instructed. Use `docs/agents/verification.md`: this notes/fixture-only study needs scoped checks, not a product gate. Every fixture runner and verifier must fail with a nonzero exit code on missing inputs or a failed assertion.

## Why this matters

Two investigations could not identify the managed build loaded by historical MCP processes. A PATH executable, today's installation and a random configuration reference do not establish which assemblies produced old results. Identity captured by the producing process can help future investigations without replacing binaries merely to investigate them.

## Current state

- `src/SqlHarness.Mcp/McpOperationMapper.cs:39` defines `McpCapabilitiesDocument`: `ConfigurationRef`, `ScopeMode`, engine/limit/root/policy fields; no producer version or assembly identity.
- `src/SqlHarness.Mcp/McpHost.cs:30` computes numeric `ServerVersion` with `Assembly.GetName().Version?.ToString(3)`, exposed through `ServerInfo`.
- CLI has the established record pattern in `src/SqlHarness.Core/Capabilities.cs:8`:

```csharp
public sealed record SqlHarnessCapabilities(
    string Version,
    string BuildId,
    int ContractVersion,
```

- `src/SqlHarness.Core/Journal/JournalModels.cs:14` stores `SessionIdentity` with host PID/start, client version and protocol version, but no SQLHarness producer build.
- `src/SqlHarness.Core/ArtifactReader.cs:13` stores manifest version, ownership and `ProducerOperationRef`, which are different identities from the producer build.
- `plans/107-evidence/task-3-diagnosis.md:9-17` distinguishes loaded managed identity from the historical PATH candidate. `plans/113-evidence/task-3-decision.md:36-45` retains the same historical uncertainty.
- Match additive nullable properties and explicit unavailable evidence, as in `ArtifactManifest.ProducerOperationRef`; do not fabricate old identity from the current process.

## Global constraints

- Read `docs/mcp.md`, `docs/mcp/startup.md` and `docs/mcp/runtime.md` before proposing a public MCP contract. Only local stdio exists; this study adds no transport or tool.
- No source/test edits, user-home reads, process inventory, credentials, database calls, binary replacement, server restart or real-client launch.
- `configurationRef` remains configuration identity; artifact ownership remains authorization; neither becomes producer identity.
- No absolute executable paths, environment dumps or executable-file hashes by default. An on-disk hash is not proof of an already loaded managed assembly.
- Keep original SQL/targets/results readable under existing bounds. This feature adds no masking policy.
- The default response budget is 65,536 UTF-8 bytes; accepted budgets remain 4,096..1,048,576 under process limits. This study must not increase them.

## Review focus

1. Two development builds can both have numeric version `0.0.0`; they must remain distinguishable when stronger loaded facts exist.
2. Missing/trimmed informational metadata and older journal/artifact evidence remain explicitly unavailable.
3. A changed PATH file and a still-running older process must not be conflated.
4. Capability root paging at minimum budgets must retain current progress/completeness semantics.
5. Producer identity is diagnostic provenance, never approval, authentication or a compatibility guarantee.

## Scope and deliverables

**Only writable paths:** this plan, its index row/section in `plans/README.md`, and `plans/116-evidence/` containing `inventory.md`, `design.md`, `cases.json`, `evaluate.ps1`, `verify.ps1`, `results.json`, `decision.md` and review/check logs. Do not update README, production contracts or source.

Read-only integration map: MCP host/mapper/catalog, CLI doctor/capabilities, journal identity/schema/write paths, artifact manifest producer/reader, `McpToolSchemaTests.cs`, `McpTokenBudgetTests.cs`, CLI `DoctorCommandTests.cs` and `CapabilitiesCommandTests.cs`.

## Candidate contract to evaluate

Evaluate an additive `producer` object on the existing MCP capabilities result. Proposed fields: `identityVersion: 1`; entries for loaded `SqlHarness.Mcp` and `SqlHarness.Core`, each with `name`, numeric `version`, nullable `informationalVersion`, nullable `moduleVersionId`; and a compact availability/reason field. Read facts from the corresponding loaded types, not by searching disk. MVID distinguishes modules but is neither a content signature nor proof of trust. Do not echo filesystem locations.

Study three alternatives: A capabilities only; B capabilities plus immutable producer data on new journal sessions; C B plus producer data in newly written artifact manifests. For B/C trace the actual producer chain: CLI, MCP, Core and dashboard are not necessarily the same binary instance. Specify who stamps and carries each fact; prohibit stamps from the current artifact reader. Give each alternative a compatibility, byte-cost, retention and diagnostic-coverage row.

## Tasks

### Task 1: Establish the attribution and integration map

- [x] Read the files above and create `inventory.md` with exact symbols, excerpts, schema/version homes and the two documented incident limits. Record what a capability response can identify now versus what persisted evidence would need later.
- [x] Verify: `Select-String -Path plans/116-evidence/inventory.md -Pattern '107|113|McpCapabilitiesDocument|SessionIdentity|ArtifactManifest'` -> all five topics present with source references. Manually reopen every cited excerpt.

### Task 2: Freeze the candidate and exercise synthetic identities

- [x] Write `design.md` with exact proposed field names/types/nullability, no changes to handshake version semantics, the A/B/C matrix, and a minimal future file/test map. Pin the chosen persistence boundary or explicitly defer it.
- [x] Create `cases.json` with separate expected outputs for: release identity, two different development MVIDs with the same numeric version, unavailable metadata, old artifact without producer, reader/build different from producer, PATH/build different from loaded facts, and multiple capability root pages.
- [x] Implement `evaluate.ps1` as an offline candidate projector over synthetic records only. `results.json` records case IDs, proposed JSON, UTF-8 byte counts at 4,096 and 65,536 budgets, availability and omissions. Use System.Text.Json for wire estimates, or label another serializer's measurements as non-wire estimates. Do not load a real target process.
- [x] Implement `verify.ps1` using independent expected-case assertions: same loaded identity stays stable across pages; distinct module facts remain distinct; historical missing values stay unavailable; no secret/path fields; every candidate result fits the selected budget or explicitly records non-fit. Assert the core capability page remains progress-capable in the proposed reservation design.
- [x] Verify: `pwsh -NoProfile -File plans/116-evidence/evaluate.ps1` then `pwsh -NoProfile -File plans/116-evidence/verify.ps1` -> exit 0, all seven scenarios accounted for. Synthetic estimates are not production SDK acceptance.

### Task 3: Decide and produce the implementation handoff if warranted

- [x] Write `decision.md`: accept A/B/C, defer, or reject; explain which future incident each accepted surface resolves and which it cannot resolve. Include evidence, alternatives, exact future tests, costs and unresolved questions. Historical 107/113 identity stays unresolved.
- [x] If accepted, provide the full implementation contract/file map in `design.md`; reserve a later numbered implementation plan only after checking numbering and separate operator selection. Do not implement or automatically create another plan as part of this study.
- [x] Specify future regression commands: `dotnet test tests/SqlHarness.Mcp.Tests --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests'`; `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~DoctorCommandTests|FullyQualifiedName~CapabilitiesCommandTests'`; then Windows `just verify` and explicit changed-branch formatting. These are future product gates, not required for a notes-only study.
- [x] Verify: run the study verifier and `git diff --check`; update index status to RESEARCH COMPLETE with decision and limits, not IMPLEMENTED.

## Done criteria and checks

- [x] `pwsh -NoProfile -File plans/116-evidence/verify.ps1` exits 0 and checks every named scenario and A/B/C disposition.
- [x] All seven named evidence files in Scope are present; relative Markdown links resolve; current excerpts were independently reread.
- [x] `git diff --name-only $studyStart -- src tests scripts` is empty; new/untracked paths were also checked with `git status --short`.
- [x] Evidence says synthetic/offline, exact studied revision and serializer; no claim of live attribution, token savings or product verification.

## STOP conditions and maintenance

Stop if a useful identity requires scanning user processes/configuration, adding authorization semantics, exposing credentials or inspecting live databases. Report if all available loaded facts are indistinguishable; do not substitute PATH identity. Reconcile harmless source drift rather than abandoning the study. A future implementation must update authoritative MCP descriptions/capabilities and affected version docs together; B/C must explicitly test old rows/manifests and producer-versus-reader identity. Work in an isolated `plan-116` checkout, preserve unrelated edits, and do not merge, push or publish.
