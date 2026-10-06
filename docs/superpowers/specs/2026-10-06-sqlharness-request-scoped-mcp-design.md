# SQLHarness MCP: request-scoped SQL Server design

Status: proposed; implementation has not started. The general global MCP is available in Codex CLI and Claude Code. A closed profile and its variables come from each explicitly authorized task, not startup configuration. Public implementation, examples, tests and documentation contain no organizational identifiers or personal deployment data.

## Behavior

Keep existing `sqlharness mcp serve <profile> --var ...` unchanged. Add an explicit alternative:

```powershell
sqlharness mcp serve --request-scope --allow-profile sample-country --allow-profile sample-shared --input-root <absolute-local-input-directory>
```

The positional profile becomes optional only for this mode. Exactly one startup mode is required; `--request-scope` cannot combine with a positional profile or startup `--var`. A nonempty repeated `--allow-profile` allowlist is mandatory in request mode and invalid in fixed mode. Reject duplicate/missing profiles and any allowed profile whose resolved engine is not SQL Server (omitted engine means SQL Server). Do not add PostgreSQL request mode; preserve existing fixed-mode behavior without changing its engine contracts.

One process snapshots the allowlisted profile definitions, file roots, and budgets at startup without database access or interactive login. Profile-file edits cannot redirect later calls; restart is required to change definitions or the allowlist. SQLHarness stays company-independent: organization-specific scope and database naming rules belong to private profiles and instructions, never Core or public fixtures.

## Per-request scope

Exactly 11 existing tools, no `select_target`, mutable current database, persisted last scope, database discovery, or scope tokens. Add a nested argument:

```json
{"scope":{"profile":"sample-country","vars":{"tenant":"example","env":"test"}}}
```

Required in request mode on `inspect`, `validate`, `query`, `measure`, `compare`, `watch`, `snapshot`, and `artifact`. Those same tools in fixed mode retain their existing schema and reject the new argument. Generate separate catalogs for the two modes so required fields match actual behavior. Preserve tool names and number.

`capabilities`, `plan`, and `gain` remain target-free. Request-mode capabilities report `scopeMode: request`, `engine: sqlserver`, permitted profile names (explicit operator allowlist only), and unchanged limits; fixed mode reports `scopeMode: fixed` additively. Do not enumerate unrelated profiles, resolved database lists, variable values, file paths, or credentials. Plan continues its offline sanitized behavior without acquiring a database scope.

Resolve a request once through the existing TargetResolver against the immutable startup snapshot. Reject unknown/nonallowlisted profile, missing/extra/invalid vars, duplicate keys including casing, unknown nested fields, raw server/database/auth/engine or mutation flags. Build a fresh immutable McpScope and scoped Core module for that call; no singleton handler field may retain its scope. Execution uses this same scope from classification through connection, results, artifacts, and sidecars.

Existing result target evidence is retained. Offline validation remains static visible-effects only and never proves live identity or permissions. Client instructions confirm expected profile and target before SQL and requires a new explicit user request for a different task scope; MCP scope syntax does not itself prove user authorization.

## Lifecycle and ownership

One process-wide database gate across all scopes, not a gate per scope. Concurrent second database operation returns the existing `busy` outcome. Matrix and parameter sets are one gated request with one immutable target; their parameters cannot change profile vars. Time budgets, cancellation, EOF shutdown, progress and safe stderr retain current behavior. Local artifact reads may run concurrently but must use their own request scope.

Stamp and verify the existing owner tuple `{profile, canonical vars, engine, server, database}` using each call's scope. Foreign/legacy artifact and snapshot reads still fail closed with the existing content-free errors; no ownership migration, metadata edits, raw SQL/plan/snapshot exposure, persistent mutations, or snapshot overwrite. Artifact ID alone never grants access. CLI owner behavior is unchanged.

Roots remain process-global local filesystem boundaries, not per-target isolation or authorization. Initially use one dedicated local input folder; do not allow the home/workspace/filesystem root. Store ticket SQL outside application repos.

## Public packaging and private deployment

Public examples register a neutral server name such as `sqlharness`, using an operator-provided absolute executable path, `--request-scope`, explicit permitted profiles, and a dedicated local input directory. No organization-specific server name, epic, country, service, endpoint, database, personal path, or profile is part of the public implementation or test fixtures.

Clients launch independent stdio processes at session startup, including after Windows restart. No Windows service, scheduled task, standalone autostart, HTTP bridge, or new dependencies. Actual client registration, private skills and live database checks are separate local deployment work; those inputs and outputs never enter the public repo, issue, PR, or commit text.

## Acceptance

Offline synthetic scopes A and B prove separate target resolution, ownership separation, cancellation and no leaked state. Public live integration tests, if needed, use explicitly authorized disposable local SQL Server fixtures supplied through environment configuration, not company profiles. Public tests never read operator production/UAT profiles. Fresh-client startup verification and real organizational database checks are private deployment evidence; a stdio driver alone is not actual-client proof.