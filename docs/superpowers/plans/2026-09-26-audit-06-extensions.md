# Safe extensions and follow-up design Implementation Plan

**Status (2026-10-08):** DONE — T1 b3b2d06/814267c/5b3ddd9/5ed9970/141f497/0d98173 (further in `plans/011`), T2 845570c, T3 e268c38, T4 50ba1db/e363e3b (specification only; implementation = `plans/035`), T5 be70c8f (specification only; implementation = `plans/037`). The checkboxes below were not ticked during execution.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce excessive blocking and prepare features that shorten agent work without implicitly broadening permissions.

**Architecture:** Use the corrected safety, response and resource boundaries. T1–T3 implementations have concrete contracts; T4–T5 deliver specifications first, because the CI thresholds and pg_stat_statements require separate product decisions.

**Tech Stack:** .NET 8, ScriptDom, SqlParserCS, Spectre.Console.Cli, PostgreSQL, xUnit.

**Spec:** [audit B4 and feature proposals](../specs/2026-09-26-project-audit.md), [roadmap](2026-09-26-audit-roadmap.md).

## Global Constraints

- T1 after 01 and 02/T1; T2 after 03/T1–T3 and 05/T3; T3 after 04/T1–T2; T4 after 02/T3.
- No new unsafe bypass, persistent DDL or automatic creation of PostgreSQL extensions.
- New syntax is supported only when the analysis covers all effects; parse failure remains a rejection.
- Plans and SQL text do not become public automatically through the artifact command.
- Do not merge Query Store semantics with pg_stat_statements under a single identical report without describing the differences.

## Review Focus

- DECLARE initializer and EXPLAIN ANALYZE with side effects: T1.
- ALTER/TRUNCATE of a target other than a real temp: T1.
- Path traversal, symlink and confidential artifact: T2.
- Process termination in the middle of the NDJSON stream: T3.
- Unstable result/unavailable metric and statistics reset: T4–T5.

## T1 — safe syntax extensions

**Files:** `src/SqlHarness.Core/SqlSafety.cs`, `Postgres/PostgresSafetyClassifier.cs`, `Postgres/PostgresBenchmark.cs`; tests SqlSafetyTests, PostgresSafetyTests; `AGENTS.md`, `README.md`.

**Interfaces:** existing Classify and diagnostics from 03/T1; in the spec keep a list of exactly supported constructs, without a global "allow all DDL on temp".

- [ ] T-SQL: allow scalar DECLARE and SELECT in a query with initializer analysis; TRUNCATE only of an unambiguously local #temp; ALTER TABLE #temp only with ADD/DROP COLUMN and supported local constraints. Every variant has a positive test and a persistent/cross-database negative test. Do not allow EXEC/dynamic SQL through a declaration.
- [ ] PostgreSQL: consider EXPLAIN without ANALYZE over a safe SELECT, and SELECT INTO TEMP with unambiguous locality. EXPLAIN ANALYZE executes SQL — it is subject to full effect analysis and is not automatically a read.
- [ ] ANALYZE only of an established session table: check parser support. If it is missing, evaluate a library update with full regression; do not replace the parser with a regex. Record a concrete blocker if there is no safe path.
- [ ] Resolve lo_ user-function handling with the identity/effect policy from 01/T3; do not remove the prefix block without a replacement protection.
- [ ] Run the tests of both classifiers; authorized live tests check the real temp and the absence of persistent effects. Describe implemented capabilities in capabilities; local commits per construct.

## T2 — selective reading of existing artifacts

**Files:** new `src/SqlHarness.Core/ArtifactReader.cs`, `src/SqlHarness.Cli/Commands/ArtifactCommand.cs`, `tests/SqlHarness.Tests/ArtifactReaderTests.cs`, `tests/SqlHarness.Tests/Cli/ArtifactCommandTests.cs`; CLI registration and capabilities.

**Interfaces:** planned command `artifact <id> --section summary|metrics|operators --json` or --output agent; an identifier from the manifest, not an arbitrary path. Do not add reading of queries.jsonl, raw plan XML, snapshot cells or source SQL in the first version.

- [ ] Write a versioned artifact manifest and a mapping to safe sections. Define an error for an old artifact without a manifest; no guessing of confidentiality after an extension.
- [ ] Tests for traversal, a symlink/reparse point leaving the root, an unknown id/section, a damaged manifest and an oversized file. Rejection without disclosing content and without a DB connection.
- [ ] Implement the read limit and the budgeted projection from 03/T3; an operator identified by identifier may be indicated without loading all run arrays into the result.
- [ ] Test the compare→artifact workflow: no re-benchmark and the same metric as in the saved report. Run the new tests and writer tests; local commit.

## T3 — optional watch events

**Files:** `src/SqlHarness.Core/WatchRunner.cs`, `Contracts.cs`; `src/SqlHarness.Cli/Commands/WatchCommand.cs`, `Commands/Renderer.cs`; `tests/SqlHarness.Tests/WatchTests.cs`, `Cli/AgentOutputTests.cs`.

**Interfaces:** `watch --output ndjson` separate from agent/json/json-summary. Records `started`, `changed`, `completed`, `failed`, each with schemaVersion, sequence and elapsed; data state is subject to the same redaction and budgets.

- [ ] Define a single terminal record and the sequence order. Do not generate changed for an unchanged result. An empty transport interruption must not be interpreted as success.
- [ ] Add tests for flush after an event, mid-run cancellation, a serialization error, a deadline and a slow consumer. Do not accumulate the full history before emission.
- [ ] Preserve the existing final JSON for the current flags; announce NDJSON explicitly in capabilities. Gain accounting covers the entire stream.
- [ ] Run the watch/CLI tests, local commit.

## T4 — specification of CI regression thresholds

**Files:** new `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`; point to future implementation files in that specification, do not create stubs.

**Interfaces:** the result of this task is an approvable decision matrix, not a new unagreed exit code.

- [ ] Define the metrics, relative threshold and minimum absolute difference, zero baseline, missing PG CPU metric, unstable trial and absence of equivalence. The domain pass/fail/inconclusive result must be separate from SQL success.
- [ ] Write a table of synthetic cases and expected decisions, the measurement noise policy, repetition counts and threshold versioning.
- [ ] Propose CI integration without redefining exit 8; agree on a new mode or a separate command. Do not claim a regression if the result is technically unequal or the measurement is incomplete.
- [ ] Finish the task with a document containing criteria and a separate implementation plan. Commit `docs: define benchmark regression decision contract`.

## T5 — pg_stat_statements specification

**Files:** new `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`; `docs/superpowers/plans/2026-09-26-audit-roadmap.md` status update of the subordinate project.

**Interfaces:** specification only for the new read-only diagnostics. SQL Server qstop preserves its current contract.

- [ ] Examine the official PostgreSQL 14+ documentation for extension availability, column versions, permissions, resets, database scope and SQL text visibility. Missing extension → explicit unavailability, without CREATE EXTENSION.
- [ ] Establish ranking, query identification, metrics, snapshot/delta and the absence of a 24h window promise from a cumulative counter alone. A reset/restart must invalidate an incomparable delta.
- [ ] Write a confidentiality contract analogous to Query Store: metrics/identifiers on stdout, SQL only in a locally sensitive artifact, no publication without review.
- [ ] Deliver fixtures for a missing extension/permissions, an empty result, a reset and two server versions; prepare a separate implementation plan. Do not test live without an authorized target.
- [ ] Commit the documentation and the mapping in capabilities only after actual implementation.
