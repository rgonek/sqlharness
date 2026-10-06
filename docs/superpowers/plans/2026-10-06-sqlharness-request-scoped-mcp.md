# SQLHarness Request-Scoped MCP Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` for inline implementation, or `superpowers:subagent-driven-development` if the user chooses delegated execution. Track steps with checkboxes. This document authorizes planning only; do not commit, push, or alter clients during planning.

**Goal:** A general SQL Server MCP in Codex CLI and Claude Code, with an explicitly supplied closed profile and variables per operation.

**Architecture:** Add an opt-in SQL Server request mode alongside the existing fixed mode. Freeze permitted profile definitions and process limits at startup, then create one immutable scope per call using the same Core resolver and owner enforcement. Keep one process-wide database gate and all 11 tools.

**Tech Stack:** .NET 8, C#, Spectre.Console.Cli 0.55.0, ModelContextProtocol 2.2.0, protocol `2025-11-25`, existing xUnit projects.

**Spec:** [2026-10-06-sqlharness-request-scoped-mcp-design.md](../specs/2026-10-06-sqlharness-request-scoped-mcp-design.md). Read both documents before implementation.

## Global Constraints

- Request mode is SQL Server only; preserve existing fixed mode and CLI behavior.
- No new transport/dependency, mutable selected scope, profile reload, raw target, mutations, or artifact ownership bypass.
- Startup allowlist is required; profiles, roots and budgets are frozen. Every target-dependent request supplies scope.
- Scope metadata is not user authorization; user-approved target and mutation rules still apply.
- Public tests use synthetic neutral profiles and disposable local SQL Server fixtures only. Actual organizational targets, credentials, personal paths, and live evidence remain outside this repository.
- Preserve unrelated changes. Use an isolated worktree for implementation if the checkout becomes dirty. No commits/pushes without user instruction.
- This general design and plan belong in the public SQLHarness repository. Public source, docs, tests, commit messages and PRs contain no organization-specific identifiers or private deployment references.

## Review Focus

1. Missing scope after successful call A must reject before connection, never reuse A (Tasks 1-2).
2. Profile-file edits and case-colliding vars must not redirect or ambiguously bind a call (Task 1).
3. Overlapping database calls for A/B and concurrent local artifact reads must not exchange owners (Tasks 2-3).
4. Cancellation/EOF during A must release the gate and dispose its session before a subsequent B (Task 3).
5. Public examples and test fixtures must stay neutral; scan tracked content for organization identifiers before delivery (Task 4).

## Task 1: Introduce startup policy and request scope resolution

**Files:** Modify `src/SqlHarness.Mcp/McpServerOptions.cs`, `McpScope.cs`, `src/SqlHarness.Cli/Commands/McpServeCommand.cs`, `src/SqlHarness.Cli/SqlHarnessCli.cs`; create `src/SqlHarness.Mcp/McpProcessContext.cs`, `McpRequestScope.cs`; test `tests/SqlHarness.Mcp.Tests/McpStartupTests.cs`, `McpScopeTests.cs`, new `McpRequestScopeTests.cs`.

**Interfaces (new):**
- `McpServerOptions.RequestScope: bool`, `AllowedProfiles: IReadOnlyList<string>`.
- `McpRequestScope` record: `string Profile`, `IReadOnlyDictionary<string,string> Vars`.
- `McpProcessContext.Create(McpServerOptions options, Func<IReadOnlyDictionary<string,TargetProfile>> loadProfiles) -> McpProcessContext`.
- Context owns mode, immutable profile snapshot, fixed scope if present, roots, budgets and one gate; `ResolveScope(McpRequestScope? request) -> McpScope` creates an immutable call scope.
- Reuse `McpScope` target/owner semantics and `CreateModule()`; separate process settings from target requirements. Profile snapshot must not retain externally mutable dictionaries. Context also supports target-free tools without a fabricated target.

- [ ] Add failing tests: request startup has no connections, no profile vars needed, loader runs once; incompatible/missing mode options fail with exit 2; missing/duplicate/unsupported allowlist fails safely.
- [ ] Add resolver tests with synthetic SQL Server A/B: exact targets and owners differ; missing scope, nonallowlisted profile, invalid/missing/extra vars fail; nested duplicate names/case-colliding var keys reject; modifying original profile dictionaries after startup leaves results unchanged.
- [ ] Run `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpStartupTests|FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpRequestScopeTests"`; confirm new tests fail for missing behavior, then implement mode parsing and shared resolver to pass.
- [ ] Keep existing fixed startup argument syntax and errors working. Validate failure text contains no input values or secrets.

Completion: request startup and offline resolution work without connections; old startup tests pass.

## Task 2: Wire per-call scopes into all target-dependent tools

**Files:** Modify `src/SqlHarness.Mcp/McpHost.cs`, `Tools/McpToolCatalog.cs`, `McpOperationMapper.cs`, `McpInputReader.cs` where it currently requires target state; create `Tools/McpRequestToolHandlers.cs`; test `McpToolSchemaTests.cs`, `McpMappingTests.cs`, `McpProtocolTests.cs`, `McpTokenBudgetTests.cs`.

**Interfaces:** Request handler facade accepts `McpProcessContext`; target-dependent methods require `McpRequestScope scope` plus existing tool arguments. Resolve once, instantiate/use existing scoped handlers with the process gate and host token. Change catalog creation/wiring to consume process context and select fixed/request facades; keep fixed handlers usable by existing tests. Extract shared execution/output plumbing if needed rather than duplicating Core mappings.

- [ ] Add failing schema tests for all 11 tools: eight require nested scope in request mode; fixed mode rejects scope; capabilities/plan/gain remain target-free. No raw target/auth/mutation fields. Unknown nested keys and duplicate JSON fields reject at wire parsing before SDK dictionary normalization can hide them.
- [ ] Add protocol tests: sequential A then B map only to the supplied scope; call without scope after A fails; same process handles target-free plan/capabilities without selecting A or B.
- [ ] Extend capabilities additively with scope mode and operator-allowed profile names only in request mode; preserve fixed engine behavior and exclude variable values/unrelated profiles.
- [ ] Implement request facade and shared host wiring. Use process-root controls for target-free file input, not a dummy resolved database. Keep actual resolution inside each request and all matrix/set work on that same scope.
- [ ] Run `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpTokenBudgetTests"`; pass schemas, mappings, tool catalog <=32768 B and result budgets without increasing limits.

Completion: the public request schema matches enforcement for every tool; old catalogs remain compatible.

## Task 3: Prove ownership, concurrency, cancellation and redaction

**Files:** Test `SnapshotScopeMappingTests.cs`, `McpLifecycleTests.cs`, `McpCancellationTests.cs`, `McpSecretRedactionTests.cs`, `McpStderrLeakRegressionTests.cs`; new `McpRequestScopeIsolationTests.cs`. Modify `McpExecutionGate.cs`/mapping/handlers only for demonstrated gaps.

**Interfaces:** Existing `ArtifactOwner` is built from the resolved call scope; request facade passes one shared gate, existing clock and host shutdown token. No Core owner format or CLI compatibility changes are planned.

- [ ] Add failing synthetic-owner tests: B cannot read A benchmark sections or snapshot baseline; own artifacts remain available after restart with the same startup profiles; legacy ownerless data remains denied; foreign snapshot capture refuses before connection; matrix/set artifacts retain their invocation owner.
- [ ] Test concurrent database A/B: one runs, other returns `busy`, zero second connection. Concurrent local artifact A/B reads use their own immutable scope. No per-scope gates.
- [ ] Test cancelling A and EOF: deadlines reach execution, connection/session disposed, gate released; a later authorized synthetic B works after cancellation. EOF cancels and shuts down rather than executing another queued request.
- [ ] Test stderr and safe errors with sentinel SQL, var/parameter values and secret strings; none appears. Keep target evidence only where existing result contract permits it.
- [ ] Run the full MCP project: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj`. Record pass/fail/skip; do not enable live integration environment variables.

Completion: ownership isolation and lifecycle safety are proven offline for multiple scopes, with existing fixed-mode regressions passing.

## Task 4: Verify published protocol and document both modes

**Files:** Modify `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs`, `McpDependencyTests.cs` only if new host shape needs fixture changes; `docs/mcp.md`, `AGENTS.md`, `README.md`. Keep design history coherent; sanitize organization-specific mentions without rewriting Git history.

- [ ] Add published stdio request-mode smoke: initialize `2025-11-25`, list exactly 11 tools, validate synthetic A/B, reject missing scope and unsafe arguments, target-free plan, pure protocol stdout, clean EOF. Test fixed published smoke unchanged.
- [ ] Run `dotnet test` for the complete offline suite and inspect skips. Publish to a temporary directory with `dotnet publish src/SqlHarness.Cli/SqlHarness.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o <absolute-temporary-publish-directory>` using existing repository publish settings. Exercise that exact artifact over stdio.
- [ ] Update maintained contracts: new syntax, frozen allowlist, per-call scope, eight scope-dependent tools, ownership and gate semantics, target-free tools, static-analysis boundary, safe examples. No PostgreSQL request-mode instructions.
- [ ] Audit tracked source, docs, fixtures and generated delivery text for private identifiers, server/database names and user paths. Use neutral examples such as `sample-country`, `sample-shared`, `tenant=example`, `env=test`, and `C:\work\sqlharness-inputs`. Keep all real deployment work outside this repository.
- [ ] Run git diff --check; compare final status with baseline and record focused file changes, full offline results, published smoke evidence and live limits.

Completion: distributable binary implements both modes and documentation no longer promises that all MCP processes have one startup target.

## Self-review / coverage

All design sections map to Tasks 1-4. Scope-required tools include offline validate and artifact; target-free plan does not fabricate a scope. Snapshot and benchmark owners remain in Core; no migration is needed. Startup profile snapshot and gate remain process-wide, while target ownership is per call. Known execution prerequisite: this plan must be reviewed before implementing; client deployment is a separate private operational task.
