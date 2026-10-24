# SQL Server internal object names and `STATISTICS IO` — primary-source research

Date: 2026-10-24
Scope: source-backed naming facts relevant to Plan 104's local-object IO classifier. This is a bounded Microsoft-source review, not a claim that every SQL Server documentation page or historical source was exhaustively searched. No SQL was executed and no product or test code was changed.

## Decision

The proposed `^#[0-9A-Fa-f]{8}$` table-variable classification is supported by the preserved `STATISTICS IO` captures on SQL Server `17.0.1145.4` and by Microsoft Press's explanation that a table variable has a hidden tempdb table representation with an example eight-hex catalog name. It is **not a documented `STATISTICS IO` naming contract**. The public Microsoft contract describes the IO message's `Table` field as a table name and documents the message transport, but does not say that a bare eight-hex name means a table variable, nor that a suffixed name means a local temp table.

Therefore the classifier is a characterized, build-bounded inference from raw `STATISTICS IO` text. Keep it at the raw IO parsing boundary, retain raw spelling as evidence, and classify every non-matching spelling plus all legacy records without raw provenance as `Unknown`. Do not generalize the regex to other engine builds or use a `tempdb.sys.objects` name as a substitute for the IO message.

## Verified Microsoft facts

| Fact | Primary source and location | Boundary |
|---|---|---|
| `SET STATISTICS IO` output has a `Table` item described simply as “Name of the table”; the documented example formats the name in a textual `Table '…'` message. The page gives no local-temp/table-variable distinction or naming rule. | [SET STATISTICS IO (Transact-SQL), Output and Example](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statistics-io-transact-sql?view=sql-server-ver17#output) | SQL Server 2025 (17.x) docs. Supports treating the output name as text; does not define object-kind semantics for that text. |
| SQL Server error/message 3615 is a template with one table-name placeholder (`Table '%.*ls'`) and IO counters. ODBC documentation says `STATISTICS IO` is delivered as message 3615 after the result set is exhausted. | [Database Engine errors 3000–3999, message 3615](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-3000-to-3999?view=sql-server-ver17#errors-and-events-3000-to-3999); [Processing Statements That Generate Messages, STATISTICS IO](https://learn.microsoft.com/en-us/sql/relational-databases/native-client-odbc-error-messages/processing-statements-that-generate-messages?view=sql-server-ver15) | These specify message transport and format, not the naming algorithm. The ODBC article documents Native Client behavior and is transport evidence, not a claim about SQL client APIs generally. |
| The TDS `INFO` token carries number, state, class, message text, server name, procedure name, and line number. The token schema has no separate object-kind or object-id member; the name and counters arrive in message text. | [MS-TDS, §2.2.7.13 INFO](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-tds/284bb815-d083-4ed5-b33a-bdc2492e322b) | Protocol-level fact. The visible schema does not encode the table kind independently of `MsgText`. |
| For a local temporary table created in a stored procedure or concurrently executable SQL module, the engine appends a unique suffix so same-named tables from different sessions can be distinguished. The full `tempdb.sys.objects` name is the requested name plus that generated suffix; T-SQL continues to use the requested name. | [CREATE TABLE (Transact-SQL), Temporary tables](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#temporary-tables) | Current SQL Server 2025 docs guarantee a *unique suffix* and the catalog location for this stated context. They do not specify underscores, suffix length/hex alphabet, or what `STATISTICS IO` prints. |
| Table variables are not memory-only structures: the docs state they are created in `tempdb`, similarly to temporary tables. | [table (Transact-SQL), Remarks](https://learn.microsoft.com/en-us/sql/t-sql/data-types/table-transact-sql?view=sql-server-ver17#remarks) | Confirms tempdb-backed storage, not a particular internal name or IO-message spelling. |
| Microsoft Press describes the internal local-temp naming convention as the requested name followed by underscores and a unique numeric suffix; its `#T1` catalog example is a long underscored name ending in `00000000001E`. | Itzik Ben-Gan et al., *Inside Microsoft SQL Server 2008: T-SQL Programming*, ch. 7, printed pp. 247–248 ([Microsoft Press sample PDF, PDF pp. 92–93](https://www.microsoftpressstore.com/content/images/9780735626027/samplepages/9780735626027.pdf#page=92)) | First-party technical book, but SQL Server 2008-era description. It corroborates the concept and a historical example, not a current fixed suffix format. Current docs deliberately state only “unique suffix.” |
| A table variable has a hidden physical table representation in `tempdb`; in the example, declaring `@T` caused `tempdb.INFORMATION_SCHEMA.TABLES` to show `#0CBAE877`. | Same book, ch. 7, printed pp. 260–261 ([PDF pp. 105–106](https://www.microsoftpressstore.com/content/images/9780735626027/samplepages/9780735626027.pdf#page=106)) | Directly explains one observed *catalog* representation in SQL Server 2008. It does not state that `STATISTICS IO` always reports this spelling, and the example is not a cross-version guarantee. |
| A temporary table created by a procedure can remain cached in `tempdb.sys.objects` after the procedure terminates. The book's example leaves an explicit `#T1` as `#2DE6D218`; its later example leaves an explicit `#T1` as `#3A4CA8FD`. | Same book, ch. 7, printed pp. 255 and 257 ([PDF pp. 100 and 102](https://www.microsoftpressstore.com/content/images/9780735626027/samplepages/9780735626027.pdf#page=100)) | Concrete counterexample to interpreting a bare `#` plus eight hex characters in the **catalog** as necessarily a table variable. It is a cached explicit temp table. It does not establish the corresponding active `STATISTICS IO` spelling. |
| Microsoft Learn's SQL Workshop says procedures can cache temporary objects including temporary tables and table variables; dynamic SQL / `sp_executesql` objects are not cached, and some conditions disqualify temp tables. | [Temp Table Caching in SQL Server](https://learn.microsoft.com/en-us/shows/sql-workshops/temp-table-caching-in-sql-server) | Establishes that cache eligibility depends on creation context and conditions, not an object-name prefix alone. It does not document post-cache names in IO output. |
| Microsoft SQL Server engineering describes procedure/function temporary objects (including `DECLARE @t TABLE`) as eligible for caching/reuse, contrasting them with batch/dynamic temporary objects bound to user transaction lifetime. | Bob Dorr, Microsoft SQL Server engineering, [How It Works: Temporary Objects — User Transaction Scope](https://techcommunity.microsoft.com/blog/sqlserver/how-it-works-temporary-objects---user-transaction-scope/3290339) | First-party engine explanation of scope/caching behavior; no naming contract for `STATISTICS IO`. |

## What the sources do not establish

The Microsoft sources above do **not** document any of these rules for `STATISTICS IO`:

- that a table variable is always emitted as a bare `#` followed by exactly eight hexadecimal characters;
- that an explicit local temp table is always emitted with underscores and a twelve-hex suffix;
- that `STATISTICS IO` emits the same spelling as `tempdb.sys.objects`, an execution-plan object reference, or any other catalog surface;
- that the formatting is stable across SQL Server versions, editions, compatibility levels, or execution contexts.

This is a bounded documentation result. It does not prove no Microsoft source anywhere specifies these details. The current Learn pages describe `Table` at the semantic level; the Microsoft Press examples explain internal/catalog representations and caching, but stop short of a `STATISTICS IO` output rule. The experiment report remains the direct evidence for the observed raw-message rule.

## Experiment evidence versus source-backed fact

The preserved [experiment resolution review](experiment-resolution-review.md) reports raw message captures and actual-plan corroboration on SQL Server `17.0.1145.4`, Standard Developer Edition, EngineEdition `3`:

- a table variable produced a bare eight-hex `#...` IO name;
- explicit local temp tables, including `#ABCDEF12`, produced the underscore-plus-twelve-hex IO suffix;
- actual plans named the objects as `[@v]` and `[#ABCDEF12]`, and repeated procedure executions showed cache retrieval;
- The captures distinguish active raw IO names from the normalized names used for aggregation; the review does not treat catalog naming as the classifier input.

These are **experiment facts for the recorded build and cases**, not Microsoft-documented invariants. Separately, the Microsoft Press examples make the catalog collision concrete: a cached explicit `#T1` appears as `#2DE6D218` and `#3A4CA8FD` in `tempdb.sys.objects` after the procedure ends. This is a source-backed counterexample to classifying from the catalog. It does not contradict the captured active IO text because the observations concern different surfaces and lifecycle states.

## Classifier risks and counterexamples

| Candidate rule / evidence | Risk or counterexample | Handling |
|---|---|---|
| Bare `#8hex` in `tempdb.sys.objects` means table variable. | Microsoft Press shows cached user-created `#T1` objects in the catalog as `#2DE6D218` and `#3A4CA8FD`. | Never classify from catalog state. Use only raw IO text at the measured producer seam. |
| Every local temp gets an underscore plus a fixed 12-hex suffix. | Current CREATE TABLE docs promise only a unique suffix in the concurrent stored-procedure/module case; the 2008 book illustrates underscores plus a numeric suffix. Neither states the tested fixed-length hexadecimal form or IO behavior. | Treat the tested suffix regex as empirical and version-bounded. Do not broaden it based on a general description. |
| An eight-hex bare IO name is globally guaranteed to mean an anonymous table variable. | The docs define only the `Table` label; they do not bind this spelling to object kind. Other unseen builds/objects or message variants are uncharacterized. | Keep all non-matching and legacy records `Unknown`; add a recognized signature only after characterization for that exact producer/version scope. |
| TDS supplies a structured object id/kind that could remove ambiguity. | TDS `INFO` exposes the error number/severity and a text payload, not object identity fields. | A per-counter plan match would be an extra capture mechanism; don't imply transport already provides it. |

## Practical recommendation

**Historical research recommendation — superseded by the accepted Plan 104 rule (2026-10-24):** keep the Plan 104 parser-boundary classifier limited to the characterized SQL Server `17.0.1145.4` build. The user subsequently accepted implementation from the characterized evidence without a patch-version allowlist. Current behavior recognizes the exact raw signature before normalization on the tested build and does not impose an exact-build runtime gate. The build and cases remain an evidence limit, not a Microsoft guarantee. Preserve the original raw name separately from the typed aggregate identity; use `Unknown` for unsupported forms and legacy artifacts with no raw provenance; never infer variable identity from catalog, normalized, or display names. The source facts and tested-build boundary above remain valid.

## Method and source boundary

The review used current Microsoft Learn pages for `SET STATISTICS IO`, `CREATE TABLE`, error 3615, ODBC message retrieval, and MS-TDS `INFO`; the official SQL Workshops page; a Microsoft SQL Server engineering blog; and the publisher-hosted Microsoft Press sample chapter. The press book is first-party but describes SQL Server 2008, so it supports historical engine mechanisms and examples only. No third-party source was used to ground the findings. No runtime identity/version facts beyond the already preserved experiment report are asserted here.
