### Spec Compliance

- ❌ Issues found: `evaluate.ps1:52-53` takes `availability` and `reason` from `case.expect` and passes them directly into `New-Producer`; `verify.ps1:39-40` compares the output to those same expectation fields. This does not independently project or verify availability, and no fixture exercises the defined `unavailable` state for unreadable required assembly/version metadata. Derive status/reason from synthetic loaded facts and assert against a separate golden expectation.
- ❌ Issues found: `design.md:43` says producer bytes are excluded from the existing page reserve when unavailable, while `design.md:49-53` requires the complete producer object on every page and says to reserve its serialized bytes. Resolve the contradiction; an unavailable producer still has a required object and must be budgeted.
- ⚠️ Cannot verify from this offline diff: actual MCP SDK/System.Text.Json envelope sizing, the full current optional capability payload at the 4,096-byte minimum, and live client acceptance. The design identifies these as future implementation acceptance work (`design.md:53-54`).

### Strengths

- The design defines `identityVersion`, availability/reason, both loaded assembly entries, nullable informational version and MVID, and the unavailable required-metadata representation (`design.md:33`). It preserves handshake version semantics (`design.md:10`).
- The A/B/C alternatives, explicit deferral of B/C, and future implementation/test map are present (`design.md:38`, `design.md:47`, `design.md:58`). The required scenario IDs are listed in the evaluator (`evaluate.ps1:9`); generated results include byte estimates and fit flags at both requested budgets (`results.json:347`, `results.json:348`, `results.json:867`, `results.json:868`).
- The report/source chain check is supported by current code: MCP `serverInfo.version` still uses `McpHost.ServerVersion` (`src/SqlHarness.Mcp/McpHost.cs:30`, `src/SqlHarness.Mcp/McpHost.cs:45`); the MCP and Core candidate paths are distinct (`src/SqlHarness.Mcp/McpOperationMapper.cs:139`, `src/SqlHarness.Core/Capabilities.cs:202`); CLI capabilities and Doctor read their own capability/version paths (`src/SqlHarness.Cli/Commands/CapabilitiesCommand.cs:34`, `src/SqlHarness.Cli/Commands/DoctorCommand.cs:52`). MCP session identity flows through `SessionIdentities.Mcp`/`WithMcpClient` into `ActivityJournal.UpsertSession` (`src/SqlHarness.Mcp/McpHost.cs:151`, `src/SqlHarness.Core/Journal/SessionIdentities.cs:44`, `src/SqlHarness.Core/Journal/SessionIdentities.cs:70`, `src/SqlHarness.Core/Journal/ActivityJournal.cs:1122`); dashboard projection reads journal session columns (`src/SqlHarness.Dashboard/JournalReader.cs:82`, `src/SqlHarness.Dashboard/JournalReader.cs:1677`).
- The fixture labels PowerShell serialization as non-wire estimates (`cases.json:3`), and the report supplies the requested focused command outputs. I did not rerun the scripts per the reviewer prompt.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `plans/116-evidence/evaluate.ps1:52-53`; `plans/116-evidence/verify.ps1:39-40` — Availability/reason are copied from the expected output and compared to the same values, so the candidate cannot demonstrate its status mapping. The unavailable contract is consequently untested. Make the projector derive the status from synthetic facts, add a required-metadata-unavailable case, and have the verifier compare to independent expected values. Plan-mandated independence is missing.
- `plans/116-evidence/design.md:43,49-53` — The A row excludes producer bytes from the reservation when unavailable, but the contract keeps an always-present producer object and the paging design reserves the complete object. A future mapper could reserve too little or handle this state differently across pages. State that the serialized object is reserved in every availability state, with `output_budget_too_small` if the minimum envelope cannot fit.

#### Minor (Nice to Have)

- `plans/116-evidence/evaluate.ps1:111,124` — `candidateArtifactProducerBytes` measures `$producer` (the current reader's loaded identity), even when `$persistedProducer` holds a different originating producer; old-artifact cases also report a payload size despite no stored producer. Measure the persisted producer when present, or label this as a generic schema-size estimate rather than an artifact payload estimate.
- `plans/116-evidence/evaluate.ps1:66` — Root pages are split by fixture `pageSize` before budget estimates are calculated; the minimum-budget assertion proves these compact, predetermined pages fit, but does not exercise a budget-driven page-selection/reservation path. This limitation is disclosed in the report; keep it explicit as synthetic progress evidence, with actual adapter behavior deferred to implementation acceptance.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The frozen contract, alternatives, scenarios, and scope boundaries are clear, and the reported focused checks match the offline nature of the task. The status projector/verifier are circular for availability, and the budget design has a contradictory rule for unavailable identity, so this snapshot does not yet fully meet the task's independent-evidence and reservation requirements.
