# SQLHarness agent contract

SQLHarness runs bounded SQL Server and PostgreSQL queries for coding agents: execution, measurements, baseline/candidate comparison with result equivalence, plan distillation, polling and snapshots. [README.md](README.md) is the reference for every command, option and rule; this file is the contract and the map into it.

## Working against a database

1. **Resolve unknown configuration.** Use `sqlharness capabilities <profile> --json` when the engine or needed limits are unknown; `sqlharness doctor --json` diagnoses local configuration. Execute directly; `--dry-run` is an optional offline check, not execution proof or mutation approval.
2. **Lock the scope.** One closed named profile and one `--var` set per invocation. A different profile or different variables needs a new explicit user request (in request-scope MCP, the operator's `--allow-profile` list approves profile switches; different variables still need the user). `--unsafe-direct` is for an explicit user request only, with complete `--server`, `--database` and `--auth`, and never with a profile or `--var`.
3. **Inventory before measuring.** Pick representative cases offline: row count, cardinality distribution, ordering ties, missing history, boundary dates, empty results.
4. **Run one batch per invocation.** Pass values as typed `--param` (types in [README Quick start](README.md#quick-start)). For `measure` and `compare`, put preparation in `--setup`, which runs once on the measured session. Any ScriptDom-parsable construct inside a top-level `SELECT` is accepted, so SQLHarness handles unfamiliar nested syntax; `sqlcmd` is not a fallback.
5. **Read compact output.** Start with `--output agent`, or `--json-summary` on `measure`/`compare`. Check errors, omissions and clipped cells; retrieve required detail before drawing conclusions.
6. **Judge equivalence as technical.** `compare --compare-results` checks observed rows, not domain meaning. For a domain check use two-direction `EXCEPT`, or grouped counts when duplicates matter.

```powershell
sqlharness query prod-eu --var tenant=acme --var env=uat --file .\queries\orders.sql --param customerId:int=42 --output agent
sqlharness compare prod-eu --var tenant=acme --var env=uat --baseline .\before.sql --candidate .\after.sql --repeat 5 --json-summary
```

## Safety contract

- **Read-only is the default.** A persistent mutation needs fresh, single-use user approval for the exact batch and the resolved database; then record that approval with `--allow-mutation --confirm-database <exact-resolved-name>`. Session-local work (SQL Server `#temp` and table variables, PostgreSQL `TEMP`) follows the [setup contract](README.md#benchmark-setup-contract) and needs no approval.
- **A safety rejection is a stop.** Report it and ask the user for a narrower request or an approval. Persistent helper objects are never the way around a rejected setup.
- **Correct errors within scope.** Stop on tool unavailability, authentication/access failure or target mismatch. Ordinary SQL/argument errors may be corrected and retried within the unchanged read-only scope when the cause is clear. Do not automatically repeat timeouts or cancellations. A persistent-mutation attempt consumes its approval.
- **Read the error details.** Validation and safety can share an exit code and error code. Correct only a clear input error; an explicit SQL safety rejection or an ambiguous rejection is a stop.
- **Secrets live in environment variables** named by the profile, and in process memory only.
- **Local outputs are locally sensitive:** snapshots under `~/.sqlharness/snapshots`, compare/measure artifacts, `.sqlplan` files, `--param-set` files and `~/.sqlharness/data/activity.db`. Commit, attach or publish them only after the user reviewed them; the SQLHarness home itself is created with mode 0700 on Unix, and `doctor` warns when an existing home is broader.
- **The dashboard** (`sqlharness dashboard`) is the operator's tool; agents leave it and its API alone.

Exit codes: `-1` (255 on POSIX): unhandled CLI error in text mode, including command-line parse errors; other output modes, `mcp` and `dashboard` return `2` · `0` success · `2` validation/safety · `3` authentication · `4` connected database differs from the resolved database (exact ordinal comparison) · `5` SQL execution · `6` local storage · `7` `watch` reached `--max-duration` · `8` `snapshot --diff` found differences. Connected server names/addresses are reported for context and are not compared with the profile host; TLS certificate validation authenticates the endpoint (`trustServerCertificate: false` for SQL Server, `sslMode: verify-full` for PostgreSQL).

## Reference map

| Read this | When |
|---|---|
| `sqlharness <command> --help`, [README Commands](README.md#commands) | Choosing options and defaults (`watch`, `snapshot`, `plan`, `artifact`, `--dry-run`, `--output agent`) |
| [Benchmark setup contract](README.md#benchmark-setup-contract) | Writing `--setup`, temp tables, table variables or `SET @v` |
| [PostgreSQL engine notes](README.md#postgresql-engine-notes), [temp target proof](README.md#postgresql-temp-target-proof) | The profile has `"engine": "postgres"` |
| [Parameter sets](README.md#parameter-sets) | `measure --param-set`, CLI `compare --matrix`, or MCP `parameterSets`, `parameterSetFiles`, and `matrix` |
| [Result equivalence and compact output](README.md#result-equivalence-and-compact-output) | Choosing `--compare-results`, `--compare-nullability` or reading a summary |
| [Safety contract](README.md#safety-contract) | A rejection, a mutation request, redaction or artifact handling |
| [docs/mcp.md](docs/mcp.md) | Running or calling `sqlharness mcp serve` (fixed or request scope; no explicit persistent mutations) |
| [Activity journal](README.md#activity-journal-and-workflow-output-cost) | Questions about what is recorded locally |

## Changing SQLHarness

- **Protect what the caller cannot see.** The security boundary is the resolved credential and the persistent-mutation classifier. SQL text, parameters, the caller's own plan files, result data and server/database names reach the caller anyway (they supplied them, or `query` reads them), so they stay readable; bound their size with budgets instead of masking them.
- **Done means both gates green:** `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1` (WSL ext4, provision once with `pwsh ./scripts/setup-linux-gate.ps1`). Run one gate at a time.
- **Plans** live in `plans/`; `plans/README.md` is the index and execution queue. Update a plan's row when its work lands.
- **Docs follow behaviour.** Update README.md (reference), tool descriptions/schemas and capabilities where affected. Update `skills/sqlharness/SKILL.md` only when the user workflow changes; this file changes only when the contract or the map changes.

## User-facing skills and token budget

`skills/sqlharness/SKILL.md` is a lean workflow template for ordinary users and custom use-case skills. Assume the consuming agent has no repository access and needs to do its job, not learn SQLHarness internals.

- Keep the skill focused on workflow, task-specific decisions and interpreting evidence. Do not repeat tool catalogs, argument descriptions, defaults, limits or technical contracts available through tool descriptions/schemas or capabilities.
- Put tool-specific guidance in tool descriptions/schemas and runtime policy in capabilities. Keep each surface concise and avoid duplication; minimize the total tokens needed for the user's task.
- Essential guidance must be available through the installed skill, command help or tool metadata/capabilities. Repository-relative README, AGENTS and docs links must not be required to use the skill.
- Apply these rules to the bundled skill, derived custom skills and future documentation changes.
