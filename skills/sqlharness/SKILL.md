---
name: sqlharness
description: "Use when collecting SQL Server, Azure SQL or PostgreSQL query and benchmark evidence through SQLHarness CLI or MCP. Do NOT use for database access through other tools."
---

# SQLHarness

Use SQLHarness to execute bounded SQL, measure queries and compare variants. [README.md](../../README.md)
is the command reference; [AGENTS.md](../../AGENTS.md) defines the repository contract.
Use command help or MCP tool schemas for arguments and defaults: [commands](../../README.md#commands).

## Choose the target and operation

Lock one named profile and its exact variables. Change the target only on an explicit user request;
request-scoped MCP may switch profiles within the operator's allowlist, but different variables still
need a new request. Direct connections require an explicit request and complete connection settings:
[scope rules](../../README.md#safety-contract).

Read capabilities when the engine, runtime scope, allowed profiles, input roots or limits needed for
the task are unknown. Reuse MCP configuration for the connection; refresh after reconnect or restart.
Use doctor to troubleshoot local configuration. Every database execution checks the connected database
against the resolved name before requested SQL; verify the returned target. Server aliases are informational.

Choose the operation by the evidence needed:

| Operation | Use |
|---|---|
| `query` | Bounded data, catalog, storage, existing-index or Query Store SQL |
| `measure` | Repeated timing, IO and plan evidence for one query |
| `compare` | Baseline/candidate measurements and observed result equivalence |
| `watch` | Bounded polling until a condition or stability |
| `snapshot` | Capture results or compare them with a saved capture |
| `plan` | Distill a saved execution plan offline |
| `artifact` | Retrieve missing sections of saved benchmark evidence |

Bind values as typed parameters, matching the application's types, sizes and NULL semantics; do not
interpolate values into SQL. Use two-part persistent object names. Execute directly; dry run is optional
for checking an exact invocation without connecting and does not prove execution cost, permissions or
mutation approval.

<!-- docs-sync:example -->
```powershell
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --output agent
sqlharness compare prod-eu --var tenant=acme --var env=uat --subject orders-report --scope whole --baseline .\before.sql --candidate .\after.sql --param customerId:int=42 --repeat 5 --compare-results multiset --json-summary
```

## Read enough evidence to support the conclusion

Start with compact output: CLI `--output agent`, or `--json-summary` for benchmarks; MCP returns the
compact envelope. Use full JSON or artifact sections only when needed. Inspect status, errors, omitted
rows/cases and clipped cells before reporting values. Aggregate or page SQL results when rows are missing;
follow artifact continuation to obtain the required case and plan details. Inventory alone is not evidence
that every case or plan was reviewed. Missing metrics and plans remain explicit gaps.

For optimization, measure the requested workload first and choose representative cases before making
broader claims: cardinalities, distributions, ordering ties, missing history, boundary dates and empty
results. Keep dependent work sequential and benchmarks free of competing work. Do not clear shared caches.
Report repeated elapsed time, CPU and IO where available, variability and relevant plan findings.

Use setup only for preparation shared by measured executions. Match application input; replacing a TVP
with a CTE or temporary table measures that query shape, not the application's TVP plan. Setup/session
rules: [benchmark setup](../../README.md#benchmark-setup-contract);
multiple cases: [parameter sets](../../README.md#parameter-sets).

Choose ordered, multiset or set comparison to match the question; use off only when equivalence is not
being checked. Observed equality is technical evidence, not full-domain equivalence. Use two-direction
EXCEPT or duplicate-aware grouped counts for a domain check. Nullability is a separate check unless
explicitly included. Read regression verdict and reasons: inconclusive is not a pass, and the verdict
does not change the exit code.

Plan shape stability does not imply stable timing or row counts. Review actual/estimated rows, spills,
grants and warnings relevant to the proposed change. A missing plan for a selected repetition remains
missing evidence; a capped operator summary does not cover the full plan. For missing-index proposals,
read the measured plan hint and inspect the table's existing indexes; a hint alone does not prove benefit.

## Handle errors and changes

Stop and report tool unavailability, authentication/access failure, target mismatch or safety rejection.
Judge the detailed message, not exitCode or error.code alone: argument validation can share a safety
code. Correct only a clear input error; stop when the rejection is ambiguous.
Do not switch targets/transports, weaken checks or create persistent helpers to bypass a rejection.
Correct ordinary SQL or argument errors within the unchanged authorized read-only scope and retry when
the correction addresses the cause. Do not automatically repeat timeouts or cancelled benchmarks; report
completed cases and the limit reached.

A persistent mutation needs fresh, single-use approval for the exact batch and resolved database,
recorded with `--allow-mutation --confirm-database <exact-resolved-name>`. An execution attempt consumes
approval. Approval never overrides classification. MCP performs no explicit persistent mutations.
Accepted session-local setup needs no persistent-mutation approval. Local artifacts, parameter files,
snapshots and plans are sensitive; publish only after review. Keep secrets in the environment variables
named by the profile and process memory. Leave the dashboard and its API to the operator.

## Engine-specific work

For remote PostgreSQL profiles use `sslMode: verify-full`. Setup must be reviewed because its user
functions may have hidden effects; use a least-privilege principal. PostgreSQL IO and CPU evidence differ
from SQL Server: [engine notes](../../README.md#postgresql-engine-notes). For MCP startup, file roots and
request scope see [the MCP contract](../../docs/mcp.md).

Completion: report the exact target, observed cases, outcome, equivalence mode, measurements and reviewed
plan evidence, with omissions and limitations. Never present an unmeasured proposal as a proven gain.
