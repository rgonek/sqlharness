# SQLHarness audit — 26.09.2026

**Audit archive:** stored in the repository as the basis for the [implementation plans](../plans/2026-09-26-audit-roadmap.md). Plans 01–06 derive from the audit; plan 07 MCP was added later at the user's request and will be implemented last. The results below describe the state as of the audit date, not the state after future fixes. Synthetic trials are preserved at the end of the document; the plans do not depend on files in a temporary directory.

Commit: `5e655942f4f99f6c84ae47a86ac210c19434b541`.
Scope: the public Core API, the CLI, both dialects, safety classification, parameters, connections and target identification, measurements, artifacts, agent-facing responses and their processing cost. Review of the code, the specifications and the history of the last 100 commits, tests and local reproductions. No queries were executed against databases. The project code remains unchanged.

## Assessment

The project has a sensible Core/CLI split, useful diagnostic commands and extensive tests, but **the declared read-only contract has gaps**. The priority items are write paths that are mistakenly treated as temporary table work, and PostgreSQL functions. Overly strict blocks also exist: in particular, the validation of PostgreSQL parameters through the T-SQL parser and the comparison of a DNS name with the server IP address.

The biggest architectural problem is not the number of interfaces. The rules for the same concept diverge across the module, the dialect, the validators and the CLI. From the agent's point of view, the biggest losses come from unstructured errors, the need to repeat correct queries, and the absence of a global response limit.

P1 = fix before relying on safety guarantees or measurement correctness. P2 = significant usability, resources and consistency. An offline classification confirmation does not mean a write was executed on a server; database permissions remain an additional boundary.

## Security findings

### S1 / P1 — SQL Server: OUTPUT INTO bypasses the persistent write check

Sources: `src/SqlHarness.Core/SqlSafety.cs:97`, `:167`, `:183`, `:302`.

The visitor detects `HasNonLocalOutputInto`, but the flag is checked only in `ClassifyCompareSetup`. `ClassifyQuery` treats an INSERT into #temp as local work and skips the persistent OUTPUT target.

Classifier reproduction, `allowMutation=false`:

```sql
CREATE TABLE #t(id int);
INSERT INTO #t OUTPUT inserted.id INTO dbo.audit_sink VALUES (1);
```

Result: `Allowed=True`, `HasMutation=False`. The persistent table indicated in OUTPUT is not a temporary table. The `query` path can therefore send such a batch without mutation confirmation, if the database user has the required rights. This also affects other places that use this classification.

Fix direction: include all write targets, including OUTPUT, in the shared analysis. Once a persistent target is added, the operation must not remain session-local.

### S2 / P1 — SQL Server: an alias beginning with # is treated as a temporary table

Source: `src/SqlHarness.Core/SqlSafety.cs:167`.

```sql
UPDATE #t SET id = 42 FROM dbo.items AS #t;
DELETE #t FROM dbo.items AS #t;
```

The classifier allows both examples without mutation. It checks the target name without resolving the alias from FROM. In this form #t is an alias of a persistent table. Direction: resolve the DML target with respect to aliases, instead of inferring locality from a token prefix.

### S3 / P1 — PostgreSQL: SELECT does not guarantee the absence of side effects

Sources: `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs:12`, `:266`, `:488`; `Postgres/NpgsqlSessionFactory.cs:95`.

Accepted offline as read-only:

```sql
SELECT set_config('search_path', 'public', false);
SELECT pg_cancel_backend(12345);
SELECT pg_advisory_lock(42);
SELECT app.write_something();
```

The first three functions change configuration, attempt to cancel someone else's query, or hold a session lock. The last example shows that the classifier does not recognize the effects of user functions; a real function may perform DML. The list of forbidden names covers only some such operations. The Npgsql session does not add a read-only transaction boundary.

The fix requires defining a permission model and a policy for functions and data sources. A read-only transaction can limit writes to persistent tables, but by itself it does not block all administrative effects, locks or external access. Adding a few names to the denylist is not sufficient. Views, functions and synonyms can also hide operations that the local names in the SQL do not reveal.

Source of function semantics: https://www.postgresql.org/docs/15/functions-admin.html

### S4 / note on AST coverage — no confirmed server-side gap

Source: `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs:148` and `:170`.

The library parser accepts an INSERT into TEMP with a modifying CTE in the source SELECT, and the classifier skips such a write. However, PostgreSQL requires that a modifying CTE be attached to a top-level statement. The tested example with WITH after INSERT INTO is therefore evidence of a parser inconsistency and incomplete AST analysis, **not a confirmed bypass of the protection on a real server**. The CTAS form requires the same caution. I do not count these trials as confirmed P1 vulnerabilities.

Direction: before extending the grammar, unify the recursive analysis of all write targets and add tests of parser conformance with the real engine. A standalone WITH with an INSERT into a persistent table was correctly rejected by the current classifier.

Server rule: https://www.postgresql.org/docs/17/queries-with.html
### S5 / P1 — PostgreSQL: the TLS setting does not allow forcing certificate verification

Source: `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs:37`.

`trustServerCertificate=false` gives `SslMode.Require`, and true gives `Disable`. For the Npgsql 8 version in use, Require provides encryption without certificate verification; true also disables encryption. The behaviour is documented in the project, so this is a flaw in a deliberately accepted contract, not a divergence between the implementation and the specification.

Direction: an explicit TLS policy in the profile, the ability to use VerifyFull and to configure the CA. The name trustServerCertificate should not at the same time decide about disabling TLS. Implement a change of the default behaviour with profile migration.

Documentation: https://www.npgsql.org/doc/security and https://www.npgsql.org/doc/release-notes/8.0.html

### S6 / P1 — an invalid parameter value can leak through InnerException

Sources: `src/SqlHarness.Core/SqlSafety.cs:349`, `src/SqlHarness.Core/SecretRedactor.cs:7`, `src/SqlHarness.Core/SqlHarnessModule.cs:179`.

For `n:int=private-audit-value` the redacted error contains:

```text
Invalid value for SQL parameter 'n'. | The input string 'private-audit-value' was not in a correct format.
```

Before successful parsing, knownSecrets contains the full declaration, not the value itself. The parser preserves the FormatException, and the redactor appends its content. The new parameter-set path has additional protections, but does not solve all the older plain --param paths.

Direction: safe validation messages without appending unsanitized inner exceptions; extraction of raw values for redaction before parsing. Trials are needed for query, compare, measure, watch and snapshot.

## Overly strict blocks and correctness

### B1 / P1 — PostgreSQL parameters re-parsed as T-SQL

Sources: `src/SqlHarness.Core/SqlSafety.cs:861`, `src/SqlHarness.Core/SqlHarnessModule.cs:197`, `src/SqlHarness.Core/MeasureParameterSets.cs:72`.

With the parameter n:int=1 `SELECT @n` passes, but `SELECT @n::int`, `SELECT @n LIMIT 1` and `SELECT @n, $$hello$$` are rejected with a message about parsing parameter references. The PostgreSQL classifier accepts the first query with ::int. The cause is the unconditional use of TSql170Parser in SqlParameterReferenceValidator. The error affects many commands.

Direction: parameter reference analysis belongs to the correct dialect and should share the parsed document with the safety analysis.

### B2 / P1 — PostgreSQL: DNS versus IP blocks valid connections

Sources: `src/SqlHarness.Core/SqlExecution.cs:49`, `src/SqlHarness.Core/Postgres/PostgresPing.cs:12`.

Server identity comes from inet_server_addr(), while the profile may contain a DNS name. A textual comparison of `db.example.test` with `192.0.2.1` returns false despite a matching database name. Loopback has a special bypass, which masks the problem in the local playground.

Direction: separate identity rules per engine, accounting for DNS/proxy as well as TLS with host verification. Do not remove the whole target check as a stopgap fix.

### B3 / P1 — PostgreSQL: logical reads counted multiple times

Source: `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs:208`.

WalkBuffers sums the metric of every node, although a parent node includes its descendants. A synthetic plan Aggregate(10 hits) → Scan(10 hits) gives 20 instead of 10. A change in plan depth can look like a change in I/O cost and lead the agent to a wrong recommendation.

Direction: take total buffers from the root aggregate; define per-relation metrics separately and preserve the relation schema. In addition, the current long rounds fractional milliseconds, and CPU=0 means no measurement, not proven zero consumption. The report should distinguish this in a machine-readable way.

Buffer semantics: https://www.postgresql.org/docs/15/sql-explain.html

### B4 / P2 — safe local operations rejected more broadly than necessary

Confirmed offline: SQL Server rejects `DECLARE @x int=1; SELECT @x` in query, although DECLARE is allowed in setup. It also rejects TRUNCATE #temp and ALTER TABLE #temp. PostgreSQL rejects EXPLAIN SELECT and SELECT INTO TEMP; ANALYZE of a temporary table ends in ParseError in the parser in use. The lo_ prefix block also rejects the hypothetical safe user function lo_custom_readonly.

These are candidates for precise extensions, not a reason to disable the validator. First fix the recognition of real targets. ANALYZE temp is particularly useful for representative PostgreSQL benchmarks. Keep the prohibition on persistent DDL, dynamic SQL and uncontrolled sources.

### B5 / P2 — watch has neither a hard time limit nor a whole-report limit

Source: `src/SqlHarness.Core/WatchRunner.cs:52`, `:107`.

The deadline is checked after the query; after a delay up to the deadline, the loop still starts another poll. The query still gets the full timeout, with no cancellation token tied to the remaining time. Every changed result goes into List emitted. The row limit applies per poll, not to the whole history. Messages from the entire session are also retained.

Direction: a time budget for the whole operation, deadline verification before each poll, limits on history and bytes, an optional event stream and a final summary.

## Interfaces and duplication

| Item | Assessment | Direction |
|---|---|---|
| ISqlHarnessModule | A good single entry point for the CLI and tests; the object? result loses the link between the operation type and the report | Keep the facade, consider typed results inside |
| ISqlSessionFactory / ISqlSession / ISqlReader | Different responsibilities: connection, session, result stream; they are not duplicates | Keep; identity after connecting should not require a public setter |
| ISqlDialect | Too broad: safety, parameters, catalog, plans and benchmark execution | Clarify the responsibility for SQL analysis and engine capabilities, without creating an interface for every function |
| ISqlDialect.IdentitySql / CollectSessionTempTables | No production calls to these members; the factories use their own constants, and setup already returns SessionTempTables | Remove the dead contract or genuinely centralize its use |
| ICompareArtifactWriter / IQueryStoreArtifactWriter / IIndexAnalysisArtifactWriter | Different data and confidentiality requirements; do not automatically merge domain interfaces | A shared implementation of safe file publication, staging and cleanup |
| ISnapshotStore / IGainStore | Different persistence and data semantics, not duplicate writers | Keep separate contracts |
| SqlHarnessModule | It aggregates dispatch, validation, redaction, lifecycle, measurements, reports and the catalog | Extract the preparation and execution of operation families behind the existing facade |
| HelpProvider | An empty "for the future" class, currently providing no behaviour | Remove it or fill it in only for a concrete help change |

SqlHarnessModule changed in 32 of the last 100 commits; ISqlDialect and both adapters in 9 each. This is evidence of an active change-cost hotspot, not merely a complaint about file size. CompareCellRunner still calls static SqlHarnessModule helpers, and BenchmarkRunner and BenchmarkCollector physically remain in its file.

Duplication of rules already has consequences: target validation in the CLI rejects profile + --engine, but TargetResolver ignores Engine when detecting the direct option and ignores it when a profile is present. The SELECT/CTE/INTO analysis is repeated in PostgresSafetyClassifier and PostgresBenchmark. Exception mapping is repeated in the module, watch and snapshot. Target safety and legality should be authoritative in Core; the CLI is there to translate user input.

The schema, space and indexes commands partially describe indexes, but they answer different questions: structure, physical size, DMV recommendation coverage. I would keep them. Likewise, measure --param-set and compare --matrix have different goals and session models; merging them would reduce contract clarity.

## Agent usage and token economy

### A1 / P1 — --json does not guarantee JSON for errors

Sources: `src/SqlHarness.Cli/Commands/Renderer.cs:12`, `:94`; `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs:70`.

Confirmed renderer result for an error in Json mode: `SQLHarness Safety: SQL safety rejection: UnsupportedStatement.`. CLI validation also prints text. The agent must guess the response format and parse natural-language messages. There is no stable code for the specific cause, the stage, or whether the error can be fixed by changing the input.

Direction: a versioned JSON envelope for successes and errors, stable codes, a safe error location and a next-step hint. It should also cover argument, input and artifact-write errors.

### A2 / P2 — summary limits some tables, not the total cost

Sources: `src/SqlHarness.Core/BenchmarkSummary.cs:138`, `src/SqlHarness.Cli/Commands/Renderer.cs:11`, `src/SqlHarness.Core/QueryResultCollector.cs:65`.

The 10-operator limit is good. Table maps, warnings, the number of matrix cells and the number of parameter sets remain unbounded. Query limits rows, but a single varchar(max) can take megabytes. The collector still reads and hashes all rows; --max-rows is a presentation limit, not a query cost limit. OutputCaptureWriter additionally keeps a copy of all written text.

Synthetic measure-summary report with 1000 table entries:

- the current JSON with indentation: 91 619 bytes;
- the same report without indentation: 41 376 bytes;
- a byte reduction of approx. 55%; actual model tokens were not measured.

The example is a test of a missing boundary, not a declaration of a typical report size. A global byte limit, a cell length limit and omitted/truncated metadata are needed. Full data should remain in deliberately chosen artifacts. Do not truncate data used for equivalence just because their presentation is limited.

### A3 / P2 — capability discovery requires unnecessary context

The main --help shows command names without descriptions. The agent must read separate helps and the long AGENTS.md. There is no concise description of engine capabilities, supported input, limits, output format and permission requirements.

In this environment the installed sqlharness.exe has a shorter command list than HEAD: qstop and indexes are missing. This is an observation of drift between the installation and the repository, not a bug in the current code. It is worth exposing the contract version, the program version and the build identifier in a single short doctor/capabilities result. The response must not reveal profile secrets.

### A4 / P2 — gain measures a byte heuristic, not the agent's token accounting

Sources: `src/SqlHarness.Core/CanonicalResults.cs:20`, `src/SqlHarness.Core/SqlHarnessModule.cs:262`, `src/SqlHarness.Core/GainStore.cs:183`.

The estimate is ceil(bytes/4), and the saving of a single call is floored at zero. The aggregate sums positive savings and does not subtract cases where the response was larger than the reference raw. Raw is an internal canonical representation, not an alternative response actually delivered by another tool. It does not account for prompts, help, error fixes or follow-up calls.

Direction: keep an explicit estimated, also report a negative net delta, and explicitly describe the reference point. Evaluate task scenarios: how many calls and bytes are needed from the question to a correct decision. Do not equate a 55% byte reduction with a 55% reduction in LLM cost.

## Proposed features — order

1. **validate offline**: SQL classification, used parameters, supported dialect, detected write targets and safe reason codes; without a connection and without treating such a result as later authorization for a mutation.
2. **capabilities / doctor**: a small machine-readable description of the version, commands, engines, limits and dependencies. Separate local diagnostics from an explicitly invoked database check.
3. **A consistent agent response mode**: short JSON without indentation, a byte limit, priority findings, counts of omitted items and references to artifacts. Keep full JSON for analysis.
4. **Selective artifact reading**: fetching a single section or a selected operator instead of re-running the benchmark. Do not automate disclosure of SQL text and parameters.
5. **Watch with a budget and events**: a final summary, a bounded change history, optional NDJSON and correct cancellation at the deadline.
6. **Engine-aware metrics**: null/unavailable for PostgreSQL CPU, buffer semantics and fractional time measurement; information about the separate equivalence sidecar. Optional regression thresholds for CI with a result separate from execution correctness.
7. **Further PostgreSQL diagnostics**: pg_stat_statements as an explicitly supported option after the basics are fixed; without pretending to identical permissions and Query Store semantics.

Every proposal concerns the same closed profile and set of variables. Agent conveniences should not add automatic target switching or mutation retry.

## What is well designed

- Parameters are bound, and connections are built by builders.
- Profiles reject unknown and duplicated properties, and regex validation has a timeout.
- A shared session for setup and benchmarks, plus separate matrix cell sessions, have a clear contract.
- Comparison results have separate ordered/multiset/set/off, and the row limit has fingerprints.
- Snapshot rejects writing a truncated result, and diff does not print values.
- Query Store separates metrics from confidential SQL text; newer writers have staging and cleanup.
- XML distillation has size and depth limits, and summary has an operator limit.
- No automatic index modifications based on DMV recommendations alone.

## Verification and limitations

- `dotnet test SqlHarness.sln --no-restore --filter 'FullyQualifiedName!~Integration' --verbosity minimal`: 1404 passed, 1 failed, 0 skipped.
- The failed `RunAsync_CancellationTerminatesEntireProcessTree` exceeded the PID publication timeout. Repeated separately with --no-build it passed (1/1). I do not present the entire first run as green; it is a signal of test/environment instability, without evidence of a regression in the cancellation mechanism.
- `dotnet list SqlHarness.sln package --vulnerable --include-transitive --format json`: the NuGet source reported no vulnerable dependencies. This does not prove the absence of all vulnerabilities.
- Reproductions in `Program.cs` and `evidence.txt`: only parsers, classifiers, rendering, synthetic metrics and identity comparison. The data used is artificial.
- No integration tests and no queries on real databases were performed. In particular, the effects of accepted SQL require regression tests on disposable databases of both engines before the fixes are deployed.
- Priorities: first S1–S3 and S5–S6; then B1–B3 and A1; then response/time budgets and tidying up responsibilities. Syntax extensions after the gaps in write analysis are closed.


## Offline reproduction archive

Synthetic data. True means a classifier decision, not successful SQL execution on a server. In particular, the PG NESTED trials do not constitute confirmed server-side gaps (see S4).

~~~text
PG True mutation=False local=False reason=Allowed: SELECT set_config('search_path', 'public', false)
PG True mutation=False local=False reason=Allowed: SELECT pg_cancel_backend(12345)
PG True mutation=False local=False reason=Allowed: SELECT pg_advisory_lock(42)
PG True mutation=False local=False reason=Allowed: SELECT app.write_something()
PG False mutation=False local=False reason=ParseError: CREATE TEMP TABLE x AS WITH changed AS (DELETE FROM public.items RETURNING *) SELECT * FROM changed
PG False mutation=False local=False reason=ParseError: CREATE TEMP TABLE x(id int); INSERT INTO x WITH changed AS (DELETE FROM public.items RETURNING id) SELECT id FROM changed
PG True mutation=False local=True reason=Allowed: CREATE TEMP TABLE x(id int) ON COMMIT DROP; INSERT INTO x VALUES (1)
PG True mutation=False local=True reason=Allowed: DELETE FROM pg_temp_fake.items
PG True mutation=False local=False reason=Allowed: SELECT @n::int
PG True mutation=False local=False reason=Allowed: SELECT 1;
PG False mutation=False local=False reason=UnsupportedStatement: EXPLAIN SELECT 1
PG False mutation=False local=False reason=ParseError: CREATE TEMP TABLE x(id int); ANALYZE x
PG False mutation=False local=False reason=SelectIntoNotAllowed: SELECT * INTO TEMP x FROM public.items
PG False mutation=False local=False reason=UnsupportedStatement: SELECT lo_custom_readonly()
SQLSERVER True mutation=False reason=Allowed: CREATE TABLE #t(id int); INSERT INTO #t OUTPUT inserted.id INTO dbo.audit_sink VALUES (1);
SQLSERVER False mutation=False reason=UnsupportedStatement: DECLARE @x int = 1; SELECT @x;
SQLSERVER False mutation=False reason=UnsupportedStatement: CREATE TABLE #t(id int); TRUNCATE TABLE #t;
SQLSERVER False mutation=False reason=UnsupportedStatement: CREATE TABLE #t(id int); ALTER TABLE #t ADD v int;
SQLSERVER True mutation=False reason=Allowed: SELECT * FROM dbo.items WITH (UPDLOCK, HOLDLOCK);
PARAM OK: SELECT @n
PARAM REJECT: SELECT @n::int: SQL parameter references could not be parsed.
PARAM REJECT: SELECT @n LIMIT 1: SQL parameter references could not be parsed.
PARAM REJECT: SELECT @n, $$hello$$: SQL parameter references could not be parsed.
PG DNS identity matches: False
PG buffers expected=10 actual=20; sub-ms elapsed=0
SQLSERVER ALIAS True mutation=False reason=Allowed: UPDATE #t SET id = 42 FROM dbo.items AS #t;
SQLSERVER ALIAS True mutation=False reason=Allowed: DELETE #t FROM dbo.items AS #t;
SQLSERVER ALIAS True mutation=False reason=Allowed: INSERT INTO #t OUTPUT inserted.id INTO dbo.audit_sink VALUES (1);
PG NESTED True mutation=False reason=Allowed: CREATE TEMP TABLE x AS WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING *) SELECT * FROM changed
PG NESTED True mutation=False reason=Allowed: CREATE TEMP TABLE x(id int); INSERT INTO x WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id) SELECT id FROM changed
PG NESTED False mutation=False reason=MutationNotAllowed: WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id) SELECT id FROM changed
PARAM ERROR REDACTION: Invalid value for SQL parameter 'n'. | The input string 'private-audit-value' was not in a correct format.
JSON ERROR: SQLHarness Safety: SQL safety rejection: UnsupportedStatement.
SYNTHETIC SUMMARY 1000 tables: pretty bytes=91619 compact bytes=41376; bytes/4 estimates=22905/10344 (not tokenizer)

~~~
