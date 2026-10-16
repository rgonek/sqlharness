---
name: sqlharness
description: "Use when collecting SQL Server, Azure SQL or PostgreSQL query and benchmark evidence through SQLHarness CLI or MCP."
---

# SQLHarness

## Prepare

Keep the requested profile and variables; request-scoped MCP's allowlist permits profile switches, but changing variables needs a new request. Discover arguments and limits through installed tool schemas or CLI help. Read capabilities when the connection's configuration is unknown; reuse it until reconnect or restart. Capabilities also reports mutation policy; CLI `capabilities <profile> --json` (`engineGuidance`) and MCP tool descriptions carry the engine's setup and safety rules; follow them instead of assuming another engine's.

<!-- docs-sync:example -->
```powershell
sqlharness capabilities <profile> --json
```

Execute once the target and required inputs are resolved, using parameters and query shapes that match the application.

## Read evidence

Start with compact output. Check execution status, errors, omitted cases and clipped cells. Retrieve the pages or artifact sections needed for the question. Plan pages fit the response byte budget, so node counts can vary; follow each continuation to cover the saved plan. Each case supporting a conclusion needs reviewed evidence or an explicit gap.

Report execution status, result equivalence and performance verdict separately. Match equivalence semantics to ordering and duplicates; validate domain meaning with two-direction EXCEPT or duplicate-aware grouped counts when needed. Report inconclusive verdicts as inconclusive.

## Optimization

Before changing SQL, measure the baseline on representative cases: common and rare values, ordering ties, boundary dates and empty results. Keep dependent work sequential, benchmarks free of competing work and shared caches intact.

Compare repeated measurements and variability on matching targets and conditions. Inspect plan details to explain measured results. On a SQL Server missing-index hint, follow `overlap.verdict` from the artifact `plan` section:

- `covered`: do not propose a new index; look at why the plan did not use the existing one.
- `include-gap`: extend the named index's INCLUDE.
- `partial-key`: compare the hint with the named index before proposing a new one.
- `new-shape`: treat a new index as a hypothesis and prove it with `compare` after approved DDL.
- When `overlap` is absent: read `evidenceGaps`, then fall back to a scoped catalog `query`.

Creating or altering an index is a persistent mutation and needs approval. Label every other unmeasured proposal as a hypothesis.

## Finish

Answer the user's question with the target, covered cases, verdict and relevant evidence. Account for every requested case as reviewed or unresolved, naming missing data and limits on the conclusion.

## Boundaries

Stop and report tool unavailability, authentication/access failure, target mismatch or SQL safety rejection. Read error details; retry only a clear SQL/argument error corrected within the same read-only scope. Ambiguous rejections, timeouts and cancellations require a report before further execution. Keep rejected work stopped rather than switching transports or creating persistent helpers.

For persistent writes, use `mutation` in the CLI or `sqlharness_mutation` in MCP. The operator's dashboard policy decides whether to reject, ask for a click, or run an eligible DML request. If the mutation result is anything other than `success`, stop and report its status; do not retry it or switch transports. Session-local preparation needs no persistent-write approval.

Keep secrets in configured environment variables. Obtain user review before publishing results or artifacts. Leave the operator's dashboard and API alone.
