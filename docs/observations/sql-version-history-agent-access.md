# Observation: agent access to retained SQL version identities

Recorded 2026-11-03 against repository HEAD `85d1c95e`.
Status: collecting observations, not approved for implementation.
System label: unmapped.

## Observed workflow

A user asked which Optimization versions represented the original query and the adopted change.
The session had repeated comparisons, a rejected candidate, an adopted candidate and a later
parameter-only revision. Compare responses supplied operation references and benchmark evidence,
but did not return the dashboard's retained version labels or a version-history read interface.
The exposed MCP catalog contained no history reader.

The agent inspected CLI help and the local journal. Two bounded read-only journal invocations
established schema and ordered hash occurrences, but the agent then incorrectly inferred a
one-based version sequence. During the retrospective, two more bounded journal invocations
located the stored labels and corrected that delivered claim: the sequence started at `v0`.
The earlier metric values were not corrected. Direct journal access was an observed workaround,
not a recommended interface or an approved solution.

## Verified behavior and limits

- [README](../../README.md#quick-start) documents zero-based labels and retained-history rules.
  The mistaken one-based interpretation was an agent slip, not a contract defect.
- [CompareHistoryBuilder](../../src/SqlHarness.Dashboard/CompareHistoryBuilder.cs), inspected
  at lines 35-100, assigns labels from hashes within an explicit context, subject and scope,
  observing baseline before candidate and reusing stored labels when supplied.
- [JournalSchema](../../src/SqlHarness.Core/Journal/JournalSchema.cs) defines
  `compare_version_labels`; a bounded read of actual retained rows supplied the correction.
  Neither a source role such as "original" nor an operation's position alone proves its label.
- [McpToolCatalog](../../src/SqlHarness.Mcp/Tools/McpToolCatalog.cs) and the exposed runtime catalog
  were inspected. The observed compare/artifact results did not provide this history mapping.
  The absence of a history reader is a UX/agent-path gap, not a failed SQL execution.
- The installed CLI reported a development version. That does not identify the loaded MCP or
  dashboard build. Current HEAD and persisted labels were checked; the dashboard UI was not
  re-verified during this retrospective. No live SQL was rerun and no token file was read.

Confidence: high for the delivered correction and exposed-interface gap; the best permanent
interface and its token benefit remain hypotheses.

## Existing coverage

[Plan 123](../../plans/123-shared-version-table-across-conditions.md) owns shared dashboard version
identity and approximate cross-condition comparisons. [Plan 089](../../plans/089-agent-session-operation-pagination.md)
owns dashboard operation reachability. Their current repository records say locally integrated;
this does not establish the installed producer identity or an MCP history interface.
[Plan 109](../../plans/109-artifact-review-workflow-study.md) owns broader evidence-workflow cost.
No new plan, implementation, contract change, installation or publication is selected here.

## Questions for later synthesis

Should a comparison return its retained version identities, or should an agent have a bounded
history read that resolves an operation reference to the same context used by the dashboard?
How should semantic roles supplied by an agent remain distinct from content-based version labels?
Could existing history logic be reused without a second numbering algorithm?

The cheapest useful later check is an offline synthetic fixture with generic query hashes,
reused hashes, different contexts, failed attempts, retained labels and paginated history.
Compare the exposed identities with the dashboard's shared implementation. This is an open
question and verification direction, not an implementation checklist. No savings are claimed.
