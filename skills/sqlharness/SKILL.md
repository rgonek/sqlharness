---
name: sqlharness
description: Use when a coding agent needs safe, repeatable SQL Server or Azure SQL query evidence, performance comparison, execution-plan distillation, or compact schema inspection.
---

# SQLHarness

SQLHarness is a repeatable SQL Server optimization harness: it measures, compares, and proves SQL changes while keeping target resolution and SQL execution guarded.

## Readiness and scope

Before a database operation, confirm the executable is available:

```powershell
Get-Command sqlharness
sqlharness --help
```

Lock one profile + one variable set per invocation; a different profile/vars requires a new explicit user request. Use the named profile and its required variables rather than direct connection details:

```powershell
sqlharness schema prod-eu --var tenant=acme --var env=uat --json
```

Prefer `--json` for agent consumption. Pass SQL through `--file` or stdin exactly as the command requires, and use repeatable `--param name[:type]=value` parameters instead of interpolating values into SQL.

## Safe workflow

1. Inspect with `schema` when object shape is unknown.
2. Use `query` for a bounded read-only result.
3. Use `measure` for repeated timing, IO, and plan evidence for one query.
4. Use `compare` for baseline/candidate evidence and result equivalence.
5. Use `gain --json` to inspect recorded output savings.

```powershell
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --repeat 5 --json
sqlharness gain --json
```

## Mutation gate

The default is read-only. Session-local `#temp` work is classified separately, but persistent-object mutation requires a fresh, single-use user approval for the exact SQL batch and exact resolved database.

Only after that approval, use both flags:

```powershell
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\batches\approved.sql --allow-mutation --confirm-database contoso-acme-uat --json
```

Do not reuse an approval for changed SQL, a different database, a different profile, or different variables. Never work around a safety rejection; report the safe error and request a narrower or explicitly approved operation.

`--unsafe-direct` bypasses the closed-profile guardrail. Use it only when the user explicitly requests direct/ad-hoc access and supplies the complete target and authentication strategy; never combine it with a profile or `--var`.

## Offline plans and schema

`plan` is offline and does not need a scope lock or database access. It reads a showplan XML file (or stdin), returns a compact plan tree, and can emit JSON:

```powershell
sqlharness plan .\artifacts\orders.sqlplan --json
```

.sqlplan artifacts are locally sensitive: they can contain batch text and parameter values. Do not paste or publish them without explicit review.

`schema` uses only internal catalog queries and returns compact tables, views, columns, indexes, and foreign keys:

```powershell
sqlharness schema prod-eu --var tenant=acme --var env=uat --filter "%Order%" --max-objects 50 --json
```

## Outcomes

Exit codes are stable: `0` success, `2` validation or safety rejection, `3` authentication, `4` target mismatch, `5` SQL execution failure, and `6` local storage failure. Secrets, access tokens, and passwords must remain only in process memory; do not put them in arguments, files, logs, reports, or artifacts.
