# PostgreSQL correctness audit Implementation Plan

**Status (2026-10-08):** DONE — T1 3d8ce5f, T2 6b7801e/de84dfe, T3 10c44f8. The checkboxes below were not ticked off during implementation.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove false rejections of queries and connections and incorrect PostgreSQL metrics.

**Architecture:** Parameters and identity belong to the engine; metrics preserve an explicit semantics and availability. The new TLS policy has a migration consistent with the existing profiles.

**Tech Stack:** .NET 8, Npgsql, SqlParserCS, xUnit.

**Spec:** [audit B1–B3 and S5](../specs/2026-09-26-project-audit.md), [roadmap](2026-09-26-audit-roadmap.md), existing specification `2026-09-10-postgres-engine-design.md`.

## Global Constraints

- PostgreSQL 14+; both engines and the existing profiles must keep support.
- The historical specification deliberately had no sslMode. T2 is a contract change, not a silent reinterpretation of trustServerCertificate.
- equivalence results come from the sidecar; do not present them as rows returned by EXPLAIN.
- Do not translate native PostgreSQL to T-SQL nor remove target checking.

## Review Focus

- @param in a comment, a string and a dollar-quoted literal: T1.
- A parameter used only in setup or in a single variant: T1.
- DNS, IPv6, proxy, an address change and the wrong database: T2.
- An untrusted certificate and an inconsistent TLS configuration: T2.
- A plan without buffers, a parallel plan, fractional ms: T3.

## T1 — parameter references of the correct dialect

**Files:** `src/SqlHarness.Core/Dialect/ISqlDialect.cs`, `Dialect/SqlServerDialect.cs`, `Postgres/PostgresDialect.cs`, `SqlSafety.cs`, `SqlHarnessModule.cs`, `MeasureParameterSets.cs`; new `Postgres/PostgresParameterReferenceValidator.cs`; `tests/SqlHarness.Tests/SqlParameterReferenceValidatorTests.cs`, `Postgres/PostgresParameterTests.cs`, `MeasureParameterSetValidationTests.cs`.

**Interfaces:** add `void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches)` to the dialect. All commands and param-set validation call the same contract. T-SQL delegates to the existing validator; PostgreSQL uses native analysis, consistent with how Npgsql binds.

- [ ] Positive tests `SELECT @n::int`, `SELECT @n LIMIT 1`, JSON operators, dollar quoting; negative tests for a genuinely unused parameter and invalid syntax.
- [ ] Tests for comments/strings, repeated references, setup-only and multiple sets. Recognize parameter names according to the binder, without regex detection inside literals.
- [ ] Run the tests and confirm the current false rejections, implement routing in all query/measure/compare/matrix/watch/snapshot/param-set.
- [ ] Share the parsed document where safety and the benchmark analyze the same SQL; if this requires a larger change, limit T1 to correct routing and perform the consolidation in 05/T1.
- [ ] Run the indicated tests and PostgresQueryTests/PostgresBenchmarkTests; local commit `fix: validate parameter references with the selected dialect`.

## T2 — TLS and connection identity

**Files:** `Targets/TargetProfile.cs`, `Targets/TargetResolver.cs`, `Contracts.cs`, `Postgres/PostgresConnectionString.cs`, `Postgres/NpgsqlSessionFactory.cs`, `SqlExecution.cs` pod `src/SqlHarness.Core`; `docs/example-targets.json`, `README.md`, `AGENTS.md`; `tests/SqlHarness.Tests/Postgres/PostgresConnectionStringTests.cs`, `Targets/ProfileStoreTests.cs`, `SqlExecutionTests.cs`; new spec `docs/superpowers/specs/2026-09-26-postgres-transport-policy.md`.

**Interfaces:** plan for optional `sslMode` and `rootCertificate` in the PostgreSQL profile; mode values `verify-full`, `verify-ca`, `require`, `disable`. The password secret stays only in the env. Record the details of the mapping to ResolvedTarget in the spec before changing the record.

- [ ] Record a migration matrix: no new fields keeps the old mode and reports the legacy policy; an explicit sslMode is authoritative, a configuration inconsistent with trustServerCertificate is rejected; rootCertificate only with a verifying mode. New remote connection examples use verify-full.
- [ ] Builder tests: VerifyFull, CA, Require, Disable, a missing/unreadable CA, an inconsistency and a SQL Server profile with PG fields. Do not read the password into error messages.
- [ ] Separate the engines' identity checks. For PG always check database; bind the endpoint identity to the actually established and verified connection, not to DNS equality with inet_server_addr. Record the proxy/loopback and non-verifying mode policy in the spec; do not treat DNS alone as authentication.
- [ ] Unit tests on a controlled resolver/endpoint metadata: DNS→IP passes according to policy, a different database always fails, a different authenticated host fails. No network in the default tests.
- [ ] Opt-in TLS test in a one-off environment: wrong host, untrusted CA, correct CA and a connection without TLS. Requires an authorized target, does not use user profiles.
- [ ] Update the migration documentation and run the indicated tests; a separate local commit. Do not change the historical specification without a note about the policy being superseded.

## T3 — correct, explicit metrics

**Files:** `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`, `Postgres/PostgresPlanDistiller.cs`, `Artifacts.cs`, `BenchmarkSummary.cs`, `MeasureParameterSets.cs`; `src/SqlHarness.Cli/Commands/Renderer.cs`; `tests/SqlHarness.Tests/Postgres/PostgresBenchmarkTests.cs`, `BenchmarkSummaryTests.cs`, `MeasureParameterSetReportTests.cs`.

**Interfaces:** `PostgresBenchmark.ParseStats(string)` preserves the whole logicalReads = root shared/local hits+reads. The old long fields remain compatible; add precise time and CPU availability as explicit report metadata and move it to the summary, per the 03/T1 contract.

- [ ] Test root=10, child=10 → total=10; dodanie warstwy Aggregate does not change total. Add fixtures with local buffers, parallelism and missing metrics.
- [ ] Separate the global buffers from per-relation diagnostics; describe the source and the lack of additivity, keep schema-qualified relations if available. Do not sum the workers a second time.
- [ ] Preserve the fractional time in an additional metric; 0.3 ms must not be presented as a measured exact 0. Mark PG CPU as unavailable; cross-set CPU must not indicate a winner based on placeholder zeros alone.
- [ ] Describe planning/execution/sidecar and the warnings in the machine contract. Full and shortened responses must convey the same metric availability.
- [ ] Run the indicated tests plus CompareTests and MeasureTests, and record a local commit `fix: report PostgreSQL benchmark metrics without double counting`.
