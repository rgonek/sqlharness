# Plan 062: compare context and SQL optimization dashboard

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking. The execution choice remains with the user.

**Goal:** Show the operator the SQL optimization history in a session and the evidence of a single compare attempt, with minimal context from the agent.

**Architecture:** One global policy governs the CLI context requirement and the frozen MCP contract. Core passes the subject/scope pair to the journal. In request-scoped MCP the existing `scope` object pointing at profile/vars is renamed to `target` in every target-dependent tool; textual `scope` means only the SQL fragment in `compare`. The dashboard builds a read model of versions and attempts from the retained history; the UI shows SQL → scope → attempts and a comparison of both sides without a new regression classifier.

**Tech Stack:** C#/.NET, System.CommandLine, ModelContextProtocol SDK, SQLite, React, TypeScript, TanStack Query/Router, xUnit, Vitest.

**Spec:** [2026-10-12-compare-dashboard-design.md](../docs/superpowers/specs/2026-10-12-compare-dashboard-design.md).

## Status and dependencies

- DONE, locally merged to main in `818f1e4`; P2; effort L; recorded 2026-10-12 on `52bdc23`; implementation on `feat/plan-062-compare-context`; Windows/Linux gates green at `3ca11c8`; review of T7 by Luna at xhigh and of the whole branch by Sol at low approved; no Critical/Important; two Minor items recorded in the evidence.
- Requires the existing compare verdict (051, DONE) and the current settings/journal/dashboard.
- 055–061 are not a hard dependency. In particular 060/061 change the same MCP catalog, mappers and matrix; implement sequentially or with an integration of the current base. Do not restore removed tools or old argument names.
- The whole plan is one unit of execution: context, persistence, API, both screens and documentation.
- Drift check: `git diff --stat 52bdc23..HEAD -- src/SqlHarness.Core src/SqlHarness.Mcp src/SqlHarness.Cli src/SqlHarness.Dashboard plans/README.md` and `git worktree list --porcelain`.

## Global Constraints

- The contract and normative values are in the Spec; they apply to all tasks.
- `compare.requireContext=true` by default; do not make it depend on `journal.enabled`.
- MCP compare off (`compare.requireContext=false`): no `subject` and textual `scope` properties, without accepting hidden fields; in request-scoped mode the `target` object remains required. CLI off: either both context fields are absent or a valid full pair.
- Breaking MCP change: in **every** database-dependent tool in request-scoped mode the public `scope` object (`profile`/`vars`) changes its name to `target`. Do not accept the old name as an alias. In fixed mode there is no `target` argument. In `compare` the name `scope` is reserved for the textual optimization scope; other tools do not accept that field. Do not change the names of CLI options used to select the database.
- Version boundary: `(sessionId, subject, scope)`, hash of both sides, full retained history. Measurement conditions belong to the attempt, not to the SQL version.
- Session identity is not changed in 062: the current MCP process key / current CLI rules determine `sessionId`. Do not add new agent conversation identification or automatic session merging.
- Do not treat different databases, vars, setups, parameters or cache as one comparable measurement series. The UI does not draw a shared trend or a version ranking on such data.
- No live DB and no access to the operator dashboard. Verification is isolated.
- Done requires both gates, run one after another, and updates to README, skills/sqlharness/SKILL.md, Capabilities.cs and docs/mcp.md.

## Review Focus

1. Two MCP servers with different configuration in one test process: schema and validation must not leak between instances (T2).
2. Repeating identical SQL on both sides with no hash: a stable version or an explicit unknown, without an artificial new version (T4).
3. More than 500 operations and retention: explicit pagination, completeness and no numbering computed from a single page (T4/T5).
4. Matrix: a single regression or differing results among improvements must not get lost in a green aggregate (T3/T6).
5. The latest whole ended in failure and an older green compare: the header shows the failure and the correct pair, without declaring success (T5).
6. In request-scoped MCP the `target` argument (connection object) does not collide with `scope` (the SQL scope text in compare); all DB tools reject the old `scope` object (T2).

## File map

Existing entry points: `SqlHarnessConfig.cs`, `SqlHarnessConfigWriter.cs`, `Contracts.cs`, `Capabilities.cs`; CLI `Commands/SqlHarnessCommands.cs`; MCP `Tools/McpToolCatalog.cs`, `Tools/McpRequestToolHandlers.cs`, `McpOperationMapper.cs` and the tool-registering host; journal `JournalModels.cs`, `JournalSchema.cs`, `ActivityJournal.cs`, `OperationJournalDescriber.cs`, `JournalSummary.cs`, `JournalBenchmarkBuilder.cs`; dashboard `DashboardModels.cs`, `JournalReader.cs`, `DashboardServer.cs`; UI `api/types.ts`, `api/queries.ts`, `pages/SessionPage.tsx`, `pages/OperationPage.tsx`, `pages/SettingsPage.tsx`, `lib/compare.ts`, `components/VariantPanel.tsx`.

New small modules: `Core/CompareContext.cs`, `Dashboard/CompareHistoryBuilder.cs`, UI `components/CompareSessionOverview.tsx`, `components/CompareDetail.tsx`, `components/SqlDiff.tsx`. Do not refactor unrelated query/measure paths.

### T1: Global policy and Core/CLI context

**Files:** Core/config/CLI map above; tests `Journal/SqlHarnessConfigTests.cs`, new `CompareContextTests.cs` and `Cli/CompareContextCommandTests.cs`.

**Interfaces:** `CompareConfig.RequireContext` (default true), `SqlHarnessConfig.Compare`; `CompareContext(string Subject, string Scope)`; nullable `Context` as an init property on both compare operations. The shared `CompareContext.Parse(string? subject, string? scope, bool required)` returns a valid pair or null, and invalid input is rejected by the existing validation error mechanism.

- [x] RED: missing config → true; round trip true/false; wrong type and unknown field are rejected; empty fields, fields with whitespace, slash, uppercase and 65 characters are rejected; 64 characters accepted; off allows zero fields but rejects one. `whole` preserved exactly.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests|FullyQualifiedName~CompareContext"`; confirm the expected RED of the new tests.
- [x] Add the CLI options and validation before DB/file execution following the existing preflight order; Core receives the context also for matrix. Do not change the SQL result or the regression assessment itself.
- [x] GREEN of the same filter; a fixture with an open counter proves zero connections when the pair is missing. Update the existing compare fixtures that require a pair, without globally disabling the feature in tests.
- [x] Commit the task after review.

### T2: Frozen, dynamic MCP contract

**Files:** MCP catalog/handlers/mapper/host from the map; tests `McpToolSchemaTests.cs`, `McpRequestScopeTests.cs`, `McpMappingTests.cs`, `McpStartupTests.cs`.

**Interfaces:** the host passes an immutable snapshot `RequireContext` to the catalog and handler registration. In every target-dependent request-scoped tool change the public name of the target selection object `scope` to `target` (its `profile`/`vars` structure stays unchanged). The mapper assigns `Context` from T1 for `compare`; internal types `McpScope`/`McpRequestScopeArgument` may keep their names. Schema and runtime use the same policy; do not use a mutated global SDK tool cache.

- [x] RED: `compare` in fixed MCP: true → textual `subject`/`scope` are properties and required, without `target`; false → both properties absent. `compare` in request-scoped MCP: the `target` object always required, and textual `subject`/`scope` only when true. Off + submitted hidden context fields → rejection. On + missing/empty/partial pair → error before the DB.
- [x] RED: all target-dependent tools in request-scoped accept `target: {profile, vars}` and reject the previous `scope` object (no alias), also when the compare context is off. For `compare` with true, an object submitted as textual `scope` is a wrong type. For other tools, textual `scope` is an unknown argument. Verify preflight without a DB, the unknown-arguments allowlist and `tools/list`/`tools/call` for fixed and request-scoped.
- [x] RED: two isolated instances with opposite settings; a config file change does not change the running contract, a new host sees the change.
- [x] Run `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpRequestScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpStartupTests"`.
- [x] Implement using the current SDK registration capabilities, with a separate per-instance schema. Review both MCP catalogs/handlers and every target parser/mapper path — do not change only `compare`. Keep the unknown-arguments allowlist and the budgets consistent. Do not introduce a server restart performed by the agent.
- [x] GREEN of the filter, a test of real tools/list and tools/call through an isolated stdio process without a DB. Measure the on/off schema size difference; do not pin the token count to a specific model.
- [x] Commit after review.

### T3: Persistent write of context and attempt evidence

**Files:** journal from the map, Core reports if required for the projection; tests `Journal/ActivityJournalTests.cs`, `Journal/ActivityJournalBenchmarkTests.cs` and the summary/describer tests.

**Interfaces:** `OperationStart.Context`; nullable columns `compare_subject`, `compare_scope`. The compare projection persists the existing regression verdict, the per-case equivalence mode/check and the available measurement conditions. It does not write a new duplicate of the whole report without a budget.

- [x] RED: migrating an existing DB loses no records, old pairs null; plain/matrix carry the pair; rejections/failures keep the known context; a disabled journal still does not interfere with execution.
- [x] RED: hash-only stores labels, hashes and safe metrics, but not setup SQL, parameter values or plans. storeSensitive on stores the available conditions within the existing budgets. The summary preserves a single-cell regression and an incomplete match.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`; confirm RED, implement the migration and the projection, confirm GREEN.
- [x] Describe missing historical data explicitly; do not open artifact files indicated by an arbitrary path from the DB. Commit after review.

### T4: History read model and dashboard contract

**Files:** new `CompareHistoryBuilder.cs`; `JournalReader.cs`, `DashboardModels.cs`, `DashboardServer.cs`; UI API types/queries; tests `Dashboard/JournalReaderTests.cs`, `Dashboard/DashboardContractTests.cs`, new `Dashboard/CompareHistoryBuilderTests.cs`.

**Interfaces:** new endpoint `GET /api/sessions/{id}/compares?cursor=...` with `Page<CompareAttemptSummary>`; fields of each attempt: operationId, subject/scope, baseline/candidate hash and version (nullable), status, startedAt, summary per case. Separate aggregation of subjects/scopes for the whole retained session in SessionDetail; do not compute headers from the UI page. OperationDetail receives the context, the versions and the T3 evidence projection. C#/TS types pinned by a contract test.

- [x] RED: v0→v1, v1→v2, v0→v2; baseline=candidate; distinct scope/subject/session; null hash; legacy without context; order start/ID and baseline before candidate.
- [x] RED: 501+ operations, a cursor without duplicates/gaps and numbering independent of the page; retention gives no claim about the original start. No N+1 detail fetching from the UI; the backend uses limited projections and indexes.
- [x] RED: latest whole failed, earlier succeeded; running does not replace a completed one; no whole; only running whole; v2→v3 gives no percentage against v0; a direct v0→v3 gives a result only for its own conditions. Another database/parameters create no trend.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareHistoryBuilderTests|FullyQualifiedName~JournalReaderTests|FullyQualifiedName~DashboardContractTests"`; RED → implementation → GREEN. Commit after review.

### T5: Session and context setting

**Files:** `SessionPage.tsx`, new `CompareSessionOverview.tsx`, `SettingsPage.tsx`, T4 API; SessionPage/SettingsPage tests and the new CompareSessionOverview.test.tsx.

**Interfaces:** the component consumes the whole-session aggregation and the paginated T4 attempts; the link to the operation keeps subject/scope through the attempt identifier, and going back restores the group. The setting writes `compare.requireContext` through the existing config writer mechanism.

- [x] RED: two subjects, multiple scopes, chronology, expanding groups, a link to compare and going back; All operations still available; metadata collapsible; missing context is a separate group.
- [x] RED: a failed whole does not show an old improvement; fragments only show the absence of a final compare; loading/error/empty/running/abandoned and loading more than 500 attempts do not lose the context.
- [x] RED: the toggle is on by default, saving off preserves other settings; the text explains the CLI requirement and the MCP restart; it does not perform an automatic restart.
- [x] In `src/SqlHarness.Dashboard/ui` run `npm run test -- src/pages/SessionPage.test.tsx src/pages/SettingsPage.test.tsx src/components/CompareSessionOverview.test.tsx`; RED → implementation → GREEN, then `npm run typecheck`. Commit after review.

### T6: Compare detail, SQL diff and both sides of the measurement

**Files:** OperationPage, VariantPanel and lib/compare; new CompareDetail and SqlDiff; tests of OperationPage, lib/compare and the new components.

**Interfaces:** `CompareDetail` consumes the T4 OperationDetail; the remaining operation types keep the current screen. The case comparison uses the pairs from the existing `comparePairs`, adapted to the current names from 061 if implemented.

- [x] RED: independent execution, regression and equivalence status; a missing verdict does not become an improvement; equivalence off, false, partial and legacy null have distinct labels.
- [x] RED: matrix with a single regression/differing results; null and zero baseline without a percentage; SQL Server/Postgres without invented metrics; runs and min–max visible.
- [x] RED: diff and full SQL; no storeSensitive; plans/IO of both sides; unavailable historical conditions; a large SQL does not block rendering. The diff is local, deterministic, with a limit of 256 KiB in total and 4000 lines; above that a message and the existing bounded preview of both texts. No new diff package if the existing dependencies/helper line diff suffice.
- [x] Run `npm run test -- src/pages/OperationPage.test.tsx src/lib/compare.test.ts src/components/CompareDetail.test.tsx src/components/SqlDiff.test.tsx`; RED → implementation → GREEN. Then `npm run check` and `npm run build`.
- [x] Verify responsiveness and keyboard operation on an isolated fixture page: desktop and 390 px width, tabs, groups, tables, diff, no-data states. Without the operator dashboard/API and without publishing a fixture with user data. Commit after review.

### T7: Documentation, integration and final gates

**Files:** README.md, docs/mcp.md, skills/sqlharness/SKILL.md, Capabilities.cs, plans/README.md; capabilities/schema tests aligned to the new contract.

- [x] Update all CLI/MCP compare examples with `subject`/`scope`; in **every** request-scoped MCP example and schema description change the database selection object `scope` to `target`. Describe the breaking change without an alias, the global setting, hiding the textual context fields when off (but not `target`), the MCP restart, `whole`, stable names, versions after hashes and the retained history restrictions. Do not add SQL context to the calls of other tools.
- [x] Capabilities reports the active context policy and the version semantics according to the real contract, without secrets and without new instructions that are unnecessary in off mode.
- [x] Review the whole diff against the Spec: the whole plan, no version selection mechanism, no composable percentages, no safety changes. `git diff --check` must return 0. The review of T7 and of the whole branch is finished with no Critical/Important.
- [x] Run in the foreground `pwsh ./scripts/verify.ps1`, and after it finishes `pwsh ./scripts/verify-linux.ps1` (WSL ext4; provision through the existing setup if required). Both gates finish with code 0, the native markers are recorded together with `VERIFICATION PASS`, and the full logs contain no case-sensitive `FAILED`; see [evidence](062-verification-evidence.md).
- [x] Uaktualizuj wiersz 062 Update the 062 row to the actual status and evidence. Commit po review. Integration into main and push only by separate operator instruction; post-merge evidence requires checking the integrated tree.

## Acceptance criterion

The operator sees the correct history of several SQLs and fragments in one session, recognizes a return to a previous version, opens a compare with evidence from both sides and does not receive a false declaration of final success. Off mode removes the token cost of the two compare context fields from the MCP contract, leaving request-scoped `target`. All DB tools in request-scoped already use `target` instead of the old `scope` object; both gates are green, the documentation matches the behaviour.
