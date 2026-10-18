# Plan 081 verification evidence

Status: DONE and locally integrated. Final `main` is `e88c7fdd76ce3ce188296a56e2d63e73c57a52c7`. The integrated product tree was verified at `a717d579a6b8be0f96d6d745283442a769cbbcc6` and committed as merge commit `93122e886d40bef7c746ea91272c0169d819b56c`; the later advance to `e88c7fd` did not change product code. Sol low whole-branch review, final-fix review, integration review, and integrated gate are complete. No push or live database UAT occurred.

## Known implementation evidence

- Planning base: `1737091`; plan 079 was confirmed as an ancestor at `13b468a`. The Task 0 drift check was reviewed.
- Baseline `just verify` exited 1 while Task 1's intentionally red tests were present. Build and `ui-check` passed; the .NET run reported 4,207 passed, 11 skipped, and 29 failed. All 29 failures were the new Task 1 `SqlSafetyControlFlowTests`; existing tests passed. This is not a baseline gate pass.
- Task 1 classifier implementation commit chain: `c8a449b..abbd601`. Focused SQL safety verification passed 595/595 after review fixes. The scoped review found no Critical or Important findings.
- Task 2 cleanup implementation commit chain: `2232730..fd25a55`. The focused cleanup/setup/message suite passed 64/64 after review fixes. The scoped review found no Critical or Important findings. No live SQL Server cleanup was exercised.
- Task 3 session-state implementation commit: `4eb2315`. Focused Compare/Measure verification passed 96/96. The scoped review found no Critical or Important findings. The initial per-parameter-set capture/restore ordering coverage gap was later resolved by test-only fix `38b8148` and approved on re-review. No live SQL Server DMV/state verification was performed.
- Task 4 dashboard notification implementation commit: `0c2829b`. Focused backend journal/config/feed tests passed 77/77, MCP projection passed 1/1, and the focused UI suite passed 41/41. The scoped review found no Critical or Important findings. `LiveFeed.cs` and `DashboardSettings.cs` were reviewed and verified unchanged because their existing generic pass-through handles the behavior; Task 4's actual changed seams include `JournalReader.cs`, `DashboardModels.cs`, config, MCP projection, and UI.
- Task 5 documentation/capability contract commit: `90afb5c`. Capability contract tests passed 100/100 and fixed/request MCP guidance tests passed 3/3. The security review approved the classifier/executor diff at `90afb5c` with no persistent-effect reachability findings; the subsequent source cleanup and journal reader fixes were separately approved at `d65cfac` with no Critical or Important findings.

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

## Scope and verification limits

- No implementation, review, gate, or integration checks remain pending for Plan 081.
- Optional operator-authorized live UAT was not authorized or performed. No live database calls were made in these tasks.
- Linux verification is not a current completion requirement under `docs/agents/verification.md`.

## Final feature-branch gate

`just verify` passed at `d65cfac7b9c472b82daaca132e090709c57b8b87`; it exited 0 in 4m57s: build passed in 29.6s with zero warnings/errors; UI passed in 3m12s with 56 files and 291 tests; Core passed 4,300 with 11 skipped; MCP passed 427 with 4 skipped. UI install was skipped because the lockfile was unchanged. Formatting was skipped with zero changed C# files; no full-branch format pass is claimed. The UI run emitted non-failing localhost:3000 connection refusals and Node `DEP0190` warning. These standalone results are historical feature-branch evidence.

Two earlier full-gate attempts exposed failures, which were fixed before this final pass: `90afb5c` failed UI checks, and `439dabf` failed Core/MCP tests. The UI correction is in `439dabf`; subsequent fixture, error-preservation and journal ordinal fixes are in `d65cfac`. The gate-fix review approved `d65cfac` with no Critical or Important findings; it left one minor test-order coverage note for final Sol review. No tests were filtered out of the final gate other than the repository's normal `FullyQualifiedName!~Integration` policy.

## Final review and per-set regression fix

Sol low reviewed the complete feature branch at `d3670d8`: 0 Critical, 0 Important, and 1 Minor finding. The minor noted that the per-parameter-set capture/restore test checked counts and capture-after-setup, but did not pair every setup, capture, warmup, repetition, and restore in order. Test-only fix `38b8148` added the per-set sequence assertions. Its focused regression passed 1/1 and `MeasureParameterSetExecutionTests` passed 25/25; the scoped Luna xhigh re-review found no new breakage and marked the finding addressed. The integration review then approved the resolved integration tree with 0 Critical, 0 Important, and 0 Minor findings.

## Cross-task and integration decisions retained

- Parameterized setup prefix checks for `BEGIN TRAN`/`RETURN` belong to Task 2, which has the split/parameter context; Task 1 keeps whole-batch read-only admissions. This preserves the spec's prefix-only restriction. Reversing ownership would require Task 2 API/test rework.
- Task 2 owns typed timeout and cleanup-discard warning metadata; Task 4 persists and projects those warnings to reports, journal, and dashboard. This avoids reconstructing notices from free-form error text. Reversing that split would require exception/report API rework.
- Task 4 did not need edits to `LiveFeed.cs` or `DashboardSettings.cs`: generic pass-through already carries notices/settings. The actual modified seams are documented in the plan. This resolves the stale file list without no-op edits; the cost of a wrong call would be traceability documentation rework.
- During integration with Plan 087, its `operations.measurement_fingerprint` migration remains journal v15; Plan 081 adds `warnings_json` at v16, with `FirstRetainedVersion=14`. Both migration paths and fresh schema retain both columns. Reusing v15 would conflict with the other feature and require migration compatibility rework for unpublished standalone Plan 081 development journals.

## Integrated verification and local completion

The resolved integration tree `a717d579a6b8be0f96d6d745283442a769cbbcc6` passed full `just verify` (exit 0, five stages, 6m44s) before merge commit `93122e886d40bef7c746ea91272c0169d819b56c`: build passed in 30.5s; UI passed 56 files and 294 tests in 3m26s; Core passed 4,317 with 11 skipped; MCP passed 430 with 4 skipped (test stage 1m58s); format passed 28 C# files in 1m19s. The integrated tree exactly matched the tested tree before commit. Local `main` was then promoted to `e88c7fdd76ce3ce188296a56e2d63e73c57a52c7`; the controller confirmed the product diff from the tested tree is empty. This is local integration evidence, not a push or live-server test.

The standalone feature-branch gate at `d65cfac` remains separately recorded above; the integrated gate supersedes it for current completion. Temporary SDD logs and reports are not the durable record; this file preserves the review decisions, gate counts, and verification limits.
