# Architecture consolidation Implementation Plan

**Status (2026-10-08):** DONE — T1 1db723e, T2 c3ecffa + [ADR T2](2026-09-26-audit-05-architecture-adr-t2-typed-result.md), T3 4710313. The checkboxes below were not ticked off during implementation.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove scattered rules and repeated infrastructure without multiplying abstractions or changing command semantics.

**Architecture:** Keep the public facade and the session adapters. Consolidate dialect analysis, execution of operation families, result creation and file publication.

**Tech Stack:** .NET 8, C#, xUnit; no new DI container/plugin framework.

**Spec:** [audit — interfaces and duplication](../specs/2026-09-26-project-audit.md), [roadmap](2026-09-26-audit-roadmap.md).

## Global Constraints

- After the stabilization of the 01/02 fixes and the 03/04 contracts; do not let them delay urgent fixes.
- Keep ISqlHarnessModule, ISqlSessionFactory, ISqlSession, ISqlReader, ISnapshotStore and IGainStore as separate roles.
- Do not merge measure-param-set with compare-matrix nor with schema/space/indexes.
- Domain writers stay separate; they share the write mechanics, not the data-disclosure policy.
- No CLI/JSON changes solely because of moving classes.

## Review Focus

- A direct Core consumer and old test constructors: T1–T2.
- A setup error and a cleanup masking the primary cause: T2.
- A partially completed matrix and an artifact write: T2–T3.
- A directory move failure and concurrent calls: T3.
- Reparse, dead members and differing decisions of two analyzers: T1.

## T1 — a coherent dialect analysis module

**Files:** `src/SqlHarness.Core/Dialect/ISqlDialect.cs`, `Dialect/SqlServerDialect.cs`, `Postgres/PostgresDialect.cs`, `SqlSafety.cs`, `Postgres/PostgresSafetyClassifier.cs`, `Postgres/PostgresBenchmark.cs`; testy SqlSafetyTests, PostgresSafetyTests, SqlParameterReferenceValidatorTests, PostgresBenchmarkTests.

**Interfaces:** the parameter contract from 02/T1 remains the single call. The analysis result must carry the classification, the parameters, the local objects and the benchmark capability; settle the detailed type with the actual consumers, not as an unnecessary public API.

- [ ] Compile a list of production uses of each ISqlDialect member; remove the unused IdentitySql and CollectSessionTempTables or move them to their only actual owner. A lack of consumers means removal, not a new call made only to preserve the interface.
- [ ] Extract a shared parsed-document/analysis inside each engine so that Classify/ValidateMeasuredBatch/parameter references do not implement different CTE/INTO rules. Do not impose a single AST on both libraries.
- [ ] Tabular tests of the same SQL in query/setup/benchmark: differences only those resulting from the mode contract. Keep all 01/02 regressions and the tests of a valid SELECT with T-SQL hints/functions.
- [ ] Run the indicated tests; local commit `refactor: consolidate dialect analysis responsibilities`.

## T2 — facade and operation lifecycle

**Files:** `src/SqlHarness.Core/SqlHarnessModule.cs`, `CompareCellRunner.cs`, `WatchRunner.cs`, `SnapshotRunner.cs`, `Contracts.cs`, `SqlExecution.cs`; nowe `BenchmarkRunner.cs`, `BenchmarkCollector.cs`, `OperationFailureMapper.cs`; testy ContractsTests, BenchmarkRunTests, CompareTests, WatchTests, SnapshotTests.

**Interfaces:** keep `ISqlHarnessModule.ExecuteAsync(SqlHarnessOperation, CancellationToken) -> Task<SqlHarnessOutcome>`. The mapper takes the exception and the phase, returns an error/exit code per 03/T1; it does not map based on text. The session Identity is getter-only in the target design, assigned in the factory before it is passed to the consumer.

- [ ] Move BenchmarkRunner/Collector into the files responsible for them; remove the runners' dependency on static facade helpers by moving the helpers to the owner of execution/reporting.
- [ ] Separate the preparation and execution of operation families only where a repeatable lifecycle exists; do not create a class every few lines. The facade stays dispatch/composition.
- [ ] Unify exception mapping across query/watch/snapshot/compare, preserving the difference between auth/SQL/local storage and the primary cause on a cleanup error.
- [ ] Evaluate replacing object? Report with an internal typed result. The tests must make it impossible to combine an operation with a mismatched report; do not break the public API without versioning. If the benefit requires a wide migration, keep a compatible adapter and record an ADR of the scope.
- [ ] Add tests for a read-only Identity, setup once, error receipt, a partial matrix and a cleanup error. Run the indicated classes and the complete offline set; local commit.

## T3 — shared artifact publication

**Files:** `src/SqlHarness.Core/Artifacts.cs`, `QueryStoreArtifacts.cs`, `IndexAnalysisArtifacts.cs`; new `ArtifactDirectoryPublisher.cs`; tests ArtifactWriterTests, QueryStoreArtifactWriterTests, IndexAnalysisArtifactWriterTests. SnapshotStore/GainStore do not automatically move to this contract.

**Interfaces:** keep the domain writers' Write(...). The internal publisher manages root/staging/publish/cleanup; the domain writer is responsible for the content and for matching sensitive data to the report.

- [ ] Add a shared failure injection matrix: first/subsequent write, move, cleanup, collision, concurrent writes. Do not publish a partial final directory or a success path before completion.
- [ ] Move the duplicated path generation, staging and cleanup into the publisher; paths must stay within a defined root, and identifiers must not expose parameters.
- [ ] Keep the separate validations of Query Store text matching, index filters and plan/param-set protection. Do not treat an arbitrary report object as safe to write.
- [ ] Run the three writer classes, the module error tests and the complete offline set; local commit. The review report must show the removed duplications and the absence of any change to public content.
