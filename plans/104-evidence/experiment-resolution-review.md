# Plan 104 experiment resolution review

Date: 2026-10-24
Checkout: `D:\Dev\sqlharness\.worktrees\plan-104`
Scope: read-only review of the preserved SQL Server IO and plan captures. No product or test files were changed, and I did not execute SQL.

## Verdict

Resume the original Plan 104 implementation with a narrow, typed classifier at the SQL Server STATISTICS IO producer, before `TempTableKey.Normalize` removes the raw suffix. The live captures distinguish the generated table-variable spelling from the reported local-temp-table spelling, including the exact hex-name collision that becomes ambiguous after normalization. The earlier blocker based only on normalized string-key fixtures does not describe the raw producer input; those fixtures need to exercise the parser and carry its provenance through aggregation.

This is a bounded empirical rule, not a claim that Microsoft documents STATISTICS IO naming for every SQL Server release. The tested engine is SQL Server 17.0.1145.4, Standard Developer Edition, EngineEdition 3. Keep this version boundary in the evidence and keep all unrecognized or legacy inputs classified as Unknown.

## Captured evidence

The preserved matrix reports SQL Server 17.0.1145.4. Its representative raw IO names are:

- A caller-created `#ABCDEF12` local temp table is reported as `#ABCDEF12` followed by the engine suffix: a run of underscores and exactly 12 hexadecimal characters. The same full name appears in `tempdb.sys.objects` when queried by `OBJECT_ID`.
- Table variables are reported as a bare `#` followed by exactly eight hexadecimal characters, such as `#A3D092CF` and `#BC9C4099`.
- The direct explicit-hex temp table, conventional `#p104_temp` temp table, mixed variable-plus-hex-temp case, repeated dynamic batches, and two-variable case preserve that distinction in the captured output.
- In the temporary procedure `#p104_probe`, three repeated calls report the explicit hex temp table with the same suffixed name, the conventional temp table with its same suffixed name, and table variable `@v` as bare `#BC9C4099`. In this case the explicit temp names have 107 underscores followed by their 12-hex suffix. The corresponding 24 actual plan XML captures (eight statements across three calls) report `RetrievedFromCache=true`. Their object references are `[#ABCDEF12]`, `[#p104_temp]`, and `[@v]` respectively. The object IDs and IO spellings remain stable across the three calls.
- Repeated dynamic table-variable batches also produce fresh bare eight-hex names. The classifier must therefore aggregate by kind within each run, rather than by generated spelling across runs.

`TempTableKey.Normalize` currently turns the explicit raw `#ABCDEF12` plus suffix into the same string as a bare `#ABCDEF12`. The collision is introduced by normalization; the raw SQL Server message retains the distinguishing suffix. The matrix covers one SQL Server build and a set of execution contexts. It does not establish a universal naming guarantee for all engine versions.

The experiment runner produced a second full three-repeat matrix while a summary-only plan inspection was being attempted. Both captures were preserved. The SQL used session-local temporary objects and table variables; the experiment report states that no persistent effects occurred. This review performed no database activity.

## Classifier and identity contract

Apply classification only to the decoded raw table name from SQL Server STATISTICS IO, before normalization or aggregation:

1. **Historical exact-build recommendation superseded by the accepted Plan 104 rule (2026-10-24):** the user accepted proceeding from the characterized experiment without a patch-version allowlist. Current implementation recognizes an exact bare `^#[0-9A-Fa-f]{8}$` raw name before normalization and does not impose a ProductVersion gate. SQL Server `17.0.1145.4` remains the observed build and the empirical evidence boundary, not a vendor guarantee. Preserve the raw spelling as evidence.
2. A raw local-temp name with the engine suffix shape recognized by `TempTableKey` is typed `LocalTempTable`. Derive its semantic base name using the existing suffix normalizer, and retain the full raw spelling as provenance. This rule preserves the caller's original name when it is `#ABCDEF12`. That suffix was observed on the tested build; this is an empirical rule, not a vendor guarantee.
3. Every other raw name is `Unknown` and keeps its exact reported identity. A legacy artifact with no provenance field defaults to `Unknown`, even if its stored name matches the eight-hex pattern. The accepted implementation uses strict raw signatures without an engine-version allowlist; investigate naming drift before broadening a signature.

Carry a composite typed identity through the run artifact and aggregation boundary, for example `(ObjectKind, SemanticName)`, with the table-variable aggregate represented by a dedicated kind/sentinel identity. Keep the raw reported name separately where the capture already preserves per-run IO evidence. Do not use the display string `table variables` as the identity key.

Within each run, sum all `TableVariable` counters into the single typed table-variable aggregate before computing cross-run distributions. Keep local temp tables under their normalized base name but with `LocalTempTable` identity, so an explicit `#ABCDEF12` cannot merge with a generated variable whose raw name happens to be `#ABCDEF12`. Keep Unknown rows under exact identity. This typed representation also prevents a real object whose display name is `table variables` from colliding with the aggregate label.

Put this one classification helper at the raw SQL Server parsing seam and use it from both `StatisticsIoParser` and `StatisticsIoDetailParser` before either path folds rows by name. Carry the typed identity through `StatisticsIo`, per-run benchmark artifacts, journal aggregation, and any saved metric representation. Add the new fields additively so old artifacts deserialize with Unknown provenance. Existing historical journal aggregates and artifact values remain historical; do not reinterpret their names or synthesize provenance on read.

Preserve batch totals and current LOB addition semantics. A repeated two-variable run must sum both counters into the per-run aggregate before the median is calculated. Keep displayed labels as presentation only.

## Plan corroboration and source limits

Microsoft documents that a local temp table created in a stored procedure or concurrently executable module receives an internal suffix, and that `tempdb.sys.objects` stores the full suffixed name. The [CREATE TABLE temporary tables documentation](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#temporary-tables) does not specify that STATISTICS IO always emits that full name. The [SET STATISTICS IO output documentation](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statistics-io-transact-sql?view=sql-server-ver17#output) describes `Table` as the table name without a temporary-object kind or naming rule. The [tempdb documentation](https://learn.microsoft.com/en-us/sql/relational-databases/databases/tempdb-database?view=sql-server-ver17) confirms that both local temp tables and table variables use tempdb, so database location alone cannot identify the kind.

The live output and corresponding actual plan object references provide direct corroboration for the recognized signature on the tested build. Microsoft Press also documents cached explicit temp tables whose catalog names become bare `#` plus eight hex characters after procedure scope. That is a counterexample to classifying from `tempdb.sys.objects`; it is not an observed active STATISTICS IO spelling. Keep classification at the raw STATISTICS IO producer and never substitute catalog state. Current capture code does not preserve a per-counter link to a plan object, so plan XML is independent validation rather than a production identity source. The tested-build boundary describes available evidence, not a runtime gate. The public sources do not define the IO naming rule, and the accepted raw-shape inference is not a vendor guarantee.

The primary-source survey and transport boundary are recorded in [SQL Server internal naming research](sqlserver-internal-naming-research.md) and [STATISTICS IO transport research](statistics-io-transport-research.md).

## Required regression evidence before closing implementation

- Drive the parser with raw IO text for a bare generated `#8hex` row and a suffixed explicit `#8hex` temp row. Assert distinct typed identities even when the explicit row normalizes to the same base name.
- Cover fresh generated names across repetitions, two generated variables in one run, mixed explicit-temp and table-variable IO, the cached temporary-procedure case, stable permanent-name behavior, and unknown raw shapes.
- Assert totals, including LOB reads, remain unchanged; the per-run table-variable sum is computed before cross-run distributions.
- Deserialize legacy artifacts without provenance and assert they remain Unknown with their stored names and values. Preserve existing historical journal labels without reclassification.
- Verify benchmark summaries, journal persistence/readback, dashboard comparisons and exports, agent projections, clipping, and collision-safe display behavior.

The original median repair is implementable from the actual producer boundary under the accepted Plan 104 empirical rule, without a hard-coded exact-build runtime gate. Keep the observed SQL Server 17.0.1145.4 and uncharacterized naming behavior explicit in final evidence; do not present the raw shape as a universal SQL Server contract. Unsupported raw forms and legacy inputs without producer provenance remain Unknown.
