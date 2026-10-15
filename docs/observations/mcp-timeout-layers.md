# Observation: independent MCP timeout layers

Status: collecting observations; potential documentation improvement, not an approved
implementation or contract change. Recorded 2026-10-15 against repository HEAD `e709937`.

## Observed workflow

A comparison first returned a SQL execution timeout in a SQLHarness error envelope.
An explicitly authorized retry raised the SQL timeout but then ended with a client
error: `timed out awaiting tools/call after 300s`. The latter contained no SQLHarness
result envelope, completed metrics or artifact references.

The operator subsequently changed the client's configured tool timeout and restarted
the client. Later comparisons succeeded, but finished within the earlier client limit.
That success does not establish that changing the timeout caused the recovery. The
cause of the earlier long execution and its server-side outcome were not established.

## Current evidence and interpretation

- SQL command timeout: `src/SqlHarness.Core/SqlExecution.cs:347` assigns
  `SqlCommand.CommandTimeout` from the request. The request contract permits 1..300 seconds.
- Operation budget: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:436` schedules
  cancellation of the whole call. It is independent of individual SQL commands.
- Client timeout: the client can stop awaiting `tools/call` before receiving a server
  response. The observed client error alone does not establish whether the server
  cancelled, completed or was still running.
- Existing [runtime documentation](../mcp/runtime.md#concurrency-deadline-cancellation-and-progress)
  describes server deadlines, cancellation and conditional progress notifications.
  Lack of visible progress in this session does not prove the protocol lacks progress.
- A fresh CLI process reading the new client configuration confirms the saved setting,
  not that an existing MCP connection reloaded it. Reload behavior was not verified.
- [Plan 068](../../plans/068-operation-phase-timings-and-diagnostics.md) owns operation
  phase diagnostics and is recorded as integrated on HEAD. Availability in the server
  used for the investigation was not established by this observation.

Classification: documentation UX gap; no demonstrated SQLHarness runtime defect.
Confidence is high in the distinction between observed errors; the timeout cause and
reload behavior remain unverified. No workload measurements or private mappings are retained here.

## Potential addition, pending later synthesis

Consider a short troubleshooting section distinguishing SQL command timeout, server
operation budget and client tool-call timeout. It could show the two observed error
shapes, explain unknown server outcomes after a transport failure, and distinguish a
saved client setting from a verified live reload. Client-specific configuration belongs
in client setup guidance; SQLHarness cannot infer or control an external client's limit.

Before deciding, collect comparable cases from other clients and verify their reload
behavior. A future documentation check should use synthetic examples for all three
limits and avoid attributing a recovery to a configuration change without evidence.
This entry authorizes no implementation, automatic retries, installation or publication.
