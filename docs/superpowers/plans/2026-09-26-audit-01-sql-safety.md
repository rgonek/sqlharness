# SQL safety audit remediation Implementation Plan

**Status (2026-10-08):** DONE — T1 f75a25d/07bbb0f/0cfd7a4, T2 c900888, T3 044c0c7/bcb9e69/222ca22/bba0a9d, T4 4e31382. The checkboxes below were not ticked off during implementation.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the detection of persistent writes, input redaction and target control; settle a workable policy for PostgreSQL functions.

**Architecture:** Fix the existing safety boundary before a larger refactoring. Core is the authority for the target and the permitted SQL effects; the CLI only forwards the request.

**Tech Stack:** .NET 8, ScriptDom, SqlParserCS, Npgsql, xUnit.

**Spec:** [audit S1–S4, S6 and target duplication](../specs/2026-09-26-project-audit.md), [program rules](2026-09-26-audit-roadmap.md).

## Global Constraints

- The shared roadmap rules apply; both engines remain supported.
- No implicit consent for mutations, persistent DDL, or arbitrary execution of a function considered read-only solely on the basis of its SELECT shape.
- S4 is an AST coverage gap to investigate, not a confirmed server vulnerability.
- The OUTPUT and redaction fixes do not wait for a rebuild of the whole module.

## Review Focus

- An alias named #temp pointing at a persistent table: T1.
- A second write target in OUTPUT and cross-database targets: T1.
- An invalid value from InnerException with a partially correct parameter list: T2.
- A function hidden in a view, an operator or a table source: T3.
- A direct call to the Core API bypassing CLI validation: T4.

## T1 — all SQL Server write targets

**Files:** `src/SqlHarness.Core/SqlSafety.cs`; `tests/SqlHarness.Tests/SqlSafetyTests.cs`, `QueryTests.cs`, `MeasureTests.cs`, `CompareTests.cs`.

**Interfaces:** keep `SqlSafetyClassifier.Classify(...) -> SqlSafetyDecision`; `HasMutation` must describe the entire batch, and `HasSessionLocalWork` the actual objects, not alias names.

- [ ] Add tests: `TempInsertWithPersistentOutputRequiresApproval`, `HashAliasOfPersistentTableRequiresApproval`, `AliasOfActualTempRemainsLocal`. Check UPDATE and DELETE, a quoted alias, an ambiguous alias, and INSERT/UPDATE/DELETE/MERGE with OUTPUT INTO.
- [ ] Run `dotnet test --filter 'FullyQualifiedName~SqlSafetyTests'`; the new persistent write cases must expose the current bug.
- [ ] Resolve the target against FROM/aliases and collect all write targets. Reject an ambiguous target. Persistent OUTPUT requires consent in query; in setup/measure/compare it stays disallowed. Cross-database stays disallowed even with consent.
- [ ] Check the full module path: rejection before ConnectAsync, no success artifact, correct exit 2. Consent and an exact database name admit only the allowed DML contract.
- [ ] Run the SqlSafetyTests, QueryTests, MeasureTests and CompareTests; record the results and make a local commit `fix: classify all SQL Server write targets`.

## T2 — redaction before parsing

**Files:** `src/SqlHarness.Core/SqlSafety.cs`, `SecretRedactor.cs`, `SqlHarnessModule.cs`, `MeasureParameterSets.cs`; `tests/SqlHarness.Tests/SecretRedactorTests.cs`, `SqlParameterParserTests.cs`, `QueryTests.cs`, `WatchTests.cs`, `SnapshotTests.cs`, `CompareTests.cs`, `MeasureTests.cs`.

**Interfaces:** keep `SecretRedactor.Redact(Exception, IReadOnlyList<string>) -> string`; a safe validation error must not inherit a value from the inner exception.

- [ ] Add tests with `n:int=private-audit-value`: the error result of none of the five commands contains the value or the full declaration; the parameter name and expected type remain usable.
- [ ] Extend the cases with decimal, date, GUID, Base64, Unicode, overlapping values and an error on the second parameter after a correct first one. Do not use real secrets.
- [ ] Separate diagnostic exceptions from public messages and gather the values for redaction before conversion; unify plain parameters, matrix and param-set. Do not publish the inner exception as user-facing text.
- [ ] Run the listed test classes with the `FullyQualifiedName~...` filter; check stdout and the stored error reports/artifacts.
- [ ] Record the result and make a local commit `fix: redact invalid parameter values before parsing`.

## T3 — threat model and PostgreSQL policy

**Files:** `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md` (nowy); `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`, `NpgsqlSessionFactory.cs`; `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`; new `tests/SqlHarness.Tests/Integration/PostgresSafetyIntegrationTests.cs` once the policy is settled.

**Interfaces:** policy specification first; do not add empty interfaces. The spec must define the Classify behavior and the scope of permitted session execution for Query and CompareSetup.

- [ ] Record the threat model: an agent with CLI access, existing functions/views/operator overloads, account permissions, search_path and name resolution. Separate the absence of persistent DML, the absence of administrative effects and the absence of external access.
- [ ] Define a matrix: set_config, pg_cancel_backend, pg_terminate_backend, advisory locks, sequence functions, a VOLATILE function performing DML, SECURITY DEFINER, a function in a view and a function named lo_ without effects. Each entry has an explicit allowed effect, an enforcement mechanism and a rejection message.
- [ ] Compare full AST analysis, trusted functions after overload resolution, minimal privileges and read-only transactions. Do not base the safety promise on the denylist alone nor on the transaction alone. Do not change database roles/permissions automatically.
- [ ] Add offline tests of AST recursion with CTE, INSERT source and CTAS; distinguish syntax accepted only by the library from syntax that is valid server-side. Do not label S4 as an exploit without a server-valid example.
- [ ] Complete the spec with the chosen policy, setup/temp compatibility and the effect on call counts. Only then spell out/implement the implementation; an unresolved trust boundary blocks the declaration "read-only guaranteed".
- [ ] On an authorized one-off database run the function and temp tests, including no effect for a rejected case. Keep only synthetic evidence. Separate the local specification commit from the policy implementation commit.

## T4 — identical target rules in Core and CLI

**Files:** `src/SqlHarness.Core/Targets/TargetResolver.cs`, `Contracts.cs`; `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`; `tests/SqlHarness.Tests/Targets/TargetResolverTests.cs`, `Cli/CommandTests.cs`.

**Interfaces:** `TargetResolver.Resolve(SqlTargetRequest, IReadOnlyDictionary<string, TargetProfile>) -> ResolvedTarget` remains the source of truth.

- [ ] Add a Core test: profile + Engine ends in a safety rejection, also for an unknown engine; without a connection. Cover profile + direct auth, direct + vars and an incomplete target.
- [ ] Take Engine into account when detecting the direct option. Do not ignore the passed value. The CLI may validate the format, but must not establish other safety rules.
- [ ] Run TargetResolverTests and Cli.CommandTests; update AGENTS.md/README only with actually implemented behavior, and record a local commit.
