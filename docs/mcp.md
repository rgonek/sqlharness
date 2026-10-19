# SQLHarness MCP server (local stdio)

One `sqlharness` process can serve the SQLHarness diagnostics and benchmarks to an MCP client over local stdio. The server is part of the same single-file distribution, calls into `SqlHarness.Core` through the same module the CLI uses, and enforces the same safety rules. Only local stdio is supported, one client per process. There is no HTTP/SSE transport, no ports, no OAuth, no sampling, prompts, elicitation, or experimental tasks.

Read the section needed for the change or configuration task.

## Start

Read [start](mcp/startup.md#start).

## Benchmark artifact storage

Read [benchmark artifact storage](mcp/artifacts.md#benchmark-artifact-storage).

## Client configuration

Read [client configuration](mcp/startup.md#client-configuration).

## Protect local agent configuration

Read [protect local agent configuration](mcp/startup.md#protect-local-agent-configuration).

## Scope lifetime

Read [scope lifetime](mcp/startup.md#scope-lifetime).

## Input roots and file inputs

Read [input roots and file inputs](mcp/inputs.md#input-roots-and-file-inputs).

## Client workspace roots

Read [client workspace roots](mcp/inputs.md#client-workspace-roots).

## Tools

Read [tools](mcp/tools.md#tools).

## Benchmark evidence and local journal

`measure` and `compare` share per-case sessions: setup is bound to that case, cases run sequentially, the first failure stops the operation, and completed cases remain reportable. `measure` accepts optional `subject` and `scope` fields for observations that belong in Optimization, regardless of `compare.requireContext`; a measure without a subject has no Optimization version identity. MCP descriptions and schemas remain authoritative for tool inputs and output; the [README benchmark and journal sections](../README.md#parameter-sets) explain local conditions, keyed fingerprints, journal-only table IO totals, and SQL Server dashboard reproduction scripts. The [Optimization section](../README.md#dashboard) describes measure/compare grouping, pair labels, defaults and equivalence. Schema v16's keyed measurement fingerprints group observations by execution conditions while excluding comparison-only settings; older journal rows fall back to their existing conditions fingerprint. Schema v16 also stores operation warnings for session safety notices. With operation annotations enabled, eligible journaled calls return a budget-reserved `operationRef` and store their optional call-time label and keep state. Schema v21 stores these independently from owner annotations. Journal retention and `storeSensitive` behavior are documented in [Activity journal](../README.md#activity-journal).

SQL Server read-only tools also accept TVP and list parameters. Source queries (`rowsFrom`/`valuesFrom`) are classified as read-only and materialized once per case before setup; capabilities reports the parameter-count and materialization limits. The [MCP input contract](mcp/inputs.md#input-roots-and-file-inputs) documents the accepted parameter forms, and the [README parameter section](../README.md#parameter-sets) explains binding, empty inputs, journal conditions and mutation rejection. `sqlharness_mutation` rejects these parameter kinds.

## Persistent mutations

Read [persistent mutations](mcp/tools.md#persistent-mutations) for the `sqlharness_mutation` contract, approval outcomes, and capability disclosure.

## Result envelope, errors, and budgets

Read [result envelope, errors, and budgets](mcp/runtime.md#result-envelope-errors-and-budgets).

## Concurrency, deadline, cancellation, and progress

Read [concurrency, deadline, cancellation, and progress](mcp/runtime.md#concurrency-deadline-cancellation-and-progress).

## Artifacts

Read [artifacts](mcp/artifacts.md#artifacts).

## Stdout and stderr

Read [stdout and stderr](mcp/runtime.md#stdout-and-stderr).

## Versions

Read [versions](mcp/startup.md#versions).

## Context cost

Read [context cost](mcp/runtime.md#context-cost).

## Evidence scope

Read [evidence scope](mcp/runtime.md#evidence-scope).
