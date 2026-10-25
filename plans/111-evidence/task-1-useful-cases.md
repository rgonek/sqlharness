# Plan 111 Task 1: Useful cases and existing boundaries

Research snapshot: `621a25210fc3338dcea90392895ac62957d8f5f0` (`plan-111`). This is source/documentation analysis only; no database, installed MCP process, or live artifact was consulted.

## Drift from the planned base

Ran the plan's six-path comparison from `677b2f93b66c06d3dc0bda8475c7570ef29738d9` to `HEAD`. Four paths differ: `docs/mcp/artifacts.md` (+10/-2), `src/SqlHarness.Core/AgentOutputLimits.cs` (+15), `src/SqlHarness.Core/SqlExecution.cs` (+4/-2), and `src/SqlHarness.Mcp/McpOperationMapper.cs` (+12/-6). `src/SqlHarness.Core/PlanDistiller.cs` and `docs/mcp/inputs.md` are unchanged.

The artifacts documentation now describes additional compact statement discovery and diagnostics while retaining the saved `plan` section's role: it reads distilled plan JSON contained in an artifact, does not read or regenerate raw XML, and distinguishes measured/runtime fields. The `AgentOutputLimits` delta adds error-cell helpers; `MaximumCellCharacters` remains 4096. The `SqlExecution` and `McpOperationMapper` changes concern newer statement evidence surfaces; the existing plan-input mapping still calls `McpInputReader.ReadPlanAsync` and creates a `SqlHarnessPlanOperation`. None of these changes supplies a catalog/Query Store retrieval path or a query-cell-to-distiller transfer. This source drift does not establish which binary, if any, is currently running.

## Synthetic cases

These are analysis fixtures, not executable SQL and not live acceptance.

### A. Measurable SELECT

**Synthetic task:** “For the current read-only report query and its supplied parameter set, identify the expensive operators, compare estimates with observed rows, and report current elapsed/CPU evidence.”

**Required evidence:** Exact query/case identity and parameter-set identity; current measured repetition; saved plan/operator shape; estimate and runtime row/execution counters; measured timing; explicit omissions/availability. For SQL Server, relevant statement and per-table counters may also be needed. Plan shape alone does not answer current runtime cost.

**Existing supply:** Yes, when the case can be executed under the existing measure contract and the artifact actually contains plan/counter evidence. `measure` captures new runs; the artifact `plan` section reads saved distilled plan data. `sqlharness_plan` only distills caller-supplied plan text and is not a measurement or fetch. The current artifact documentation states that saved-plan nodes are paged and `planShapeStable`/evidence gaps do not turn plan shape into timing evidence.

**Expected outcome:** Existing measure + artifact reads can answer the synthetic current-run question with source identity and bounded evidence. No stored-plan retrieval feature is needed for this case.

**Observed source outcome:** The current docs describe these surfaces as above (`docs/mcp/artifacts.md`, “Artifacts”); `ArtifactReader` declares `plan` as a supported section and pages saved nodes (`src/SqlHarness.Core/ArtifactReader.cs`, lines 353-439). No live artifact was read, so whether a particular run contains complete evidence remains unknown.

### B. Historical SELECT with unavailable parameters

**Synthetic task:** “Explain why yesterday's parameter-sensitive SELECT chose a scan. The original parameter set is no longer available, and do not rerun it against production.”

**Required evidence:** The historical plan document or plan identity; capture/compile time and source attribution; statement identity and relevant parameter context (including whether values are absent, redacted, or only inferred); database/schema/index context at the time; warnings and optimizer estimates. Distinguish historical estimated/stored evidence from current measured runtime facts. If the original parameters are unavailable, say so rather than substituting a guessed set.

**Existing supply:** `measure` cannot reproduce the original historical run or parameter context; it executes a new benchmark case. The `plan` artifact section only reads plan JSON already saved in benchmark artifacts. The standalone `sqlharness_plan` accepts a caller-provided XML/JSON document and distills it offline, but does not discover a historical plan in a catalog. A caller-controlled catalog SQL query/export could be an alternative only if separately reviewed for permitted source, bounded server-side projection/export, and safe rooted-file handling; this study has not established an existing end-to-end workflow for that retrieval.

**Expected outcome:** Existing tools can inspect a historical document once an operator supplies it, but do not recover a missing historical document or its parameter context. Any proposed catalog retrieval must label the result as stored/estimated historical evidence and must not claim current runtime measurements.

**Observed source outcome:** MCP plan input is exactly one of inline `content` or rooted `file`; it delegates to the offline plan distiller (`McpInputReader.ReadPlanAsync`, lines 60-73; `McpOperationMapper.MapPlanAsync`, `src/SqlHarness.Mcp/McpOperationMapper.cs`, lines 551-575; `SqlHarnessModule.ExecutePlan`, `src/SqlHarness.Core/SqlHarnessModule.cs`, lines 1561-1587). Saved artifact `plan` reads saved JSON, not source XML (`docs/mcp/artifacts.md`, “Artifacts”). No catalog lookup appears on these inspected paths. Whether the historical plan remains in a particular engine catalog, and whether original parameter values are retained, are unresolved and environment-dependent.

### C. DML stored plan

**Synthetic task:** “Review the stored plan for a past `UPDATE` before deciding whether to schedule another maintenance window. Do not execute the DML.”

**Required evidence:** Stored-plan source and age; statement/target identity; operators, indexes, predicates, estimates, warnings, compile context, and parameter context if available. Clearly label missing runtime row counts, actual duration, lock/log impact, and current data/index conditions. Retrieval must not execute the DML or imply that a plan-only review predicts actual write cost.

**Existing supply:** `measure` is not a substitute for retrieving the old DML plan and would require a new execution path; execution of the target DML is out of scope. PostgreSQL measured SQL validation explicitly accepts one explainable `SELECT` (`src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`, lines 89-97); other engine-specific execution checks should be assessed by a future implementation task rather than inferred here. `sqlharness_plan` can distill a supplied plan offline, and `sqlharness_artifact` can read a saved measure/compare plan when one exists, but neither performs stored-plan discovery.

**Expected outcome:** Without caller-supplied plan text or an existing artifact, current tools cannot answer. A catalog retrieval might fill this gap without executing DML, but it is only a candidate design; source selection, plan age, permission behavior, input size, and historical parameter availability still need study.

**Observed source outcome:** The plan tool only maps caller text/file to `SqlHarnessPlanOperation` (`McpOperationMapper.cs`, lines 551-575) and `ExecutePlan` distills that input without connecting (`SqlHarnessModule.cs`, lines 1561-1587). No live DML or database path was used. Whether SQL Server measure would reject every DML form is not asserted here; regardless, measuring a new DML execution would violate this synthetic case.

## Source safety, trust, and size boundaries inspected

- **Input admission:** Inline plan content is limited to 1 MiB UTF-8. File input must be an absolute path lexically below a configured startup `--input-root`; UNC/network paths are rejected; no configured root disables file input. Plan documents are capped at 16 MiB UTF-8 and bounded reads honor cancellation. See `src/SqlHarness.Mcp/McpLimits.cs`, lines 5-25; `src/SqlHarness.Mcp/McpInputReader.cs`, lines 60-73, 149-218, 223-244; and `docs/mcp/inputs.md`, “Input roots and file inputs.”
- **Trust boundary:** Input roots scope caller-supplied paths; the reader does not resolve links/reparse points or defend against same-user replacement while reading. Same-user local processes can read or alter filesystem entries. Client `roots/list` never widens the allowlist (`docs/mcp/inputs.md`, “Client workspace roots”). Rooted file delivery is therefore a controlled local workflow, not a security boundary against the same OS identity.
- **Distillation:** `PlanDistiller` is documented as offline, has defaults of 16 MiB characters, 100,000 elements and depth 128, rejects DTDs, disables XML resolver/entity expansion, and validates the SQL Server Showplan root. It returns distilled nodes rather than raw XML (`src/SqlHarness.Core/PlanDistiller.cs`, lines 52-109 and 309-323). MCP input size is UTF-8 bytes while the parser's document bound is .NET characters; both gates apply, so the stricter one for a specific Unicode input depends on its encoding/content.
- **Saved-plan reading:** The artifact reader bounds saved plan JSON by `ArtifactReader.MaxReportBytes` (16 MiB), and `StoredPlanReader` enforces JSON depth 128 and 100,000 elements (`src/SqlHarness.Core/ArtifactReader.cs`, lines 353-373; `src/SqlHarness.Core/StoredPlanReader.cs`, lines 5-21). This reader consumes the distilled JSON form, not XML.
- **Output behavior:** The user-facing cell cap remains 4096 characters (`src/SqlHarness.Core/AgentOutputLimits.cs`, line 7); the 16 MiB input/parser ceiling does not raise response-cell limits. Artifact plan responses are independently byte-budgeted and paged (`docs/mcp/artifacts.md`, “Artifacts”). Existing PlanDistiller safety failures do not constitute catalog-query safety or catalog output bounds because no catalog retrieval exists on the inspected path.
- **Secret and attribution posture:** Current input documentation describes credential-filtered bounded path echoes, not masking caller SQL/plans. The proposed retrieval must keep SQL, parameter values, plans, target names, and useful evidence readable, while bounding size and explicitly stating source/time and stored-vs-measured status. Do not infer a running binary from source HEAD or this offline inspection.

## Task 1 decision and unresolved questions

The need is specific: a historical plan may be useful when original parameters are unavailable or when a DML statement must not be executed. Existing artifact plan reads and standalone distillation help only after plan material exists; neither performs discovery. The measurable SELECT case is already served by current measure/artifact paths. This supports investigating a bounded retrieval design in later Plan 111 tasks, but does not yet establish recurrence, safe/complete catalog fields, target-lock design, response usefulness, or a go decision.

Open questions for later tasks: which engine/catalog source is in scope; whether each candidate query can return a plan without executing user SQL; how to validate exact target/object/plan selectors; source age and parameter-context availability; server-side projection versus controlled rooted-file export; complete-document versus selected-field distillation; query count/latency/cancellation; and whether a strict capture/result bound is practical without changing the 4096-character cell cap. No live Query Store access, database write, or new plan export was performed.
