# Plan 116 Task 1: attribution and integration inventory

Captured in the assigned `D:/Dev/sqlharness/.worktrees/plan-116` worktree at study start commit `443f468e620e98c2e65fb109040d91ef06c286c5`. The tree was initially clean. The specified source drift scan from `5f7342a7f5d48e03268491059949d1edfc24c65c` over the mapped MCP, Core, Journal, CLI, dashboard, and focused test paths returned no output. This is a source/design inventory only.

## Contract boundary

Only local stdio is supported, one client per process; docs explicitly exclude HTTP/SSE, ports, OAuth, prompts, elicitation and experimental tasks (`docs/mcp.md:1-5`). Startup has fixed-profile and SQL Server-only request-scoped modes (`docs/mcp/startup.md:3-24`). `configurationRef` denotes the frozen configuration and is expressly not authorization (`src/SqlHarness.Mcp/McpOperationMapper.cs:25-34`). Artifact owner is a separate authorization tuple and matching remains exact/fail-closed across restarts (`docs/mcp/startup.md:86`; `src/SqlHarness.Core/Contracts.cs:430-449`). Neither value is a producer-build identity.

## Current capability/runtime identity

`McpCapabilitiesDocument` currently returns `ConfigurationRef`, `ScopeMode`, engines, process limits, comparison context, and optional diagnostics/input-root, mutation, timeout, annotation, artifact-page, parameter-ingress, materialization, and engine-guidance data (`src/SqlHarness.Mcp/McpOperationMapper.cs:25-70`). `BuildCapabilities` populates that document from the frozen `McpScope` or `McpProcessContext` (`src/SqlHarness.Mcp/McpOperationMapper.cs:139-207`). The public tool catalog description also labels `configurationRef` as runtime configuration information (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:742`).

The running protocol server separately reports its version in MCP `initialize.serverInfo` or `server/discover`; the docs say `sqlharness_capabilities` reports runtime configuration only (`docs/mcp/startup.md:88-93`). Core's CLI capabilities contract contains an assembly-derived `version` and `buildId` and a distinct `ContractVersion` (`src/SqlHarness.Core/Capabilities.cs:8-12,69,202-204,258-262`); Doctor similarly reads its own assembly informational version (`src/SqlHarness.Cli/Commands/DoctorCommand.cs:38-53`). These are loaded Core/CLI assembly values when those paths execute, not proof of which MCP executable/managed assembly served some historical request. `configurationRef`, capability contract versions and artifact/journal schema versions are not substitutes for that proof.

## Persisted attribution paths and version homes

- **MCP host and call integration:** `McpHost` constructs one process `SessionIdentity`, then overlays client name/version/title and negotiated protocol version for journaled calls (`src/SqlHarness.Mcp/McpHost.cs:151-160`). It also wires the journaling module (`src/SqlHarness.Mcp/McpHost.cs:176-185`). `McpOperationMapper` maps tools into Core operations; `Tools/McpToolCatalog.cs` declares the public tool list and capabilities description. Capabilities itself is not a journaled tool call (`docs/mcp/runtime.md:58-59`).
- **Journal model and schema:** `SessionIdentity` persists SQLHarness session key, client labels, MCP mode, agent and host PID/start times, CWD, roots, and protocol revision; these fields do not include a SQLHarness loaded producer assembly identity (`src/SqlHarness.Core/Journal/JournalModels.cs:14-30`). `SessionIdentities.Mcp` and `WithMcpClient` populate client-supplied labels and process facts (`src/SqlHarness.Core/Journal/SessionIdentities.cs:44-85`). SQLite session columns are defined in `JournalSchema` (`src/SqlHarness.Core/Journal/JournalSchema.cs:22-42`); `ActivityJournal.UpsertSession` writes/updates those values (`src/SqlHarness.Core/Journal/ActivityJournal.cs:1122-1157`). Dashboard `JournalReader` projects session identity fields for session/operation views (`src/SqlHarness.Dashboard/JournalReader.cs:75-89`). Journal schema migration/version source of truth is `JournalSchema.Migrations` (migration table begins at `src/SqlHarness.Core/Journal/JournalSchema.cs:8`; database version is read with `PRAGMA user_version` at `src/SqlHarness.Dashboard/JournalReader.cs:104-108`).
- **Artifact provenance:** `ArtifactManifest` has optional `Owner` and `ProducerOperationRef`; `ForReport` stamps the current manifest version, kind, report and sections (`src/SqlHarness.Core/ArtifactReader.cs:13-39`). `ArtifactReader.CurrentManifestVersion` is 2 and accepts legacy v1 (`src/SqlHarness.Core/ArtifactReader.cs:341,582-584`). The writer call chain is Core report/artifact creation and `SqlHarnessModule` journaling, where artifact reads return the producer operation ref as parent linkage (`src/SqlHarness.Core/SqlHarnessModule.cs:416-419`). `configurationRef` is not part of the persisted manifest.
- **Existing descriptions and tests:** MCP contract references are in `docs/mcp.md`, `docs/mcp/startup.md`, and `docs/mcp/runtime.md`; tool shape/description evidence lives in `McpToolSchemaTests` (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:311-320,444-452,1868-1894,2064-2078`). Budget/envelope and artifact fixtures are in `McpTokenBudgetTests` (`tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs:149-165,446-448,798-807`). CLI surfaces are `DoctorCommand`/`CapabilitiesCommand`, covered by `tests/SqlHarness.Tests/Cli/DoctorCommandTests.cs` and `CapabilitiesCommandTests.cs`. They validate offline CLI metadata, not the identity loaded by an already-running MCP server. Dashboard workflow-cost grouping is `JournalReader` parent-operation grouping (`src/SqlHarness.Dashboard/JournalReader.cs:1438-1465`) plus `OperationPage.tsx` producer-reference display (`src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx:205-212`).

## What a capability response can and cannot identify

Today a capabilities response can identify the process's frozen configuration reference, fixed/request scope mode, supported engine(s), configured process/runtime limits and policies, plus optionally diagnostic/input-root and mutation details. It does not contain a SQLHarness MCP build/assembly identity; client/server protocol version is separately exposed through MCP initialize/discover and journal protocol metadata. Existing persisted journal records can associate calls with client-supplied client name/version/title, protocol revision, process identity facts, and allowlisted call metadata. Artifact manifests can associate artifacts with an owner and producer operation reference. These are useful joins, but none identifies the exact managed SQLHarness assembly loaded for that producer process.

A future persisted producer identity would need to be captured from the running managed assembly in-process at startup/call time and correlated with the session/operation evidence. Hashing an on-disk executable after start cannot prove what managed assembly was loaded. Keep `configurationRef` as configuration identity and artifact ownership as authorization identity. Do not include absolute executable paths, environment dumps, or executable-file hashes by default; keep SQL, targets and results readable within existing bounds. Task constraints preserve the 65,536-byte default response budget and the existing 4,096..1,048,576 accepted bounds.

## Incident limits informing this study

1. **Plan 107 list-parameter mismatch:** the PATH candidate was older than inspected source, but the historical MCP processes' loaded managed assembly identity was not established; apphost/module listing and on-disk candidate hash do not prove what was loaded. A source/build mismatch is evidence, not proof of historical cause (`plans/107-evidence/task-1-build-identity.md:29-41`; `plans/107-evidence/task-3-diagnosis.md:11`).
2. **Plan 113 partial publication denial:** mitigation is present, but the original cause and loaded-binary identity remain unresolved. Bounded evidence found only the historical 2026-10-15 event in the inspected scope; absence of later records there does not establish absence elsewhere. Focused source-side integration evidence does not identify the binary handling the historical request (`plans/113-evidence/task-3-decision.md:7-9,29-45`).

These limits motivate deciding whether optional loaded-producer identity should be added to capability discovery and/or new journal/artifact evidence. The inventory makes no public contract or implementation recommendation beyond preserving the identity boundaries and constraints above.

## Anchor excerpts checked

> `Runtime configuration for the capabilities tool. <see cref="ConfigurationRef"/> is random per process and identifies this frozen configuration; it is not authorization.`
>
> — `src/SqlHarness.Mcp/McpOperationMapper.cs:29-31`

> `The server version travels in the initialize result's serverInfo (or server/discover); sqlharness_capabilities reports runtime configuration only.`
>
> — `docs/mcp/startup.md:92`

> `public string? ProducerOperationRef { get; init; }`
>
> — `src/SqlHarness.Core/ArtifactReader.cs:23`

> `The historical process inventory did not establish loaded managed assembly identity.`
>
> — `plans/107-evidence/task-3-diagnosis.md:11`

> `mitigation present; original cause unresolved; no further change`
>
> — `plans/113-evidence/task-3-decision.md:7`
