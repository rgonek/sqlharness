# Plan 104 final review fix

## Findings addressed

- Plan-side `kind=table` rows no longer become anonymous-variable groups from a `#` plus eight hexadecimal characters alone. Explicit `table-variable` kind and `@` identity remain producer evidence. Added explicit hex local-temp, mixed variable/local-temp, and typed identity tests.
- Reconciliation keys now retain the anonymous-variable-group type separately from the display name while preserving case-insensitive name matching and existing schema/name normalization. A real object named `table variables` therefore cannot collide with the anonymous group; swapped counters remain Partial.
- Synthetic table-variable capture fixtures now carry `AnonymousTableVariable` producer identity. This keeps manually constructed/legacy rows without producer provenance Unknown.
- The complete empirical SQL Server table-IO identity rule is exposed once as nullable `engineGuidance.tableIo`, serialized only for SQL Server; the measure/compare descriptions use a short pointer. PostgreSQL omits the member. Capabilities contract version is 9 because the document shape changed. MCP docs describe the field.
- The failing MCP operation-annotation request catalog remains under the existing 32768-byte ceiling with no ceiling increase or assertion relaxation. Existing hash qualifiers and optimization-key independence wording remain intact. SQL Server table-IO guidance is engine scoped.

The earlier gate failures were: the compact statement-evidence fixture expected Complete but was Partial because its manually built `TableIo[@P]` had no producer identity; fixed annotations-on request tool catalog exceeded 32768 bytes; and PostgreSQL tool descriptions inherited SQL Server aggregation wording. The fixture now states its producer identity, the descriptions point to the SQL Server capability field, and the annotation budget passes.

## Verification

- RED: `final-fix-logs/sqlharness-plan104-final-fix-red-core.log` records the new reconciliation regressions failing before the fix.
- Core final: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter 'FullyQualifiedName~StatementReconcilerTests|FullyQualifiedName~StatementMetricsTests|FullyQualifiedName~CapabilitiesCommandTests' -v minimal` — 142 passed, 0 failed.
- MCP final: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter 'FullyQualifiedName~McpToolSchemaTests' -v minimal` — 65 passed, 0 failed. This includes the annotation byte-budget and SQL Server/PostgreSQL serialization checks.
- Automatic formatter: `pwsh ./scripts/format-changed.ps1 -RepoRoot D:\Dev\sqlharness\.worktrees\plan-104 -Mode working-tree -Fix` — exit 0. Post-commit branch check `pwsh ./scripts/format-changed.ps1 -RepoRoot D:\Dev\sqlharness\.worktrees\plan-104 -Mode branch -BaseRef ab46345a` also passed (`format: OK`, 21 changed C# files); raw log: `final-fix-logs/sqlharness-plan104-final-fix-branch-format.log`. The repository keeps `insert_final_newline = false`.
- Raw initial-gate, RED, failed intermediate, budget, and final green logs are retained in `plans/104-evidence/final-fix-logs/`. The final outputs are `sqlharness-plan104-final-fix-green-core-final4.log`, `sqlharness-plan104-final-fix-green-mcp-final3.log`, and `sqlharness-plan104-final-fix-budget-final.log`. An intermediate MCP schema attempt with two stale assertions and an intermediate Core run before the version assertion update are also retained to keep the failure history explicit.

No SQL, merge, push, or artifact-schema change was performed. Root will run the full gate and scoped whole-branch rereview.
