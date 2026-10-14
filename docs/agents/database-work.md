# Database work

Read before executing database operations from this repository. Use installed capabilities, tool schemas or command help to resolve configuration, arguments and limits.

## Scope and approval

Keep the requested profile and variables. Request-scoped MCP's operator allowlist permits profile switches; different variables still need a new request. Direct connections require an explicit request and complete connection settings.

Persistent mutations require fresh, single-use approval for the exact batch and resolved database. Record CLI approval with `--allow-mutation --confirm-database <exact-resolved-name>`; an attempt consumes it. MCP supports no explicit persistent mutations. Session-local setup needs no persistent-write approval.

## Failures and data

Stop on tool unavailability, authentication/access failure, target mismatch or SQL safety rejection. Read error details: correct only clear SQL/argument errors within the unchanged read-only scope. Ambiguous rejections, timeouts and cancellations require a report before further execution. Keep rejected work stopped rather than switching transports or creating persistent helpers.

Keep secrets in configured environment variables and process memory. Obtain user review before committing, attaching or publishing database outputs, artifacts or parameter files. Leave the operator's dashboard and API alone; use isolated fixtures for development.

## Technical contracts

For execution, discover setup, engine limits, parameter sets, result modes and artifact paging through the installed interface. When changing their implementation, read the relevant maintained contract:

- Setup/session lifetime: [benchmark setup](../../README.md#benchmark-setup-contract).
- PostgreSQL classification: [engine notes](../../README.md#postgresql-engine-notes) and [TEMP proof](../../README.md#postgresql-temp-target-proof).
- Multi-case benchmarks: [parameter sets](../../README.md#parameter-sets).
- Equivalence/output projections: [results](../../README.md#result-equivalence-and-compact-output).
- Safety/redaction: [safety contract](../../README.md#safety-contract).
- Journal persistence/projections: [activity journal](../../README.md#activity-journal-and-workflow-output-cost).
