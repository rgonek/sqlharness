# MCP startup

## Start

The server supports a fixed-profile mode and a SQL Server-only request-scoped mode. Each process serves one local stdio client.

Fixed-profile mode keeps the existing command shape:

```powershell
sqlharness mcp serve <profile> --var key=value [--input-root <absolute-directory>]
```

- `<profile>` is a closed target profile from the operator's profile store, with fixed `--var key=value` pairs resolved once at startup.
- `--input-root` is repeatable and takes an absolute directory that allows file inputs. By default no input roots are configured, which means no file inputs are admitted at all.
- `--max-result-bytes` sets the process response ceiling in bytes: default 262144, accepted range 4096..1048576. Each call defaults to a 65536-byte budget and may raise or lower that budget up to the process ceiling.
- `--max-operation-seconds` sets the process database time budget in seconds: default 900, accepted range 1..86400. A single tool call may only lower it.
- `--unsafe-direct` is blocked for `mcp serve` and rejected with exit 2. Direct server/database targets are not available on this path.

Request-scoped mode selects one of the explicitly allowed SQL Server profiles on each target-dependent call:

```powershell
sqlharness mcp serve --request-scope --allow-profile sample-country --allow-profile sample-shared --input-root C:\work\sqlharness-inputs
```

The nonempty `--allow-profile` list is required and repeatable. Do not provide a startup profile or `--var` in this mode. Startup rejects duplicate profile names and any allowlisted profile that resolves to Postgres. The process snapshots the allowed profile definitions, file roots, and budgets once; editing the profile store or changing the allowlist requires a restart. Startup does not connect to a database or perform interactive login. Request-scoped mode is not available for Postgres.

## Client configuration

SQLHarness never installs or edits a client configuration automatically. To register the server, paste an entry manually into the MCP client configuration file. Neutral shape (field names vary by client):

Fixed-profile client entry:

```json
{
  "command": "sqlharness",
  "args": ["mcp", "serve", "sample-country", "--var", "tenant=example", "--var", "env=test", "--input-root", "C:\\work\\sqlharness-inputs"],
  "env": {}
}
```

Request-scoped client entry:

```json
{
  "command": "C:\\tools\\sqlharness.exe",
  "args": ["mcp", "serve", "--request-scope", "--allow-profile", "sample-country", "--allow-profile", "sample-shared", "--input-root", "C:\\work\\sqlharness-inputs"],
  "env": {}
}
```

## Protect local agent configuration

`~/.sqlharness/dashboard.json` and `~/.sqlharness/dashboard-token` contain a live dashboard access token. Do not print, paste, or expose those files. Avoid broad reads such as `cat ~/.sqlharness/*.json`; name `targets.json` explicitly when you need to inspect profiles. On Windows, the corresponding dashboard path is `%USERPROFILE%\.sqlharness\dashboard.json` (and `dashboard-token` beside it).

The sample hooks in [`scripts/hooks`](../../scripts/hooks) block common Claude Code and Codex shell reads of `dashboard.json` and `~/.sqlharness/*.json`, while allowing an explicit `targets.json` read. They are optional guardrails for shell commands, not a filesystem access boundary. Review and merge the relevant example into your existing client hook configuration; do not replace other settings. Claude Code hooks use the repo-local script path in `claude-settings.example.json`. For Codex, copy `protect_sqlharness_config.py` to `~/.codex/hooks/` and merge `codex-hooks.example.json` into `~/.codex/hooks.json`; review and trust the hook when Codex prompts.

Connection secrets stay in the process environment and the operator's profile store. In fixed mode, tool arguments never carry a profile or vars. In request-scoped mode, profile and vars may appear only inside the required nested `target`. In both modes, tool arguments never carry raw server or database, auth, engine, `unsafeDirect`, `allowMutation`, `confirmDatabase`, or password fields, and unknown argument keys are rejected before execution.

## Scope lifetime

In fixed mode, the operator supplies the profile and vars once at process start. The server resolves and freezes one immutable scope for all calls; a different profile, variables, or target requires a restart.

In request-scoped mode, each target-dependent call supplies a nested `target`, for example `{"target":{"profile":"sample-country","vars":{"tenant":"example","env":"test"}}}`. The target is required for `query`, `mutation`, `measure`, `compare`, `watch`, `snapshot`, and `artifact`. Each call resolves the selected allowlisted profile against the immutable startup snapshot and builds a fresh scoped handler/module. The other two tools (`capabilities` and `plan`) remain target-free; in particular, offline plan distillation does not invent a target. Fixed mode retains its existing schemas and rejects the request-only `target` argument. Matrix and parameter-set values are execution parameters and cannot change the target.

This is a breaking MCP input change: every request-scoped target-dependent tool now calls the database-selection object `target`; `scope` is not accepted as an alias. For `sqlharness_compare`, `scope` is reserved for the textual optimization fragment and `subject` names the SQL being optimized. With the default `compare.requireContext: true`, both string fields appear in the compare schema and are required. Use stable short ASCII labels, and use exactly `whole` for a direct whole-query comparison. `compare.requireContext` is global, independent of journal enablement, and is frozen for the MCP process. Change it in `~/.sqlharness/config.json` and restart the server, then reconnect the client. When false, the compare schema hides and rejects `subject` and textual `scope`; request-scoped `target` remains required. Other tools accept neither `subject` nor textual `scope`.

Example request-scoped compare call with context enabled:

```json
{
  "target": { "profile": "sample-country", "vars": { "tenant": "example", "env": "test" } },
  "subject": "orders-report",
  "scope": "whole",
  "baseline": { "sql": "SELECT ..." },
  "candidate": { "sql": "SELECT ..." }
}
```

The dashboard groups comparisons by `(sessionId, subject, scope)` and assigns SQL-content versions from hashes in first-seen order. `v0`, `v1`, and later labels are local to that group; they do not select a winner or establish a relationship between a fragment and the whole query. Numbering uses the full retained journal history before pagination. Journal retention can remove earlier observations and change version numbers; the dashboard presents retained history only.

Capabilities identify `scopeMode` as `fixed` or `request` and report the frozen `compareContext` policy. It includes `requireContext`; when true it also describes the label pattern, `whole` label, hash-based version identity and retained-history boundary. `connectionTimeoutSeconds` reports the effective SQL connection and driver-managed authentication timeout; Azure CLI token acquisition retains its separate existing budget. Mutation capability data reports the global default mode, click verification policy, approval timeout, and whether the current dashboard lock identifies a live dashboard process. Fixed mode also reports the active profile's effective mutation mode and whether a writer is configured. Request mode reports the effective mode and writer-configured boolean for each allowlisted profile without selecting an active profile; global settings still apply. These fields disclose no credentials, tokens, connection strings, or writer values. Dashboard reachability is a lock/PID liveness signal, not a health check or approval result.

Request mode reports the SQL Server engine and the permitted profile names from the explicit allowlist; neither mode reports resolved database lists, variable values, credentials, or local paths other than the configured input roots (`inputRoots`). The operator's `--allow-profile` list permits selecting those profiles for target-scoped calls; the client confirms the intended profile and target before SQL. The list approves profiles only: changing the vars within an allowlisted profile still needs a new explicit user request. A profile outside the list needs the operator to restart the server with a new list. Mutation execution still requires the dashboard's configured approval policy; profile allowlisting does not approve a write. A dry run stays offline and does not prove live identity, object existence, or database permissions. On a successful connection, the server name/address is reported as observed context; TLS certificate validation authenticates the endpoint (`trustServerCertificate: false` for SQL Server, `sslMode: verify-full` for PostgreSQL). Exit 4 means the connected database differs from the resolved database using exact ordinal comparison.

Each resolved scope carries an artifact owner tuple built from its profile request and resolved target: `{ profile, canonical vars, engine, server, database }`. In fixed mode the scope comes from the startup request; in request-scoped mode it comes from that tool call. Vars are stored case-insensitively with deterministic (ordinal-sorted) order, so the same scope serializes stably. The owner holds no auth material and no secrets: passwords and environment values never land in owner metadata or reports. Matching is exact and fails closed: profile, engine, server, and database compare ordinal; var keys compare case-insensitively with exact values; the full var set must agree (count included). Malformed candidate metadata simply does not match and never throws. The owner survives a restart because it is persisted in the artifact, not in server memory: a new process with the same profile, canonical vars, and resolved engine/server/database matches again without any in-memory state.

## Versions

- MCP SDK: `ModelContextProtocol` 2.2.0 (`ModelContextProtocol.Core` 2.2.0), pinned in `Directory.Packages.props`.
- Offered protocol revisions: `2025-06-18`, `2025-11-25`, `2026-07-28`. The server negotiates: an `initialize` for `2025-06-18` or `2025-11-25` gets that revision; an `initialize` for an older revision gets `2025-11-25`; a `2026-07-28` client uses `server/discover` and per-request `_meta`.
- The server version travels in the `initialize` result's `serverInfo` (or `server/discover`); `sqlharness_capabilities` reports runtime configuration only.
- Real-client check before release: `pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness <worktree build>` (manual; covers Claude Code, Codex, Copilot CLI, opencode and Grok Build, each skippable with `-Skip<Client>`; `-OpencodeModel` picks a free opencode model when the default hits a usage limit; the Grok run trusts its scratch folder in `~/.grok/trusted_folders.toml` for the duration and restores the file byte for byte).
