# Agent contract review: output, inputs, evidence, tool surface

Date: 2026-10-12. Base: `27d2a9a`. Source: one agent optimization session in System1 over MCP
(request scope, SQL Server), reviewed afterwards with the operator. Workload data, object names and
measurements stay out of this document.

## Problem

The session optimized one medium-size parameterized batch (history lookup, `IF` branch, temp staging,
dictionary join). It reached a valid conclusion, but a large share of its tool calls did not move the
investigation forward, and one wrong claim reached the operator:

| Observation | Cause on `27d2a9a` |
|---|---|
| A catalog listing retained only one item; the agent concluded "almost no traffic" | Non-query reports degrade from unbounded straight to `detailLimit` 1: `CalculateDetailLimit(16384, 512)` = 1 (`AgentOutputProjection.cs:48-54`, `:79-92`). Plan 038 W1 required a stepped ladder; only `query` got one (plan 041). |
| A compare matrix retained only one case; per-table reads showed only `Worktable` | Same ladder; `Take` keeps dictionary order, so the least useful table survives. |
| `artifact statements` retained only a statement prefix | Same ladder. |
| Every response carries the envelope twice | `StructuredContent` and one text block (`McpResultAdapter.cs:432-433`); the 16 KiB budget counts both, so only part of the wire budget remains for text. Actual client context duplication is not established by this transport observation. |
| A small `query` emits envelope overhead larger than row data | Envelope overhead: full target block, .NET type names, `resultHash`, `rawFootprint`, empty `messages`, `recordsAffected: -1`, zero `omitted*` counters, all twice. |
| Parameter `@Name` rejected with `detail: null`; outputs print `@Name` | `ValidateName` rejects `@` (`SqlSafety.cs` `NamePattern`); `SqlValidation.cs:177` drops the message. |
| `nvarchar` parameter bound as `nvarchar(26)` | `CreateUnicodeString` sizes by value length; applications using Dapper bind `nvarchar(4000)`, so the benchmark compiles a different plan. |
| `IF` rejected as `unsupported_statement` with `detail: null` | `ClassifyProvenStatement` has no control-flow case; the statement kind is known but not reported. |
| Matrix cells always show `parameterValue: null` | `AgentOutputProjection.cs:170`, `:687` hardcode null; `docs/mcp.md:100` promises the value. |
| A temp table appears under separate session-suffixed keys | `#name____…<hex>` differs per session; `BenchmarkRunner.cs:121-133` groups by raw name. |
| Plan operators (lookups, scans) cannot be cited | Artifacts already store distilled plans (`plans/*.plan.json`), but no artifact section serves them. |
| Statistics show inflated estimated compare savings | `raw` for compare is the accumulated artifact footprint, which no agent would read; follow-up `artifact` calls are not journaled. |
| Two parallel `query` calls got `busy` | One process-wide gate for every database operation, across scopes; it never spans processes, so it does not protect measurements from another session either. |

## Decisions (operator, 2026-10-12)

1. **One representation.** Tool results carry only the text block. `outputSchema` and
   `structuredContent` are removed. The envelope contract stays in `docs/mcp.md` and is pinned by tests.
2. **Budget.** The budget measures the UTF-8 bytes of the text the model reads. Default 64 KiB per call;
   a call may raise or lower it within the process ceiling (`--max-result-bytes`, default 256 KiB, range
   4096..1048576).
3. **Lists degrade in steps.** Every report uses the descending ladder `unbounded, 128, 64, 32, 16, 8, 4,
   2, 1, 0`. Per-table reads are ordered by median reads, descending, before any limit applies. Artifact
   sections with lists (`statements`, `operators`, `plan`) accept a cursor when truncated.
4. **Compact query envelope and matrix overview.** Target reduces to the verified database name;
   columns use short CLR valueType names, never guessed SQL types. Clipped cells carry positional and
   length metadata. Default-valued fields are omitted; resultHash/rawFootprint leave agent projection.
   Compact before budget fitting. Matrices keep a complete lightweight case inventory before optional
   repeated detail; omitted cases and omitted detail are distinct. Envelope schemaVersion becomes 2.
5. **Parameter contract.** A leading `@` in a parameter, matrix or parameter-set name is accepted and
   normalized. Unsized `nvarchar`/`varchar` bind as size 4000 when the value fits, `max` otherwise
   (Dapper's convention). Reports show the bound type with its size.
6. **Specific rejections.** Parameter errors keep their message in every path, including `validate` and
   dry runs. `unsupported_statement` names the statement kind and start line.
7. **Dry run replaces `validate`.** `query`, `measure` and `compare` accept `dryRun: true`: the same
   arguments (setup, matrix, parameter sets) are classified offline and nothing connects. The
   `sqlharness_validate` tool is removed. Usage is observed through the journal before further changes.
8. **Control flow.** `IF … ELSE` and `BEGIN … END` are classified by recursing into every branch under the
   unchanged statement rules. `WHILE`, `GOTO`, `RETURN`, `TRY/CATCH`, `WAITFOR` stay unsupported.
9. **Plan evidence.** The plan artifact section uses a bounded reader for saved JSON, without changing
   serialization-only model converters or reading raw XML. Default selection is explicitly last-measured;
   another available repetition can be selected. Saved-shape stability is separate from timing stability.
   Flattened node paths and budget-aware continuation recover all saved nodes; operators also paginate.
   Predicates follow the current explicit includeText policy.
10. **Matrix values and temp keys.** Matrix cells carry `parameterValue` as documented. Temp table keys are
    normalized to `#name` in metrics and the journal.
11. **Workflow cost and provenance.** New operations have opaque operationRef; manifests retain their
    producer reference, including matrix cells. Artifact reads link by exact identity, not path inference.
    Byte output cost includes linked reads and dry runs. Tokens are explicitly estimates; input/discovery/
    chat costs are marked unmeasured. The named single-measured-execution-output reference excludes XML
    and repeats; it is an output-volume comparison, never proof of actual avoided agent tokens.
    Legacy accounting and unavailable provenance remain explicit.
12. **Tool surface.** `sqlharness_inspect` is removed from MCP entirely, including `ping`: every connection
    already verifies `DB_NAME()` against the resolved target (exit 4) and every result reports it. The SQL of
    the Query Store and index probes ships as versioned recipe files that `query` runs from an input root.
    The CLI keeps its commands; removing them is a separate decision.
13. **No busy gate.** Database tool calls run concurrently. Benchmarks are not serialized; measurement
    interference from other sessions or people is documented, as it always existed.
14. **One parameter-set contract.** `measure` and `compare` accept the same inputs: inline `parameterSets`,
    `parameterSetFiles`, and `matrix` as shorthand for one-parameter sets. Execution semantics stay per
    tool: `measure` rotates sets on one connection, `compare` opens one connection per set.

15. **Information ownership and capabilities.** Skills hold operator policy and evidence standards;
    tools/list owns input syntax. MCP capabilities exposes only frozen runtime mode, configured engines,
    allowed profiles, input roots/paging and enforced process ceilings by default. Recipe metadata is
    available on includeDetails=true; diagnostics stays opt-in. Remove duplicated tool names, static
    argument limits and tutorial/protocol boilerplate. configurationRef identifies one frozen context
    without revealing configuration contents. Reuse known metadata on the same connection/process;
    reconnect/restart or uncertain identity invalidates it. Capability discovery and a separate ping
    are not mandatory steps before every task. Task scope is always explicit and freshly derived.
## Out of scope

CLI command removal; PostgreSQL control flow; journal retention; dashboard visual redesign; any change to
the safety rules for individual statements.

## Plans

| Plan | Decisions |
|---|---|
| [055](../../../plans/055-agent-output-budget-and-envelope.md) | 1–4 |
| [056](../../../plans/056-parameter-contract-and-dry-run.md) | 5–7 |
| [057](../../../plans/057-tsql-control-flow-classification.md) | 8 |
| [058](../../../plans/058-benchmark-plan-evidence.md) | 9–10 |
| [059](../../../plans/059-journal-workflow-cost.md) | 11 |
| [060](../../../plans/060-mcp-tool-surface.md) | 7 (tool removal), 12–13, 15 |
| [061](../../../plans/061-unified-parameter-sets.md) | 14 |

Order: 055 → 056 → 057 → 058 → 059 → 060 → 061. 060 removes `validate` only after 056 ships dry runs.

## Accepted retrospective refinements

- 055: inventory-first compare cases; honest CLR valueType labels and precise clipped-cell metadata.
- 058: bounded stored-plan reader, explicit repetition evidence and complete node/operator continuation.
- 059: producer identity, measured-byte coverage and optional named output-volume comparison.
- The report draft now proposes one atomic idempotent put plus optional get, with typed evidence selectors.
  This is a draft revision, not implementation authorization.
- Additional catalog recipes remain unaccepted. The accepted information-ownership refinement is
  decision 15, implemented as a plan update in 060; runtime code is still not implemented.
