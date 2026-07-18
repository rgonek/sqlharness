# SQLHarness agent contract

SQLHarness is a repeatable SQL Server optimization harness for coding agents. It provides bounded query execution, measurements, baseline/candidate comparison with equivalence checks, compact plan distillation, schema inspection, and gain reporting.

## Start safely

```powershell
Get-Command sqlharness
sqlharness --help
```

Use `--json` first. For a database request, lock one profile and one variable set per invocation; a different profile or variables needs a new explicit user request. Prefer closed named profiles to direct targets.

```powershell
sqlharness schema prod-eu --var tenant=acme --var env=uat --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness measure prod-eu --var tenant=acme --var env=uat --query .\queries\orders.sql --repeat 5 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\queries\before.sql --candidate .\queries\after.sql --repeat 5 --json
```

Use parameters instead of SQL interpolation. `query` accepts exactly one SQL source: `--file` or redirected stdin. `schema` is read-only catalog inspection. `plan` is offline, needs no target or scope lock, and accepts a showplan XML file or stdin:

```powershell
sqlharness plan .\artifacts\orders.sqlplan --json
sqlharness gain --json
```

## Safety contract

- Read-only is the default. A persistent mutation requires fresh, single-use user approval for the exact batch and resolved database, then both `--allow-mutation` and `--confirm-database <exact-resolved-name>`.
- Never work around a safety rejection. Report it and ask for an explicit, narrower request or approval.
- `--unsafe-direct` bypasses closed profiles. Use it only with an explicit request and complete `--server`, `--database`, and `--auth`; do not combine it with a profile or `--var`.
- Treat `.sqlplan` and comparison artifacts as locally sensitive. Secrets, passwords, and tokens stay only in process memory.
- Exit codes: `0` success; `2` validation/safety; `3` authentication; `4` target mismatch; `5` SQL execution; `6` local storage.
