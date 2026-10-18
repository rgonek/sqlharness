# Agent interface and token economy Implementation Plan

**Status (2026-10-08):** DONE — T1 8f83448/bd1d72d, T2 c798c1a/0d67946, T3 eb72d97/6a9eb8b/91fa861, T4 ca51814/b5daea0/4c3a1d8/329df03. The checkboxes below were not ticked off during implementation.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the agent a predictable format, short responses and the ability to recognize an error without further attempts against the database.

**Architecture:** A stable error contract, an explicitly versioned agent response mode and selective projections. Offline validation uses the same Core logic as execution.

**Tech Stack:** .NET 8, Spectre.Console.Cli, System.Text.Json, xUnit.

**Spec:** [audit A1–A4 and proposals 1–4](../specs/2026-09-26-project-audit.md), [roadmap](2026-09-26-audit-roadmap.md).

## Global Constraints

- Redaction 01/T2 first; validate requires 01 and 02/T1.
- Do not change the shape of the existing --json successes without notice. The new success envelope is opt-in via `--output agent`; the existing --json/--json-summary receive structured errors.
- `--output agent`, `--json` and `--json-summary` are mutually exclusive. Process codes remain compliant.
- An error does not contain SQL, parameter values, tokens or a connection string. No automatic retry of mutations.
- The presentation limit does not affect the data for equivalence nor the snapshot completeness declaration.

## Review Focus

- A CLI parser error before Dispatch and an unknown option: T1.
- Simultaneous report and error with a partially completed matrix: T1.
- An outdated binary, no DB and no configured profiles: T2.
- Multibyte Unicode and a single huge cell: T3.
- A negative saving and an old gain file: T4.

## T1 — versioned result and safe errors

**Files:** `src/SqlHarness.Core/Contracts.cs`, `SqlHarnessModule.cs`; nowy `src/SqlHarness.Core/SqlHarnessError.cs`; `src/SqlHarness.Cli/SqlHarnessCli.cs`, `Commands/SqlHarnessCommands.cs`, `Commands/Renderer.cs`, `Infrastructure/OutputContext.cs`; new `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`; `tests/SqlHarness.Tests/Cli/CommandTests.cs`.

**Interfaces:** record the public format in the new `docs/superpowers/specs/2026-09-26-agent-output-contract.md`: the envelope `schemaVersion`, `command`, `status`, `exitCode`, `result`, `error`, `truncation`. Error: `code`, `phase`, `message`, optional safe `hint` and `location`. The cause code is a stable string, not an exception name.

- [ ] Add tests parsing stdout through JsonDocument for a safety rejection, a bad argument, a missing file, auth, target mismatch, SQL error and local storage. Stdout in machine mode has exactly one JSON document.
- [ ] Add a partial matrix test: preserved completed cells and an error message; the renderer must not lose the SafeError because Report != null.
- [ ] Implement the code mapping before rendering and Spectre error handling before Dispatch. Codes do not come from parsing the exception.Message text. Keep raw help/version as special, explicitly described operations.
- [ ] Keep the old --json successes compatible; --output agent gives the new envelope, a compact record and the metric availability metadata from 02/T3. For errors, --json and --json-summary return JSON with the same error object.
- [ ] Run `dotnet test --filter 'FullyQualifiedName~Cli'`; check flag conflict, stderr/stdout, absence of sensitive values and numeric exit codes. Local commit `feat: add consistent machine-readable outcomes`.

## T2 — capability discovery and offline validation

**Files:** new `src/SqlHarness.Cli/Commands/CapabilitiesCommand.cs`, `DoctorCommand.cs`, `ValidateCommand.cs`; `SqlHarnessCli.cs`, `Commands/HelpProvider.cs`; new `src/SqlHarness.Core/Capabilities.cs`, `SqlValidation.cs`; new `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`, `DoctorCommandTests.cs`, `ValidateCommandTests.cs`; `README.md`, `AGENTS.md`.

**Interfaces:** `capabilities --json` describes build/version/contractVersion/commands/engines/limits; `doctor --json` is local; `validate [profile] --file ... --param ... --json` uses a closed profile and opens no connection. In this version validate does not add a new --engine path beyond the existing unsafe-direct.

- [ ] Establish with tests that all three commands do not perform ConnectAsync; doctor does not fetch a token, does not display env and does not update the installation.
- [ ] In capabilities state the qstop/indexes restrictions per engine, parameter types, limits and result modes. Do not list database names, accounts or profile variable values. The build id allows comparing the binary with the repo.
- [ ] Validate returns the classification, the required parameters, the reason for rejection, the available AST locations and the fact of non-execution. Without copying SQL/literals. Permission requirements or the existence of objects mean unknown, instead of guessing.
- [ ] Check the consistency of validate with the execution preflight on the same SQL Server/PG fixtures; the validate result is not an authorization ticket for a later mutation.
- [ ] Add short command descriptions to the main help and a concise startup path to AGENTS.md with references to the details. Remove the empty HelpProvider only if it is still unused.
- [ ] Run the three new test classes and Cli.CommandTests; record a local commit.

## T3 — response budget

**Files:** new `src/SqlHarness.Core/AgentOutputProjection.cs`; `BenchmarkSummary.cs`; `src/SqlHarness.Cli/Commands/Renderer.cs`, `SqlHarnessCommands.cs`, `Infrastructure/OutputCaptureWriter.cs`; `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`, `BenchmarkSummaryTests.cs`.

**Interfaces:** --output agent: by default 16 KiB UTF-8 for the whole envelope; `--max-output-bytes` 4096..1048576, `--max-cell-chars` default 512, range 0..4096. These options apply only in agent mode and have an entry in capabilities. These are new proposed values, not the limits of the historical --json.

- [ ] Define in the spec the order of data preservation: status/error, correctness/equivalence, metric availability, primary metrics, the artifact and omission counters, then the details.
- [ ] Test 1000 tables, many sets/matrix cells, a long warning, a huge cell, Unicode, a long artifact path: valid JSON fits the budget including the newline; truncation explains what was omitted. If the minimal envelope does not fit, return a small structured error, not truncated JSON bytes.
- [ ] Project a bounded structure before serialization; do not build a full multi-megabyte JSON only to truncate it. For operators keep the limit of 10 and the warning priority.
- [ ] Do not automatically create full query artifacts containing data just because the response is short. Indicate only artifacts actually written; omitted query results carry an explicit note that no full file exists.
- [ ] Check that an identical raw result has the same hash/equivalence regardless of the presentation budget. The output counter counts bytes without storing the whole copy of the text (coordination with 04/T3).
- [ ] Run AgentOutputTests and BenchmarkSummaryTests; record byte measurements, without claiming real tokens; local commit.

## T4 — honest gain and agent scenarios

**Files:** `src/SqlHarness.Core/GainStore.cs`, `CanonicalResults.cs`, `SqlHarnessModule.cs`; renderer; `tests/SqlHarness.Tests/GainStoreTests.cs`, `CanonicalResultsTests.cs`; new `tests/SqlHarness.Tests/AgentWorkflowTests.cs`.

**Interfaces:** keep the historical SavedEstimatedTokens; add a signed `NetEstimatedTokens = rawEstimatedTokens - emittedEstimatedTokens` and `EstimationMethod = utf8-bytes-div-4`. Read old gain records with the computed net, without rewriting user data.

- [ ] Test raw=100, emitted=200 gives net=-100; the aggregate accounts for the loss, and the old gross saved field still stays non-negative. Describe the baseline as the canonical raw, not the actual cost of an alternative tool.
- [ ] Add controlled scenarios without a DB: capability discovery→schema, rejection→validate→corrected input, compare→short result→artifact detail per 06/T2. Count the calls and the bytes.
- [ ] Archive the fixtures and byte budgets as regressions; do not make marketing-style comparisons of LLM cost without a tokenizer/model and a real baseline.
- [ ] Run GainStoreTests and AgentWorkflowTests, record a local commit and a short before/after comparison report.
