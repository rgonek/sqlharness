# Plan 062 T2 implementation report

Status: `DONE_WITH_CONCERNS` — the T2 implementation and focused evidence are complete. The MCP suite has six publish-restore failures caused by current vulnerability audit warnings for `System.Security.Cryptography.Pkcs` 9.0.13; this task did not change dependencies. T7 owns resolving build/gate blockers.

## Implementation

- The MCP host loads `SqlHarnessConfig` once when composing the process, then carries its `Compare.RequireContext` policy through the immutable `McpProcessContext`, tool catalog, schemas, and handlers. A running host keeps that snapshot; a newly created host loads the updated config.
- Compare has method-specific schema registration per MCP instance. With context required, `subject` and textual optimization `scope` are required compare arguments. With context off, both properties are absent and unknown-argument validation rejects attempts to send them. Request-scoped compare always requires its `target` object.
- All six request-scoped target-dependent tools expose `target` with the existing `{profile, vars}` structure: query, measure, compare, watch, snapshot, artifact. They do not accept the former object argument `scope`; compare reserves textual `scope` for optimization context. Other tools reject textual `scope` as an unknown argument.
- The mapper parses the compare context and assigns it to both compare operation forms. Target and context validation happen before module construction/execution.
- No operator dashboard/API, live database, user config, CLI option, or database behavior was changed.

## RED/GREEN evidence

RED was established with the new request-schema regression test against the pre-implementation code:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests.Request_scope_schema_is_required_only_on_the_eight_target_tools" --verbosity normal
```

It failed as expected: the request schema still required `scope` and did not expose `target`.

After implementation, the required focused filter passed:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpRequestScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpStartupTests" --no-restore --verbosity minimal
```

Result: 135 passed, 0 failed, 0 skipped. The two policy/freeze evidence tests also passed together: 2 passed, 0 failed. MCP build passed with 0 warnings and 0 errors. `git diff --check` passed.

The isolated MCP stream-host test exercised `tools/list` and `tools/call` for opposite policy instances. The `RequireContext=true` host listed compare with required `subject`/text `scope`, and its dry-run compare call succeeded. The `RequireContext=false` host omitted both properties, rejected hidden fields, and its context-free dry-run compare call succeeded. The legacy object `scope` was rejected on all eight current tool names (including the two target-free tools); text `scope` was rejected on the other five target-dependent tools. Compare with an object-valued text `scope` and compare missing the context pair were rejected. Tests use a synthetic `mcp-unreachable.invalid` profile; rejected fields are checked before target resolution/module creation in the request handler, so these paths do not open a database connection. No live database variables were configured.

The host config-freeze test created one host with context on, rewrote the config to off, confirmed that host kept its original schema, then created a second host and confirmed its schema and context-free compare call used off. This is per-host snapshot behavior, not a mutable SDK/global schema cache.

Captured schema sizes (UTF-8 serialized input schema for `sqlharness_compare`): on = 3,829 bytes; off = 2,510 bytes; difference = 1,319 bytes.

## Full MCP suite

One full-suite run was made, as requested:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --verbosity minimal
```

Result: 361 passed, 6 failed, 4 skipped (371 total). All six failures were published-process/stderr tests that invoke `dotnet publish -r win-x64`; publish restore failed with `NU1902` and `NU1903` treated as errors for `System.Security.Cryptography.Pkcs` 9.0.13, citing GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx. The failures were the two current-RID published-server smoke cases and four process stderr regression cases. Live PostgreSQL and SQL Server tests were skipped because their configuration environment variables were absent. No dependency changes were made, and the full suite was not repeated.

## Changed files

- `src/SqlHarness.Mcp/McpHost.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/McpProcessContext.cs`
- `src/SqlHarness.Mcp/McpScope.cs`
- `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- MCP tests covering schema/runtime, mapping, startup, request target naming, and existing MCP compare callers.

No known product issue remains within T2. The full-suite restore blocker needs the dependency/security-audit path handled outside this task. Final gates remain for T7.