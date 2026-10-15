# Plan 064: Agent guidance follows the profile's engine

> **For agentic workers:** Use superpowers:executing-plans or superpowers:subagent-driven-development to execute this plan. This document contains the design and implementation tasks; no separate spec is required.

**Goal:** A caller using a SQL Server profile sees SQL Server guidance, and a caller using a PostgreSQL profile sees PostgreSQL guidance. Engine-neutral discovery remains brief. New engines add guidance in one place instead of adding conditionals across tools and skills.

**Architecture:** Reuse the engine registry in `SqlHarnessCapabilitiesProvider`. Separate common operation guidance from engine-specific additions. Render selected-engine additions in profile-scoped CLI capabilities and the MCP catalog's tool/argument descriptions. CLI help without an engine stays neutral and points to the existing profile-scoped capabilities command for engine rules. Keep MCP capabilities runtime-only.

**Tech Stack:** .NET 10, C#, Spectre.Console.Cli, the installed ModelContextProtocol SDK, xUnit.

## Status and base

- Status: DONE on branch `feat/plan-064-engine-guidance` (66bf672); Windows `just verify` OK; awaiting local merge to main.
- Priority: P2; effort: M; risk: MEDIUM (changes discovery metadata, not SQL execution).
- Planning base: `8a08859`, 2026-10-14, plus the pending operator-skill/description edits. Preserve those edits or start from their eventual integrated commit. Inspect drift before execution.
- Relevant files: `src/SqlHarness.Core/Capabilities.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`, `src/SqlHarness.Mcp/McpProcessContext.cs`, `src/SqlHarness.Mcp/McpScope.cs`, `src/SqlHarness.Cli/Commands/CapabilitiesCommand.cs`, `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`.

## Current gap

CLI `capabilities <profile>` already selects one engine's summary, parameter types, limits and session-temp statements. However, `Build` still emits shared command descriptions and shared target-identity text containing PostgreSQL facts for a SQL Server profile. MCP argument descriptions are static attributes: its shared setup description currently names SQL Server even on a PostgreSQL fixed-scope process. CLI help has the same problem. A generic skill also loads engine-specific paragraphs for users of another engine.

## Decisions and boundaries

1. The profile determines the engine offline. Never connect, discover databases or require profile variables just to obtain guidance.
2. Profile-scoped CLI capabilities show only the selected engine's detailed rules. With no profile, return the existing brief engine inventory and common guidance, not a merged manual.
3. MCP fixed scope uses `scope.ResolvedTarget.Engine` when creating its catalog. Request scope currently supports SQL Server only, so its catalog uses SQL Server guidance. Do not enable PostgreSQL request scope or mixed-engine request scope in this plan.
4. CLI command help remains engine-neutral: common inputs, session semantics and links to `capabilities <profile> --json`. Put native temp syntax, TLS settings and engine-specific metric caveats in selected-engine guidance. Do not add a new help flag or force profile-file reads for `--help`.
5. MCP descriptions carry the selected engine's essential facts at the call site. The consumer has no repository checkout. A README link cannot replace an indispensable setup or safety rule.
6. Common facts stay common: setup runs once per measured session, batch locals do not cross batches, set connection/cache policy, comparison semantics, budgets and artifact continuation. Engine additions cover native temp creation/lifetime, parameterized setup constraints, hidden effects and metric interpretation.
7. SQL Server setup guidance retains creation of #temp before its first parameter reference. PostgreSQL setup guidance states that TEMP setup runs read-write and a SELECT can call a function with persistent effects; review those functions and use a least-privilege role. SQL Server-only output must not carry that PostgreSQL paragraph.
8. Keep tool names, arguments, defaults, schemas' structural constraints, annotations, runtime capabilities shape, SQL classification, target checks and approvals unchanged. Description filtering is not authorization and never changes execution support.
9. Use a single registry entry per engine, extending the existing `EngineDetail` structure or a focused helper owned by it. Common descriptions remain a shared template. Missing guidance for a registered engine is a configuration/programming error, not a silent SQL Server fallback.
10. Keep catalogs within existing byte budgets. Do not add tools, resources, endpoints or a capabilities manual to transport prose. Preserve standalone downstream skills; they adapt upstream patterns rather than requiring the public skill at runtime.

## Task 1: Scope CLI capabilities and centralize engine guidance

**Files:** `Capabilities.cs`; tests in `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`.

- [x] Add failing tests for SQL Server and PostgreSQL profiles and engine-neutral discovery. Use fictional profile names. Verify selected-engine identity and operation guidance, absence of foreign-engine details, unchanged parameter types/limits and command coverage.
- [x] Extend the existing engine entries with operation/parameter guidance used by both adapters. Keep `Get()` neutral and `Get(engine, profileName)` selective. Remove engine-specific facts from common `targetIdentity` and command descriptions; supply them only through the selected entry.
- [x] Add a registry-completeness check against registered `SqlEngine` values so adding another engine requires one guidance entry. Preserve unknown-engine rejection and the current default for a profile with no explicit engine.
- [x] Run capabilities, target and parameter tests. Check response budgets or measured discovery fixtures if they cover the changed output. Do not pin whole English sentences.

**Completion:** CLI discovery is selective without a connection or secrets, and a new engine has one guidance owner.

## Task 2: Render MCP metadata for the process engine

**Files:** `Tools/McpToolCatalog.cs`, `Tools/McpRequestToolHandlers.cs`; existing `McpToolSchemaTests.cs` and `McpTokenBudgetTests.cs`.

- [x] Add tests for a fixed SQL Server process, a fixed PostgreSQL process and a SQL Server request-scoped process. Inspect descriptions returned through the served `tools/list`, not only provider strings. Verify engine-specific setup facts and no foreign-engine facts.
- [x] Determine the supported metadata-customization mechanism in the installed SDK before coding. Apply selected-engine guidance during catalog construction, before registration; avoid text search/replace across serialized schemas or mutating a shared toolset after it is served.
- [x] Keep structural input schemas, required fields, allowed values, defaults and tool annotations unchanged. Use common operation descriptions plus the selected registry entry; do not duplicate engine switches in fixed/request handlers.
- [x] Verify two independently configured catalogs do not share mutable descriptions. Retain the existing request-scope SQL Server restriction and per-call scope isolation.
- [x] Run schema, request-scope, parameter binding, token-budget and stdio tests. Check actual `tools/list` bytes against the existing ceiling.

**Completion:** MCP callers receive relevant call-site guidance without repository access or cross-process metadata leakage.

## Task 3: Align help, skill routing and documentation

**Files:** `SqlHarnessCommands.cs`, `CapabilitiesCommand.cs`, relevant CLI description attributes, `README.md`, `docs/mcp.md`, `skills/sqlharness/SKILL.md`, `AGENTS.md` when its routing contract changes.

- [x] Keep unscoped CLI help generic and concise. Direct users to profile-scoped capabilities for native setup and engine caveats; preserve useful operational help and executable examples.
- [x] Remove unconditional PostgreSQL/SQL Server technical paragraphs from the generic skill when equivalent selected-engine guidance is exposed. Keep operation choice, scope, approvals, error handling and evidence interpretation in the skill.
- [x] Update the README and MCP reference to state what is neutral, what is profile/process-scoped and which surface owns the rules. Keep complete engine reference sections there, loaded only when relevant.
- [x] Run help/example parsing, command coverage and reference-link tests. Check that docs can be reworded without literal-prose test failures. Inspect engine-neutral help and both scoped outputs manually for irrelevant detail.
- [x] If downstream private adaptations are updated in the implementation session, keep them outside this public repository and self-contained. Publication of the binary and restart of running MCP processes remain separate operator actions.

**Completion:** the tool user receives needed selected-engine rules without unrelated engine material or implementation knowledge.

## Final verification

- [x] Review the public diff for secrets, real targets and private investigation material.
- [x] Run `git diff --check` and the checks required by the repository contract at execution time. Under the current contract run `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1` sequentially; report unavailable prerequisites rather than treating them as success.
- [ ] Record results and update this plan's index row only when implementation lands. No live database, install, commit, push or publication is authorized by writing this plan.

**Done:** selected CLI/MCP descriptions are engine-specific, neutral discovery/help stay brief, structural execution contracts remain unchanged, budgets hold, and users can work without the source checkout.

## Verification evidence (2026-10-15)

- Revision `66bf672` on `feat/plan-064-engine-guidance`: `just verify` → `verify: OK stages=6` (current contract, docs/agents/verification.md; the Linux gate is not a completion criterion). `git diff --check` clean.
- Suites on the feature branch: Core 3547 passed/29 skipped; MCP 379 passed/4 skipped. Served `tools/list` 15.7–16.0 KB against the 32 KiB ceiling (fixed SQL Server, fixed PostgreSQL, request scope).
- The final review built 7364117 and the branch head and compared six catalogs (three scopes × requireContext) with descriptions stripped. They were byte-identical, so only description text changed.
- Public diff uses fictional profiles and `*.invalid` hosts only.
