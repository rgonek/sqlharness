# SQLHarness agent contract

SQLHarness runs bounded SQL Server and PostgreSQL queries for coding agents: execution, measurements, baseline/candidate comparison with result equivalence, plan distillation, schema and storage inspection. [README.md](README.md) is the reference for every command, option and rule; this file is the contract and the map into it.

## Working against a database

1. **Start offline.** `sqlharness capabilities --json`, `sqlharness doctor --json`, then classify the SQL with `sqlharness validate <profile> --file .\query.sql --json` (add `--var`/`--param` the profile and SQL need). Validation never connects; it does not authorize a later mutation.
2. **Lock the scope.** One closed named profile and one `--var` set per invocation. A different profile or different variables needs a new explicit user request. `--unsafe-direct` is for an explicit user request only, with complete `--server`, `--database` and `--auth`, and never with a profile or `--var`.
3. **Inventory before measuring.** Pick representative cases offline: row count, cardinality distribution, ordering ties, missing history, boundary dates, empty results.
4. **Run one batch per invocation.** Pass values as typed `--param` (types in [README Quick start](README.md#quick-start)). Put preparation in `--setup`, which runs once on the measured session. Any ScriptDom-parsable construct inside a top-level `SELECT` is accepted, so SQLHarness handles unfamiliar nested syntax; `sqlcmd` is not a fallback.
5. **Read compact output.** `--json-summary` on `measure`/`compare` gives a bounded projection; `--json` gives the full report.
6. **Judge equivalence as technical.** `compare --compare-results` checks observed rows, not domain meaning. For a domain check use two-direction `EXCEPT`, or grouped counts when duplicates matter.

```powershell
sqlharness validate prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --json
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --json
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\before.sql --candidate .\after.sql --repeat 5 --json-summary
```

## Safety contract

- **Read-only is the default.** A persistent mutation needs fresh, single-use user approval for the exact batch and the resolved database, then `--allow-mutation --confirm-database <exact-resolved-name>`. Session-local work (SQL Server `#temp` and table variables, PostgreSQL `TEMP`) follows the [setup contract](README.md#benchmark-setup-contract) and needs no approval.
- **A safety rejection is a stop.** Report it and ask the user for a narrower request or an approval. Persistent helper objects are never the way around a rejected setup.
- **Secrets live in environment variables** named by the profile, and in process memory only.
- **Local outputs are locally sensitive:** snapshots under `~/.sqlharness/snapshots`, compare/measure artifacts, `.sqlplan` files, `qstop`'s `queries.jsonl`, `--param-set` files and `~/.sqlharness/data/activity.db`. Commit, attach or publish them only after the user reviewed them.
- **The dashboard** (`sqlharness dashboard`) is the operator's tool; agents leave it and its API alone.

Exit codes: `0` success · `2` validation/safety · `3` authentication · `4` target mismatch · `5` SQL execution · `6` local storage · `7` `watch` reached `--max-duration` · `8` `snapshot --diff` found differences.

## Reference map

| Read this | When |
|---|---|
| `sqlharness <command> --help`, [README Commands](README.md#commands) | Choosing options and defaults (`counts`, `space`, `schema`, `watch`, `snapshot`, `qstop`, `indexes`, `plan`) |
| [Benchmark setup contract](README.md#benchmark-setup-contract) | Writing `--setup`, temp tables, table variables or `SET @v` |
| [PostgreSQL engine notes](README.md#postgresql-engine-notes), [temp target proof](README.md#postgresql-temp-target-proof) | The profile has `"engine": "postgres"` |
| [Measure parameter sets](README.md#measure-parameter-sets) | `measure --param-set` or `compare --matrix` |
| [Result equivalence and compact output](README.md#result-equivalence-and-compact-output) | Choosing `--compare-results`, `--compare-nullability` or reading a summary |
| [Safety contract](README.md#safety-contract) | A rejection, a mutation request, redaction or artifact handling |
| [docs/mcp.md](docs/mcp.md) | Running or calling `sqlharness mcp serve` (fixed or request scope; no persistent mutations) |
| [Activity journal](README.md#activity-journal-and-savings) | Questions about what is recorded locally |

## Changing SQLHarness

- **Protect what the caller cannot see.** The security boundary is the resolved credential and the persistent-mutation classifier. SQL text, parameters, the caller's own plan files, result data and server/database names reach the caller anyway (they supplied them, or `query` reads them), so they stay readable; bound their size with budgets instead of masking them.
- **Done means both gates green:** `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1` (WSL ext4, provision once with `pwsh ./scripts/setup-linux-gate.ps1`). Run one gate at a time.
- **Plans** live in `plans/`; `plans/README.md` is the index and execution queue. Update a plan's row when its work lands.
- **Docs follow behaviour.** A behaviour change updates README.md (reference), `skills/sqlharness/SKILL.md` (agent digest) and `Capabilities.cs`; this file changes only when the contract or the map changes.
