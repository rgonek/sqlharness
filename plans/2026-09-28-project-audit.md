# SQLHarness audit — 2026-09-28

Code state: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`. The audit covers the public CLI and MCP, Core contracts, SQL Server/PostgreSQL classification, files and artifacts, execution restrictions, tests and distribution. Application sources were not changed. This document is a report and a proposal for the order of work, not an implementation or an approved change plan.

## Assessment

The CLI/MCP → Core → dialect/session split is fundamentally correct. There is no reason to remove one transport or to merge commands that perform different tasks. The main problems concern the MCP boundaries: leakage through logging, the lack of linking an artifact read to a target, path control and incomplete serialization of DB operations. Further expansion of the API surface should follow their repair.

## Verification and limitations

- `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal`: **exit 1**. Core/CLI: **1895 passed**. MCP: **122 passed, 2 failed, 4 skipped**.
- MCP failures: `Progress_fires_only_for_a_client_token_without_sql_payload` — 15 s timeout while waiting for the notification; `Published_single_file_server_passes_the_stdio_smoke_on_this_rid` — publish timeout after about 6 minutes, in `PublishAsync`, before evidence that the published binary works.
- Re-running the progress test alone via `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter FullyQualifiedName~Progress_fires_only_for_a_client_token_without_sql_payload --verbosity minimal`: **1 passed**, exit 0. This does not invalidate the failure of the whole suite and does not prove the cause of the timeout.
- `dotnet list SqlHarness.sln package --vulnerable --include-transitive`: exit 0, the NuGet source reported no vulnerable packages in any of the five projects. This is not proof of the absence of all vulnerabilities.
- MCP process attempts: real handshake and tools from the current build, only synthetic profiles and files in automatically cleaned temporary directories. No DB connections and no reads of real user artifacts.
- The attempts confirmed SQL and a parameter value in stderr, a read of another target's report, rejection of a file under a root with a trailing separator, no `outputSchema` for any of the 11 tools, and the offline validator's behavior.
- No live SQL Server/PostgreSQL was run, no TLS evidence, no tests on Linux/macOS and no load of millions of rows. The findings about the complexity and semantics of Unix paths come from the code; they are not a measurement of those platforms.
- The review covered the main boundaries and flows of each project. It is not a formal verification of the whole AST, of all authentication provider combinations or of every SQL statement.

## Priorities

P1: safety or the basic resource contract. P2: public API correctness and usability. P3: maintenance. Effort: S — hours, M — about a day, L — several days, including tests. Risk means the risk of the fix.

| ID | Priority | Finding | Effort | Risk | Confidence |
|---|---|---|---|---|---|
| S1 | P1 | The MCP logger writes SQL and parameters to stderr | S | low | high, reproduced |
| S2 | P1 | The MCP artifact read does not enforce the target scope | M | medium | high, reproduced |
| S3 | P1 | The input-root check ignores letter case also on Unix | M | medium | high from code; no Unix attempt |
| R1 | P1 | `inspect` bypasses the single-operation DB lock | S/M | medium | high from code |
| R2 | P1 | Result comparison has a cost dependent on repeat² and does not observe cancellation | L | high | high from code; no load measurement |
| A1 | P2 | An input-root trailing separator blocks valid files | S | low | high, reproduced |
| A2 | P2 | `validate.usage` is a mandatory piece of information with no effect on validation | M | medium | high, reproduced |
| A3 | P2 | The defined response schema does not reach tools/list | S/M | low | high, reproduced |
| A4 | P2 | The PostgreSQL safety boundary is documented more deeply than the public MCP promise | M | medium | high from code and policy |
| T1 | P2 | The full MCP test set does not pass | M | low | high for the observation; cause undetermined |
| D1 | P3 | Two watch loops and duplicated adapter rules increase the cost of change | M/L | medium | high from code |

## S1 — remove protocol content from the MCP logger

Evidence: `src/SqlHarness.Mcp/McpHost.cs:131` enables all levels except None; `:143` prints the `formatter(state, exception)` supplied by the SDK. The comment about a safe template does not match the behavior: the formatter already returns formatted data.

In a real MCP process a synthetic marker was sent inside SQL and, in a separate offline validate call, a synthetic parameter value. Both markers ended up in stderr. In the first attempt the rejected arguments also reached the log. There is no need to open a DB connection for the input to be disclosed.

Impact: the MCP client log may persist SQL, parameters and other data passed by the protocol despite response sanitization. No leak of real user secrets was found — the attempts used only markers.

Fix: own constant messages and a whitelist of event metadata, without the arbitrary SDK formatter/payload. The level filter alone should not be the only protection. The process test must check stderr after a correct and a rejected tools/call, with a parameter and SQL; the existing startup and response adapter tests do not replace this.

## S2 — bind artifacts to the MCP scope

Evidence: `src/SqlHarness.Mcp/McpOperationMapper.cs:457` accepts scope, but apart from a null check does not use it for authorization. `:465` reads from the global `SqlHarnessPaths.CompareDir`; `src/SqlHarness.Core/SqlHarnessPaths.cs:12` is a shared path under SQLHARNESS_HOME. The manifest in `src/SqlHarness.Core/ArtifactReader.cs:15` has no owner/scope. The specification `docs/superpowers/specs/2026-09-26-mcp-adapter-design.md:60` requires linking the ID with the scope.

Attempt: a process assigned to the synthetic `audit` database received the ID of a synthetic report with target `other-db`; `artifact(summary)` returned a foreign target's report with `isError=false`.

Impact: a client that knows the ID can read metrics, object names and identification of another target available in the same local store. Restricting to safe sections reduces the scope of disclosure but does not enforce the declared profile isolation. There is no evidence here of reading an arbitrary system file.

Fix: an explicit owner identifier covering the profile and the approved set of variables/targets, assignment at write time and a check before projection; alternatively a register of IDs issued to a specific scope, in line with the chosen persistence policy. A directory name or comparing the DB name alone is not sufficient. Define the migration of old artifacts and the rejection of those without owner evidence. Also check the global namespace of snapshots: `SnapshotDocument` does not store the target, and `SnapshotStore` loads by name (`SnapshotStore.cs:9,156`); comparing across profiles may be intentional in the CLI, but it requires a separate decision for MCP.

## S3 and A1 — fix input-root semantics

Evidence: `src/SqlHarness.Mcp/McpInputReader.cs:235` compares paths via `OrdinalIgnoreCase` and unconditionally appends a separator (`:239–240`). `src/SqlHarness.Mcp/McpScope.cs:161` performs GetFullPath, but does not remove a trailing separator.

S3: on a case-sensitive system, two different directories whose names differ only by case are treated as the same allowed root. This may admit input from a directory outside the approved scope. It concerns files reachable by the process; it does not automatically mean listing their entire content. The assessment comes from an explicit StringComparison in the code, without a live Linux attempt.

A1: on Windows it was confirmed that a valid file inside a directory is rejected after starting with `--input-root` ending in a separator. A double-separator comparison results. The drive root requires the same attention.

Fix: one normalization function and path-provenance check, with correct per-platform semantics, preservation of root directories and the existing link checks. Tests: root with/without a separator, drive root, a directory with a similar prefix, two directories differing by case on Unix, links and parents that are links. Before/after checks based on length and mtime are not handle identity — resistance to deliberate substitution by a concurrent local writer remains a separate area to verify, not demonstrated in this audit by an exploit.

## R1 — put inspect under the DB lock

Evidence: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:86` routes inspect to RunAsync; `:333` does not acquire the lock. RunDbAsync acquires it only at `:393`. MapInspect creates real ping/schema/counts/space/qstop/indexes operations (`McpOperationMapper.cs:152`). `McpExecutionGate.cs:57` and test `McpLifecycleTests.cs:129` explicitly codify the list of five operations without inspect.

Impact: a concurrent inspect, including exact counts, can load the DB during a benchmark; many inspects are not subject to the busy rule. The token deadline is also passed in RunAsync — the problem is not a total absence of a timeout.

This is a design inconsistency: the comment classifies inspect as discovery, but the public contract speaks of a single DB/process operation (`docs/mcp.md:77`). Fix: put inspect under the shared lock, leaving capabilities/validate/plan/artifact as offline operations. Test query↔inspect and inspect↔inspect, instead of only a list of five tool names.

## R2 — limit the total equivalence cost

Evidence: `src/SqlHarness.Core/CompareCellRunner.cs:179` keeps all repetitions of both variants; `:197` runs ResultComparer without a token. `src/SqlHarness.Core/ResultEquivalence.cs:57` compares the product of all baseline/candidate. `:135` builds a frequency dictionary for the pair. The one-million-fingerprint limit concerns a single run (`:191`), and repeat can be 100 (`CompareCellRunner.cs:60`).

Impact: with the allowed limits, up to 200 million fingerprints in memory and 10 thousand pair comparisons. The complexity of the main phase is O(repeat² × rows); the MCP deadline does not interrupt a synchronous loop that does not check the token. This is an analysis of the limits, not an executed load test.

Fix: a budget for the whole operation and explicit cancellation checks, a more compact fingerprint representation, reuse of histograms and recognition of identical runs. Preserve the existing maximum directional counts and the share of all repetitions; do not replace equivalence evidence with a truncated result. Characteristic tests must precede algorithm optimization.

## A2 — give validate.usage real meaning or remove the apparent choice

Evidence: `src/SqlHarness.Mcp/McpOperationMapper.cs:284–299` checks the usage enum, then always calls the same SqlValidation.Validate. `src/SqlHarness.Core/SqlValidation.cs:40` always uses SqlUsage.Query. The PostgreSQL shape check of the measured SQL is separate (`src/SqlHarness.Core/Postgres/PostgresBenchmark.cs:88`).

Attempt: two SELECTs in one batch with MCP `usage=benchmark` for PG receive `allowed=true`, although running a benchmark requires exactly one statement. This is not a bypass of execution-time validation: measure re-checks the SQL and will reject it. It is a redundant parameter and a misleading preflight; the documentation honestly admits that usage does not change the classifier.

Fix: a shared Core analysis that takes the mode and, when needed, setup/parameter context; expose the same contract in the CLI. Alternatively withdraw usage and call the result only a query classification. The absence of mutations after validate and the absence of an offline permissions check should remain explicit.

## A3 — publish the MCP response schema

Evidence: `src/SqlHarness.Mcp/McpResultAdapter.cs:107` defines OutputSchema, but `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:490` does not assign it to the registered tools. The real tools/list did not return outputSchema for any of the 11 tools.

Impact: the client gets structuredContent without a discoverable schema; it must know the format from the documentation. The local ValidateEnvelope is not a publication of the contract. This is an API design gap, not evidence of a protocol violation: outputSchema is an optional capability.

Fix: wire a versioned envelope schema into the registration; later narrow result to the kind of tool/operation. Test the real tools/list and success/error/controlled-outcome responses. Do not generate a second, divergent catalog of descriptions.

## A4 — expose the real read-only boundary in the public API

`docs/superpowers/specs/2026-09-26-postgres-safety-policy.md:56,69–77` explicitly documents the acceptance of functions with invisible effects and the absence of READ ONLY transactions. The code implements this: `PostgresSafetyClassifier.cs:663` filters names, and `Postgres/NpgsqlSessionFactory.cs:94` passes the classified text without changing the role/transaction. I do not treat this deliberate decision as a newly discovered bypass.

However, the public description of the MCP query speaks of read-only and of a persistent mutation that is always disabled (`McpToolCatalog.cs:507`), without explaining this boundary. An agent should not interpret `allowed=true` or `classification=read-only` as a guarantee of no effects of functions/views/operators.

Fix: capabilities and the validate result should distinguish the classification of effects visible in the text from unverified permissions/hidden effects. The startup documentation should link to this policy. An optional stricter profile requires a separate design and DB tests; do not automatically add a transaction that breaks TEMP setup and still does not close all external effects.

## T1 — recover a trustworthy MCP gate

Evidence and results are given in the verification section. Specific locations: `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs:106,545`, `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs:111,364`.

Fix: diagnose the progress race/lost delivery and the publish/restore stage separately. Preserve the stage logs and correct process cleanup after a timeout; do not increase the time without establishing the cause. Do not declare a green gate on the basis of a single progress repetition. The lack of evidence of a production server failure follows from the distribution timeout occurring during publication.

## Interfaces: what to keep, what to consolidate

| Boundary | Assessment |
|---|---|
| CLI and MCP | Two justified adapters onto a shared Core; they do not duplicate the product goal. The CLI handles shell/files/NDJSON, MCP a frozen scope and the protocol. |
| query / measure / compare | Different semantics: execution, repeated measurement, comparison evidence. Keep them separate; share preparation and execution. |
| measure param-set / compare matrix | Do not merge: one session/setup for param-set, a new session per matrix cell; different interpretation of stability and equivalence. |
| inspect / query | Fixed catalog queries limit the scope and simplify discovery. Keep inspect, fix the gate and the description of arguments dependent on kind. |
| schema / space / indexes | Partially shared index data, but different questions: structure, size, optimization potential. These are not redundant duplicates. |
| ISqlReader / ISqlSession / ISqlSessionFactory | Good provider, lifecycle and test boundaries. No interface is visible that could be removed at no cost as a duplicate of another. |
| ISqlDialect | Connects parsing, parameters, benchmark and catalog SQL. Broad, but the two engines actually benefit from a consistent selection point; split only on a concrete consumer need, without multiplying interfaces for each method. |
| ICompareArtifactWriter / IQueryStoreArtifactWriter / IIndexAnalysisArtifactWriter | Different data types and confidentiality rules; the shared ArtifactDirectoryPublisher already eliminates mechanical publishing copies. Keep. |
| ISqlHarnessModule / object Report | The ADR deliberately preserves compatibility; OperationReportContract checks operation–report pairs. A full migration to generics/union is not a safety fix. |
| IWatchClock / IMcpClock | They have a similar UtcNow, but the first manages delay and a monotonic budget, the second progress throttling. The similarity alone does not justify coupling the layers. |

Sources: `Contracts.cs:6`, `Dialect/ISqlDialect.cs:3`, `SqlExecution.cs:17,29,133`, `Artifacts.cs:318`, `QueryStoreArtifacts.cs:6`, `IndexAnalysisArtifacts.cs:6`, `OperationReportContract.cs:13`, `WatchRunner.cs:7`, `McpExecutionGate.cs:14`; ADR `docs/superpowers/plans/2026-09-26-audit-05-architecture-adr-t2-typed-result.md`.

D1: the real duplication is in the implementation. `WatchRunner.cs:69,269` contains two loops (`:122,331`) with the same budget, read, stop condition and delay for the report and NDJSON. Consider one loop engine and two event sinks. Additionally McpOperationMapper repeats the limit numbers and time parsing (`:540,579,603`), capabilities describes the limits separately (`Capabilities.cs:59`), and the registration/DTO constitute yet another description of the contract. Consolidate the pure Core rules and metadata, the transport specifics in the adapter. First tests of the conformance of the existing modes; without a demonstrated regression this is P3.

## Does SQLHarness block too much?

Yes, but the causes differ and require different actions.

| Case | Result and assessment |
|---|---|
| A valid file in a root ending with a separator | Reproduced A1 bug; fix it without weakening the policy. |
| T-SQL scalar DECLARE with an initializer | Attempt accepted. |
| T-SQL SET of a scalar variable after DECLARE | Attempt rejected as unsupported_statement; a candidate for a narrow extension. Analyze the RHS and the assignment type. |
| T-SQL table variable | Attempt rejected; a deliberate current boundary (`SqlSafety.cs:272`), a sensible extension for comparing table variable vs #temp after handling the whole lifecycle. |
| PG CREATE TEMP + TRUNCATE of that object | Attempt rejected as unsupported_statement; a candidate for an extension with control of all targets and options, not for an arbitrary table. |
| PG ANALYZE of a local TEMP | Attempt gives sql_parse_error; a known parser limitation, explicit in AGENTS.md. AST support is needed; not regex and not a safety bypass. |
| PG ordinary CTE with SELECT | Attempt accepted. |
| PG lo_/dblink/pg_advisory_ prefixes | A deliberate redundant name rejection documented in the policy; without resolving function identity, do not relax it in bulk. |
| Matrix MCP: an empty value or a comma in a value | Explicitly rejected in `McpOperationMapper.cs:492`; a consequence of encoding the JSON structure back into CLI syntax. A candidate for a shared typed matrix model in Core, keeping the CLI parser at the edge. |
| Persistent DDL, dynamic SQL, cross-database, mutation through MCP | Justified boundaries of the current product; I do not recommend a global switch disabling validation. |

## Development directions

1. **Preflight matching the future execution.** Expand validate with a shared query/setup/benchmark mode and setup context, report the rejection rule and the safe location of the AST. It resolves A2 and limits trial DB calls. Cost M/L; the risk is divergence from the real execution, therefore the preparer must be shared.
2. **Typed Core inputs for parameters and matrix.** MCP already has structures, but serializes them into text declarations (`McpOperationMapper.cs:475,492`, `Contracts.cs:49`). This allows handling values with commas without creating a second validator. Cost M/L; preserving CLI compatibility and redaction is required.
3. **A regression decision useful in CI.** A specification exists: `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`; implement its pass/fail/inconclusive over the metrics and equivalence. Do not equate a successful command run with the absence of a regression. Cost L, requires rules about noise and missing metrics.
4. **PostgreSQL diagnostics via pg_stat_statements.** A specification exists: `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`; it complements the qstop asymmetry. Preserve the absence of CREATE EXTENSION and the snapshot/delta/reset semantics, without promising an artificial time window. Cost L, live tests only on an agreed target.

## Considered and rejected as new bugs

- I do not report again the historical OUTPUT INTO, the PostgreSQL wrong parameters or the double counting of buffers without current regression evidence.
- The known incompleteness of the PostgreSQL static read-only proof is a deliberate policy; the new A4 finding concerns the visibility of this boundary in the public contract.
- The two MCP representations (text and structuredContent) are not two redundant APIs; the adapter takes their combined size on the wire into account.
- validate can correctly end the analysis with a success envelope and result.allowed=false; that is not the same as executing rejected SQL.
- `object Report` has a documented compatibility decision and type checking; on its own I do not qualify it as a bug.
- The absence of mutations and force snapshot in MCP is an approved v1 restriction.
- The absence of live testing is not evidence that the endpoint/TLS are defective; a publication test that got stuck before the smoke test does not prove a server failure.

## Installation observation

`Get-Command sqlharness` pointed to `C:\Users\rgone\.local\bin\sqlharness.exe`. Its `--help` showed only an older command set (without capabilities, validate and mcp), while the current repository build runs MCP. This is a divergence of the local installation and the repository, not a finding about the quality of the new code. Neither the binary nor the user configuration was updated.

## Order of further work

First S1, S2 and S3/A1; then R1 and A3 plus the T1 diagnosis. R2 requires separate characteristic tests before changing the data representation. A2/A4 should establish a shared contract before the D1 refactoring and the syntax extensions. The new CI/PG features already have intent documents — they must be reconciled with the current state instead of writing competing specifications.

2026-09-29 update: all findings, restrictions and development directions have been assigned to 15 plans in the [plan index](README.md). The status of all of them: TODO. The CI and PostgreSQL feature stages begin with refining the existing specifications; recording plans does not mean implementation.
