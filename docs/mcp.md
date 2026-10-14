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

## No persistent mutations

Read [no persistent mutations](mcp/tools.md#no-persistent-mutations).

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
