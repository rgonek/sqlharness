---
name: sqlharness
description: "Use when collecting SQL Server, Azure SQL or PostgreSQL query and benchmark evidence through SQLHarness CLI or MCP."
---

# SQLHarness

Use the installed tools to answer the user's database question. Discover arguments and limits through MCP tool schemas or CLI help; read capabilities only when the connection's configuration is unknown. Reuse that information until reconnect or restart. No repository access is needed.

<!-- docs-sync:example -->
```powershell
sqlharness capabilities <profile> --json
```

## Work within the request

Keep the requested profile and variables. Request-scoped MCP may switch profiles within the operator's allowlist; changing variables still needs a new request. Do not replace an unavailable or rejected tool with another database transport.

Match the application's parameter types and query shape. For optimization, measure the baseline first and choose representative cases: common and rare values, ordering ties, boundary dates and empty results. Keep dependent work sequential and avoid competing benchmarks or clearing shared caches.

## Read evidence before concluding

Start with compact output. Check execution status, errors, omitted cases and clipped cells; retrieve only the missing pages or artifact sections needed for the question. An inventory is not proof that its details were reviewed.

Keep execution success, result equivalence and performance verdict separate. Choose equivalence semantics to match ordering and duplicates. Observed equality does not prove domain correctness; use two-direction EXCEPT or duplicate-aware grouped counts when needed. An inconclusive verdict is not a pass.

Judge improvements from repeated measurements and variability on matching targets and conditions. Use plan details to explain the result; a stable plan or missing-index hint alone does not prove benefit. Report the target, cases, verdict, relevant measurements and evidence gaps. Never present an unmeasured proposal as proven.

## Handle failures and side effects

Stop on tool unavailability, authentication/access failure, target mismatch or SQL safety rejection. Read error details: correct a clear SQL/argument error within the same read-only scope; stop on an ambiguous rejection. Do not automatically repeat timeouts or cancellations, weaken checks or create persistent helpers to bypass a rejection.

Persistent writes need fresh approval for the exact batch and resolved database; an attempt consumes approval. Use the installed interface's approval mechanism; MCP does not support explicit persistent mutations. Session-local preparation needs no persistent-write approval.

Keep secrets in configured environment variables. Review local results and artifacts before publishing them. Leave the operator's dashboard and its API alone.
