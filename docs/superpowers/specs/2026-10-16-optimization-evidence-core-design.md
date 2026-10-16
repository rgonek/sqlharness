# Optimization evidence core (stage A) — design

Date: 2026-10-16. Plan: 079 (to be written from this design). Origin: dashboard review of 2026-10-16 and two
consilium rounds (reports kept outside the repository; decisions summarized here).

## Intent

SQLHarness's main use case is agent-driven SQL optimization: an agent runs `compare` (and `measure`) many times,
and the operator later shows a reviewer the evidence — mostly logical reads and scan counts per table, sometimes
the plan — and reproduces baseline and candidate by hand in SSMS. Today the evidence is unreliable or incomplete
in ways the operator cannot see:

- Parameters and setup are journaled as raw input shapes (`parameters`, `typedParameters`, `matrix`,
  `matrixText`, `parameterSets`, `setupSql`), so nobody can tell what to copy or how it ran.
- Matrix/set cases are labelled by index ("Cell 0").
- Table IO silently disappears: a command window over 1,000 messages discards all table IO
  (`SqlServerDialect.cs` collect path, `SqlExecution.cs` `SessionMessageBuffer`); table variables (`#XXXXXXXX`,
  renamed every execution) get per-name medians of 0; the headline total includes LOB reads while the table rows
  and UI total do not (op 203: 11,910 vs 1,404).
- `measure` binds setup to the first parameter set only and rotates sets on one connection, unlike `compare`; it
  journals no conditions at all.
- "Same plan" compares only the first statement's plan hash (`PlanChanges.tsx` / `planChanges.ts`).

Stage A makes the journaled evidence trustworthy, complete and reproducible, so stage B (dashboard redesign) can
present it. It changes Core, journal and the minimum dashboard surfaces needed to expose the new data.

## Goals

1. **Canonical conditions model**, written at journal time for both `measure` and `compare`, independent of the
   input shape the agent used (inline parameters, matrix, inline sets, set files; MCP or CLI).
2. **Case identity and labels**: every case carries a stable id and a human label built from its varying values
   (`@ClassType = N'ClientConfigurationModel2'`), falling back to set name, then `Case 2 of 3` (1-based).
3. **Conditions fingerprint** stored for every run, so later stages can decide whether two measurements are
   comparable across operations and sessions.
4. **`measure` adopts `compare` session semantics**: one connection and one setup per case, setup bound to that
   case's parameters; cases run sequentially.
5. **Two reproduction scripts per side and per case** (SQL Server): an `sp_executesql` form (faithful) and a
   `DECLARE` form (convenient), each copyable from the dashboard.
6. **No silent loss of IO**: table IO survives large/looping batches; conditions survive large parameter sets.
7. **One definition of logical reads** across the journal and dashboard (headline, per-table, Markdown copy), with
   LOB shown explicitly.
8. **Table variables grouped** so they no longer vanish into per-name zero medians.
9. **Plan change detection across all statements**, not only the first.

## Non-goals (later stages)

- Dashboard information architecture: session Optimization section, version table, compare-first operation page,
  aggregates and toggles, list/badge/naming cleanup — **stage B**.
- Per-statement IO attribution, "shared statement" collapsing, hidden-work (UDF/MSTVF) flags — **stage C**, after
  a UAT spike.
- New parameter kinds (TVP / user-defined table types, Dapper list expansion, RPC procedure execution) — **stage D**.
- Exposing reproduction scripts to agents through MCP/`artifact` — coordinate with plan 077 later (my call: dashboard
  only in stage A).
- Reset/rollback between repetitions for approved persistent mutations.
- PostgreSQL reproduction scripts (my call: generator sits behind the dialect; PostgreSQL reports "script not
  available for this engine" in stage A).

## Anti-goals

- A script that looks ready to run but is not what SQLHarness executed (wrong types, lost setup split, missing
  values presented as real ones).
- Hiding a regression or an incomplete case behind a summary.
- Weakening safety classification, credential protection or the `storeSensitive` opt-in.
- Fingerprints that disclose parameter values (low-entropy values must not be recoverable from the hash).

## Constraints

- **Breaking journal change accepted by the owner**: no legacy reading or migration of old journal rows. On opening
  a journal with an older schema version, keep the old file renamed (e.g. `journal.v13-<utc>.bak`) and start a new
  journal (my call on the mechanism). Plan 073 is DONE and set the journal to version 13; this plan takes the next free version (14 at planning time).
- Values and setup text remain opt-in (`storeSensitive`), as today. Without them the model still records names,
  effective types, case structure, setup hash and the fingerprint.
- Artifacts (non-journal) keep their existing compatibility rules; plan 077 coordinates case identity fields
  (`setName`, `parameterValue`) — reuse them, do not invent parallel ones.
- **Plan 077 is in execution on another machine.** Plan 079 starts only after 077 is merged to `main`; its planning
  base is that merge commit, with a drift check over the areas both touch (measure-set results and case identity in
  Core reports, `McpOperationMapper`, `McpToolCatalog` descriptions, `CompareMatrixRunner`, `MeasureParameterSets`,
  artifact metadata). 077's evaluation baseline (Task 0) is frozen before its product changes, so 079's measure
  semantics change does not disturb it, but 079 must not alter 077's MCP envelope or case decision fields.
- No formatting churn; match local style (repo rule).
- Interface meaning lives in tool descriptions/schemas, runtime policy in capabilities (AGENTS.md); update the
  `measure` description and README when its semantics change.

## Decided design

### Canonical conditions model

Built in Core from the **bound** parameters (after `SqlParameterInputs.Resolve` / `ParameterSetResolver.Resolve`),
not from input syntax. Shape (names illustrative):

```text
conditions (version 1)
  engine, server, database                       -- resolved target identity
  execution: { operation: measure|compare, sessionPolicy: per-case, setupBinding: per-case,
               warmup, repeat, timeoutSeconds, comparisonMode, compareNullability }
  setup: { hash, text? }                         -- text only with storeSensitive; plus the command split used
  fixed: [ { name, effectiveType, value? } ]     -- effectiveType = exact bound type incl. length/precision/scale
  cases: [ { id, index, label, setName?, varying: [ { name, effectiveType, value? } ] } ]
  fingerprint                                    -- see below
  omitted: [ field names omitted for size or sensitivity ]
```

- `effectiveType` is the type actually bound (`nvarchar(4000)` vs `nvarchar(max)`, `datetime` vs `datetime2(7)`,
  `decimal(18,4)`), so scripts declare exactly what ran.
- Case `id` is stable across display order (derived from the case's typed values or set name); `index` is the
  input order.
- The raw input shapes are no longer journaled as separate fields.
- Size: replace the 64 KiB all-or-nothing cap with a larger bound plus per-field omission recorded in `omitted`
  (builder sets the bound; it must fit realistic multi-case sets and long setup text).

### Fingerprint

A keyed hash (HMAC with a per-journal random salt stored in the journal) over: engine, server, database, setup hash,
the ordered list of fixed and per-case `{name, effectiveType, value}`, and execution policy fields that affect
measurement (session/setup policy, comparison mode). Not included: `repeat`, `timeoutSeconds` (shown next to numbers
instead). One fingerprint per case and one per operation (over all cases). Always stored, even without
`storeSensitive`.

### Case labels

`@Name = <literal>` for one varying parameter; `setName` when sets are named; multiple varying values joined
(`@A = 1, @B = N'x'`); without stored values, the set name or `Case n of N`. Long literals are middle-truncated for
display only; the full value stays in the model. Journal the label with each case run (today only the index is
journaled).

### measure semantics

`measure` with parameter sets/matrix opens one connection per case and runs setup on it, bound to that case's
parameters — the same contract as `compare` (`CompareCellRunner`); cases run sequentially (see "Calls settled",
item 3). `measure` journals the conditions model.
Update the tool description (`McpToolCatalog.MeasureDescription`), CLI help, README and capabilities if they state
the old behavior.

### Reproduction scripts (SQL Server)

Generated in Core by the dialect from the conditions model plus the side's SQL, per side and per case:

1. **`sp_executesql` form** (default, labelled "as executed"): header comment (operation, side, version label,
   case label, "new connection; setup runs per case"), session `SET` options matching SqlClient defaults where they
   differ from SSMS (e.g. `ARITHABORT`), setup split exactly as `SetupSqlExecution.PrepareCommands` executes it
   (parameter-free root prefix as its own batch, parameterized remainder through `sp_executesql`, `GO` between),
   `SET STATISTICS IO, TIME ON;`, then `EXEC sys.sp_executesql N'…', N'<exact parameter definitions>', @p = <literal>…`.
2. **`DECLARE` form** (labelled "estimates may differ: local variables are not sniffed"), same header/setup, then
   `DECLARE @p <type> = <literal>;` and the SQL inline. When every statement carries `OPTION (RECOMPILE)` the label
   says the forms are equivalent.

Literals round-trip: `N'…'` with quote escaping, invariant ISO dates, typed `NULL` (`CAST(NULL AS <type>)` where
needed), binary as `0x…`, decimals invariant. Without stored values, each value is a typed placeholder
(`/* value not stored */`) and the script is labelled "incomplete — values not stored". A note says SQLHarness
discards one warm-up and reports medians; the script runs once.

Dashboard (minimal, stage A): two copy actions per side for the selected case in the compare view and in the
measure view, plus the conditions rendered as a readable list instead of raw JSON fields. Layout polish is stage B.

### Table IO completeness and definitions

- Parse `STATISTICS IO` into per-table aggregates **as messages arrive** instead of after a bounded window, so
  dropping retained message text no longer drops table IO; keep the message buffer cap for the raw text only.
  Record when parsing itself was incomplete.
- Normalize table-variable names (`#` + 8 hex) into one "table variables" group per run **before** cross-run
  medians; keep named `#temp` normalization as today. Worktable/Workfile stay separate system rows.
- Logical reads = data logical reads + LOB logical reads, everywhere; per-table rows show both columns; the
  headline equals the per-run sum of the same quantity (median of run totals, labelled as such). The Markdown copy
  includes scan counts and the same grouping.

### Plan changes

Compare plan hashes per statement ordinal set on both sides; "Same plan" only when every statement's plan hash
matches; otherwise list which statements changed (ordinal + short statement text). Statement identity improvements
(hash + occurrence) belong to stage C; stage A only stops claiming "Same plan" falsely.

## Calls settled after the builder check

1. **Scripts require `storeSensitive`.** The journal stores baseline/candidate/setup SQL text only under
   `storeSensitive` (default `false`), consistent with the owner's decision to keep that opt-in. Scripts are
   generated on demand from journaled text; without stored SQL the dashboard shows "script not available — enable
   storeSensitive". Values missing while SQL is stored yield the placeholder/"incomplete" form. The SSMS acceptance
   check runs with `storeSensitive` on (the owner's configuration).
2. **Scope of the logical-reads definition and table-variable grouping: journal and dashboard only in stage A.**
   MCP output, artifacts and `RegressionPolicy` keep their current definitions so plan 077's agent contract and
   regression verdicts do not change. Harmonizing the agent path is a follow-up to agree with 077's owner. The
   dashboard labels which definition a number uses.
3. **`measure` runs cases sequentially, exactly like `compare`:** per case one connection, setup bound to the case,
   warm-up and repeats on that connection, then the next case. No concurrent sessions. The previous cross-case
   round rotation is dropped (measure reports per-case stability, not cross-case comparison). Failure semantics
   follow compare and plan 076: the first failing case stops the run; completed cases are retained and journaled.
4. **Case `id` = set name when the case is a named set, otherwise the input index;** it is not a value hash.
   Cross-operation matching uses the keyed per-case fingerprint. The existing unkeyed `ValueHash` is untouched
   (077 territory); its invertibility for low-entropy values is noted as a follow-up for 077.
5. **Journal versioning:** remove the legacy migration chain (`Version1`..`Version13`); any journal older than the
   new version is renamed to a backup and a fresh journal is created. From the new version onward, future plans
   (e.g. 073) migrate normally. Use the next free version number at execution time.
6. "Same plan" with a different statement count on the two sides counts as changed.

## Left to the builder

- Exact table/column layout of the version-13 journal schema and DTO names.
- Conditions size bound and omission policy details.
- How streaming IO aggregation hooks into `SessionMessageBuffer`/collectors.
- Exact SET option header contents (verify SqlClient vs SSMS defaults).
- Test strategy following `docs/agents/verification.md`; a live UAT check of the scripts (copy, run in SSMS,
  compare IO) is desirable but needs the operator's authorization.

## Acceptance

- Op-203-like run (table variables, typed parameters, matrix) journals: one canonical conditions record, case labels
  with values, per-case fingerprints, consistent headline vs per-table reads (difference only where explained), a
  "table variables" group instead of dozens of zero rows.
- A `measure` with two sets and parameter-dependent setup runs each case on its own connection with its own setup.
- Both scripts for each side/case execute in SSMS against the same database and yield the same per-table logical
  reads as journaled (operator-run check).
- A batch emitting >1,000 messages still journals table IO.
- A multi-statement compare whose second statement's plan changed does not show "Same plan".
- Opening a journal at an older version (13 today) preserves it as a backup and starts the new version.
