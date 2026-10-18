# Plan 081 verification evidence

Status: implementation and documentation are recorded on feature branch `plan-081`; final security review and `just verify` remain pending. This is not a DONE record. Controller owns those final checks and the post-gate evidence update.

## Known implementation evidence

- Planning base: `1737091`; plan 079 was confirmed as an ancestor at `13b468a`. The Task 0 drift check was reviewed.
- Baseline `just verify` exited 1 while Task 1's intentionally red tests were present. Build and `ui-check` passed; the .NET run reported 4,207 passed, 11 skipped, and 29 failed. All 29 failures were the new Task 1 `SqlSafetyControlFlowTests`; existing tests passed. This is not a baseline gate pass.
- Task 1 classifier implementation commit chain: `c8a449b..abbd601`. Focused SQL safety verification passed 595/595 after review fixes. The scoped review found no Critical or Important findings.
- Task 2 cleanup implementation commit chain: `2232730..fd25a55`. The focused cleanup/setup/message suite passed 64/64 after review fixes. The scoped review found no Critical or Important findings. No live SQL Server cleanup was exercised.
- Task 3 session-state implementation commit: `4eb2315`. Focused Compare/Measure verification passed 96/96. The scoped review found no Critical or Important findings. One deferred minor review note remains: no dedicated per-parameter-set capture/restore regression. No live SQL Server DMV/state verification was performed.
- Task 4 dashboard notification implementation commit: `0c2829b`. Focused backend journal/config/feed tests passed 77/77, MCP projection passed 1/1, and the focused UI suite passed 41/41. The scoped review found no Critical or Important findings. `LiveFeed.cs` and `DashboardSettings.cs` were reviewed and verified unchanged because their existing generic pass-through handles the behavior; Task 4's actual changed seams include `JournalReader.cs`, `DashboardModels.cs`, config, MCP projection, and UI.

## Task 5 documentation and contract evidence

At base revision `0c2829b2bb9bfb9646f46d1b805ccc3184be2bf1`, Task 5 updated the README human contract, SQL Server engine guidance surfaced through capabilities/tool descriptions, and MCP tool documentation. It also corrected the Task 4 planned file list and marked Tasks 0–4 according to the ledger. The installed skill did not name rejected constructs, so it was left unchanged.

The capability guidance documents the exact read-only scope and admitted/denied session grammar, transaction cleanup and discard behavior, option restoration, and PostgreSQL separation. Capabilities contract tests required updating to the new statements; the scoped command passed:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter FullyQualifiedName~CapabilitiesCommandTests --no-restore
Passed: 100, Failed: 0, Skipped: 0
```

The MCP catalog now appends the same engine grammar to query, measure and compare tool descriptions in both fixed and request scope. Focused catalog contract tests passed:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~Fixed_sql_server_catalog_carries_only_sql_server_guidance|FullyQualifiedName~Request_scope_catalog_carries_sql_server_guidance|FullyQualifiedName~Engine_guidance_changes_descriptions_only" --no-restore
Passed: 3, Failed: 0, Skipped: 0
```

An initial run found two stale expectations in that capability test file; the assertions were updated to match the new guidance and the above run passed. No runtime policy or SQL behavior was changed by Task 5.

## Pending final checks and boundaries

- Dedicated security review of the classifier and SQL Server executor diff.
- Full `just verify` on the final feature tree, with revision and stage results recorded by the controller.
- Controller's final whole-branch review and evidence update.
- Optional operator-authorized live UAT was not authorized or performed. No live database calls were made in these tasks.
- Linux verification is not a current completion requirement under `docs/agents/verification.md`.

The Task 5 scoped documentation checks are `git diff --check`, link/anchor review for the changed Markdown references, and the capabilities contract test above. Full implementation completion remains pending until the controller's security review and `just verify` pass.
