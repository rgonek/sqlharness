# Plan 116 Task 2: frozen candidate design

Captured at `443f468e620e98c2e65fb109040d91ef06c286c5`. This is an offline design study. It does not change product code, execute a server, inspect processes, or establish historical identity.

## Proposed capabilities contract

Add one additive, always-present `producer` object to the existing `sqlharness_capabilities` result. It reports the facts of the assemblies already loaded into the running MCP process. It does not change tool inputs, tool count, protocol negotiation, or `McpHost.ServerVersion` / MCP `serverInfo.version` semantics.

```json
{
  "producer": {
    "identityVersion": 1,
    "availability": "available",
    "reason": null,
    "assemblies": {
      "mcp": {
        "name": "SqlHarness.Mcp",
        "version": "1.2.3",
        "informationalVersion": "1.2.3+abc123",
        "moduleVersionId": "00000000-0000-0000-0000-000000000001"
      },
      "core": {
        "name": "SqlHarness.Core",
        "version": "1.2.3",
        "informationalVersion": "1.2.3+abc123",
        "moduleVersionId": "00000000-0000-0000-0000-000000000002"
      }
    }
  }
}
```

Exact JSON contract: `identityVersion` is integer `1`; `availability` is the string enum `available | partial | unavailable`; `reason` is a nullable string enum, null when available, `optional_metadata_missing` for partial, and `loaded_assembly_metadata_unavailable` for unavailable. `assemblies.mcp` and `.core` are required objects in every availability state. Each has required string `name`, a dotted numeric `version` (the loaded `AssemblyName.Version` rendered with `ToString(3)`) that is non-null when available/partial and null when unavailable, and nullable strings `informationalVersion` and `moduleVersionId`. If a required loaded assembly/version fact cannot be read, retain the entry with its canonical assembly `name`, `version: null`, and both optional facts null; report `unavailable` with `loaded_assembly_metadata_unavailable`. This unavailable-only null is an explicit fail-safe exception to the normal non-null version contract. When required facts are available but one or more optional facts cannot be read (including an absent informational-version attribute), retain known values, use explicit null for the missing optional values, and report `partial` with `optional_metadata_missing`. When all facts are read, report `available` and a null reason. Do not omit a known-null field: consumers can distinguish a known unavailable fact from an unrecognized future shape. Optional-field read failures must not be converted into required-fact failures; the implementation should isolate retrieval per required/optional fact and apply these availability rules.

Read `typeof(McpHost).Assembly` and a stable type owned by Core (for example `typeof(SqlHarnessCapabilitiesProvider).Assembly`) in-process. Read `GetName().Name`, `GetName().Version`, `AssemblyInformationalVersionAttribute.InformationalVersion`, and `ManifestModule.ModuleVersionId`. Do not reflect over disk, `Assembly.Location`, process command lines, PATH, executable hashes, environment variables, or configuration. Do not call the producer `serverVersion`; preserve `serverInfo.version` exactly as the existing MCP SDK handshake/discover value. A module version ID distinguishes module builds for diagnostics; it is not a content signature, trust assertion, semantic compatibility guarantee, or proof of a historical process beyond facts stamped by that process.

`configurationRef` remains the random frozen-configuration identity; client information and protocol revision remain client/protocol identity; artifact `Owner` remains authorization matching; `ProducerOperationRef` remains operation linkage. None is overloaded as producer identity.

## Persistence alternatives

| Option | What is stamped and where | Compatibility and retained size | Diagnostic coverage and limits |
|---|---|---|---|
| A. Capabilities only | Loaded MCP+Core facts in each capabilities response. No journal or artifact persistence. | Additive root member; old clients can ignore it. Approx. 300–400 UTF-8 bytes per response for ordinary short values. The complete serialized producer object is reserved in every availability state, including `unavailable`; nullable facts and the reason still consume bytes. No retained storage. | Live caller can compare loaded assemblies with inspected source/build. No link to later historical rows after process exit; does not resolve 107/113 retrospectively. |
| B. A + producer on new journal sessions | MCP startup captures one immutable producer snapshot; passes it through the existing process `SessionIdentity` and all `WithMcpClient` copies; Core `ActivityJournal.UpsertSession` writes it only on initial session insert. Dashboard reads it from session rows. CLI sessions must not inherit MCP facts; the CLI may stamp its own loaded CLI/Core facts only if the implementation scope explicitly defines a separate CLI producer shape. | Additive nullable session fields or a versioned producer JSON column. Old rows remain null/unavailable; no backfill. Approximately 250–400 bytes per new MCP session, once per session rather than per operation. Schema migration and dashboard response additions required. | Durable association for journaled calls in a session, including restart-safe retained history. Does not prove an unjournaled call, a different process/session, or the cause of 107/113 absent original session evidence. Dashboard binary must project stored facts, never substitute its own build. |
| C. B + producer on new artifact manifests | B as above; artifact writer stamps the producer snapshot captured by the producer process alongside the report/manifest. Readers preserve and return that stored value. | Additive optional manifest member; legacy v1/v2 manifests stay null/unavailable. Around 250–400 bytes per new manifest/artifact, with duplication versus session storage. Existing manifest version compatibility needs an explicit decision (optional additive property may fit v2 reader compatibility, but changing manifest version should be evaluated rather than assumed). | Allows a copied artifact to retain originating build identity after journal loss. Artifact writer must receive the producer snapshot from execution context; `ArtifactReader` must never stamp its own build. Does not prove trust or cover artifacts written outside that flow. |

**Persistence decision for this task:** defer B and C pending the Task 3 decision. The best bounded next step is A because it adds immediate live attribution without schema/storage coupling; persistence needs explicit acceptance of the schema, old-record semantics, artifact ownership flow, and duplicated-source-of-truth costs. No selected boundary is silently implied by the capability proposal.

## Root-page reservation

The producer is constant per process, so every capabilities page must carry the same complete producer object. The future mapper should reserve the serialized bytes of the complete producer plus the minimum capabilities envelope before selecting optional input roots or diagnostics. Root paging then measures complete serialized `sqlharness_capabilities` results as it does today. If the reservation cannot fit within the requested result budget, return the existing explicit `output_budget_too_small` behavior; do not drop or truncate producer identity, return a page that looks complete, or stop advancing the root cursor. A page with `InputRootsComplete == false` and a non-null continuation remains progress-capable. A single root that cannot fit remains counted in `InputRootsOmitted` and skipped, while producer identity stays present on each returned page.

The synthetic fixture models two pages, stable producer bytes, complete per-page response budgets, and cursor progress. Its PowerShell serializer is explicitly a **non-wire estimate**: production acceptance must use the actual MCP result adapter / `System.Text.Json` envelope, including its JSON options and minimum-budget behavior.

## Future implementation map (contract accepted for a separately selected implementation; not authorized by this study)

1. `src/SqlHarness.Mcp/McpOperationMapper.cs`: capability records, loaded-fact helper/projection, root-page reservation.
2. `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` and `docs/mcp/startup.md` / `docs/mcp/runtime.md`: public capabilities description, version distinction, and budgets.
3. If B is later selected: `src/SqlHarness.Core/Journal/JournalModels.cs`, `SessionIdentities.cs`, `JournalSchema.cs`, `ActivityJournal.cs`, and `src/SqlHarness.Dashboard/JournalReader.cs` / `DashboardModels.cs`; add schema migration and old-row null tests. MCP host is the authoritative producer stamper; dashboard only carries stored data.
4. If C is later selected: `src/SqlHarness.Core/ArtifactReader.cs` manifest optional property plus `Artifacts.cs` writer call chain; thread the execution-time producer snapshot into writer context. Reader code only returns persisted provenance.
5. Focused future regression commands: `dotnet test tests/SqlHarness.Mcp.Tests --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpTokenBudgetTests'`; `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~DoctorCommandTests|FullyQualifiedName~CapabilitiesCommandTests'`; then Windows `just verify` and explicit changed-branch formatting. These are future product gates, not executed by this notes/fixture-only study.

6. Implementation selection must create or assign a numbered implementation plan after checking the then-current queue. This study accepts A only; it does not create that plan or authorize implementation. The owning implementation must retain the exact null/availability rules above and cover each result with focused tests. Required and optional metadata access must be independently guarded so one missing optional value does not erase known required facts.

## Expected observations

- Same loaded MVIDs and assembly facts remain identical across capability pages.
- Two development modules with numeric version `0.0.0` remain distinct by MVID even when informational versions match.
- Missing optional loaded metadata is null and marked partial; it is not inferred from a reader, disk candidate, or prior release.
- Old manifests and rows without a producer remain explicitly unavailable and are never backfilled from the current reader.
- A changed PATH/build candidate does not replace in-memory facts.
- The candidate adds no paths, secrets, environment dump, SQL, targets, result masking, handshake-version changes, transport, or tool.
