# Plan 038: Fix the MCP friction found in a live UAT optimization session

> **Executor instructions**: Work items are independent; take them in ID order unless the user picks a subset.
> For each item, run its acceptance check and confirm the expected result before moving on. If anything in
> "STOP conditions" occurs, stop and report — do not improvise. When done, update the status row for this plan
> in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 64cd684..HEAD -- src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Cli/Commands/Renderer.cs src/SqlHarness.Core/SecretRedactor.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/MeasureParameterSets.cs src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Core/SqlValidation.cs src/SqlHarness.Mcp/McpResultAdapter.cs src/SqlHarness.Mcp/McpInputReader.cs`
> If any in-scope file changed since this plan was written, compare the "Current state" excerpts against the live
> code before proceeding; on a mismatch, treat it as a STOP condition for that item.

## Status

- **Priority**: P1 (W1–W3), P2 (W4–W6, W8), P3 (W7)
- **Effort**: S per item except W5 (M)
- **Risk**: LOW (W1, W4, W6), MEDIUM (W2, W3, W5, W7, W8: touch safety or session semantics)
- **Depends on**: —
- **Category**: bug (W1–W3, W7), ux (W4, W5, W8), agent guardrail (W6, W8)
- **Planned at**: commit `64cd684`, 2026-10-07

## Source session

A Claude Code session optimized a batch query in `System1` on a test database through MCP
request-scope mode. About 15 of the session's tool calls were spent working around the items below rather than on
the optimization. SQL text, parameters and artifacts from that session stay local; this plan only quotes shapes.

Accepted follow-up from a second Codex optimization session: nine SQLHarness calls
encountered an error, rejection or obstructive truncation. HEAD was still `64cd684`; the running MCP reported
`serverVersion=1.0.0` (not proof of binary/commit equality). Extend W1/W2 and add W8 rather than create a duplicate
implementation plan. W2 is the implementation owner; the earlier workspace setup-lifetime plan is superseded.
The runtime scope explanation remains a hypothesis until a real-provider test proves it.

## W1 — Default agent projection always truncates to one item (P1, bug)

**Evidence.** `sqlharness_query` returning 3 rows × 3 columns (raw footprint 2,151 bytes, budget 16,384) answered
with one row and one column, `truncation: { detailLimit: 1 }`. Every multi-row or multi-column result behaved the
same, so `maxRows` had no visible effect and the agent packed results into one `STRING_AGG` cell.

**Current state.** `AgentOutputProjection.CalculateDetailLimit` (`src/SqlHarness.Core/AgentOutputProjection.cs:12-18`):
`perItemEstimate = max(128, maxCellChars * 16)` → 8,192 for the default 512; `estimate = 16384 / 8192 = 2`;
`(int)Math.Pow(2, 0.2) = 1`. `Renderer.cs:176-198` starts its degradation loop at that level and only goes down,
so an untruncated projection is never attempted. No unit test covers `CalculateDetailLimit`.

**Change.** Try the untruncated projection first (`detailLimit = int.MaxValue`), then degrade through decreasing
limits (e.g. 128, 64, …, 1, 0) until the envelope fits `requestedBytes`. Apply the same order to the nested
matrix-cell projection (`AgentOutputProjection.cs:111`).

**Acceptance.** New tests: a 3×3 query report of ~2 KB projects all 9 cells with `truncation: null`; a 20 KB report
under a 16 KB budget is still truncated and reports `omittedItems`. Existing MCP schema tests stay green.

**Accepted follow-up.** A successful three-cell compare matrix exposed only its first cell. Three subsequent
`artifact(metrics)` calls each exposed only baseline, requiring local report discovery and extraction in three
shell calls. Current HEAD uses `Take(matrix.Cells)` at `AgentOutputProjection.cs:108` and
`Take(metrics.Variants)` at line 200; MCP starts degradation at the estimated level in `McpResultAdapter.cs:172`.
Extend the fix and drift check to the MCP adapter as well as CLI and nested projections.

**Additional change/acceptance.** A small matrix fitting the wire budget returns every cell, each artifact
reference, and both variant summaries; small artifact metrics returns baseline and candidate without truncation.
For genuinely oversized matrices retain bounded cell summaries/references sufficient to retrieve omitted detail
without filesystem discovery. If even those references cannot fit, provide bounded paging with an explicit
continuation (never imply completeness). Test that the caller can retrieve every omitted cell through MCP under
the same scope, and that all envelopes still fit the actual serialized wire budget. Do not solve this by raising
the default budget or dropping scope validation.

**Accepted follow-up (2026-10-08, System2 post-deploy check, five isolated database scopes).** A `query` file with three
`SELECT` result sets (raw footprint 1,754 bytes) returned only the first result set with one column:
`omittedItems: 4`, `detailLimit: 1`. A single 3-row × 4-column summary returned one cell (`rowCount: 3`,
`omittedRowCount: 2`). The agent rewrote six query variants into one `STRING_AGG` cell. Same cause:
`ProjectResultSets` (`AgentOutputProjection.cs:55-57`) applies `Take` with the same detail limit to result sets.
**Additional acceptance:** a query with three small result sets that fit the budget returns all three, each with
every row and column, and `truncation: null`.

## W2 — `#temp` created in setup is invisible when parameters are bound (P1, bug)

**Evidence.** `sqlharness_measure` with `setup` creating `#preparedCards` and a query reading it failed with
`Invalid object name '#preparedCards'`. An earlier attempt failed with "variable already declared", which shows that
`parameters` bind to the setup batch too. README "Benchmark setup contract" (`README.md:192`) promises that setup
`#temp` objects stay visible to the measured SQL.

**Hypothesis (verify first).** Setup is executed with parameters (`CompareCellRunner.cs:171`,
`MeasureParameterSets.cs:467` pass `request.Parameters`), so SqlClient sends it through `sp_executesql`; a `#temp`
created inside that call is dropped when the call returns. The existing integration proof probably runs setup
without parameters.

**Accepted follow-up.** A compare matrix with `CREATE TABLE #t (Id int PRIMARY KEY); INSERT #t SELECT
TOP (@BatchSize) ...` (`@BatchSize:int`, fixed `@AsOf:datetime`) passed safety but failed in cell zero with
`sql_execution_failed`, exit 5, `Invalid object name`. No cell completed. A later compare without setup succeeded.
`SqlExecution.cs:342` binds all supplied parameters, including those not used by setup. Existing integration tests
must be checked for this extra-parameter case; an unparameterized proof is insufficient.

**Change.** First reproduce with real SqlClient tests for measure, compare and matrix, including constant setup
with a fixed parameter used only by measured SQL. Trace root execution scope versus parameterized execution on
the same connection; do not implement a guessed fix. After confirming the cause, record one contract before
implementation: either support typed parameterized temp population using a verified execution strategy, or reject
unsupported lifetime shapes before connecting and document the narrower contract. For setup referencing no
parameters, test execution without unused bindings. A candidate strategy for parameterized population is an
AST-derived, parameter-free leading temp-declaration prefix followed by typed population on the same session;
it is not a preapproved implementation. Do not reorder arbitrary SQL, interpolate values, recommend parameter
literals, weaken safety, or widen named table-type support. Update README and docs/mcp.md to match the decision.

**Acceptance.** Real-provider tests reproduce failure before the fix and then demonstrate the chosen behavior:
supported setup temps survive warm-up and repetitions; unsupported shapes reject offline with an actionable
safe message. Cover matrix isolation/setup once per connection, extra fixed bindings, parameterized population,
setup failure and cancellation/session disposal. Run configured tests for all three execution modes; skipped
integration tests do not constitute PASS. Unchanged PostgreSQL behavior and typed binding remain covered.

**STOP** if the hypothesis is wrong (temp is lost for another reason); report the actual cause.

## W3 — Secret redaction mangles error messages (P1, bug)

**Evidence.** Error text came back as `The variable name '@[REDACTED]lientID' ...` and
`Invalid object name '#prepared[[REDAC[REDACTED]ED]E[[REDAC...]]ards'`. The bound parameter values included
single-character strings (`C`, `D`, `R`, `T`).

**Current state.** `SecretRedactor.Redact` (`src/SqlHarness.Core/SecretRedactor.cs:38-51`) applies one
`string.Replace` per known secret in sequence, so later secrets match inside earlier `[REDACTED]` markers.
Parameter values are added as known secrets (`SqlHarnessModule.cs:~520`).

**Change.** Replace all known secrets in a single pass (one regex alternation, longest first) so markers are
never re-scanned. Separately decide, and record in the plan's "As built", whether parameter values shorter than a
minimum length (e.g. < 4 characters) are excluded from redaction. That is a disclosure-policy decision for the
user; do not change it silently.

**Acceptance.** Tests: values `["C", "D"]` on `"@ClientID uses D"` yield one marker per real occurrence and never
`[[REDAC`; the existing "1 vs 100" ordering test still passes.

## W4 — Invalid input path gives no hint (P2, ux)

**Evidence.** Files under the agent scratchpad were rejected with `The input path is invalid.`; the agent found
the allowed root only by reading the client MCP config and the `--input-root` argument.

**Current state.** `McpInputReader.cs:137-164` throws the same message for every failure.

**Change.** Distinguish "outside every configured input root" from the other failures and add a hint:
`path must be under a configured --input-root (N roots configured)`. Do not print the roots themselves unless the
operator config allows paths in agent output (capabilities currently exclude paths by design).

**Acceptance.** Test: a path outside the roots yields the hint; malformed or relative paths keep their messages.

**Accepted follow-up (2026-10-08).** A workspace path that is a directory junction to a location outside the root,
and the junction's resolved path, were both rejected with the same message; finding the root took three shell
reads of the client config. The hint above is the permanent fix; the local operator guidance now names where the root
is configured.

## W5 — Per-statement metrics for multi-statement batches (P2, ux)

**Evidence.** The bottleneck (one UPDATE dominating CPU in a multi-statement batch) was found only by
parsing `plans/*.sqlplan` `QueryTimeStats` and `runs.jsonl` on disk with PowerShell; `sqlharness_artifact`
`metrics` returns batch totals only.

**Change.** Add an artifact section `statements`: per variant and statement ordinal, a statement hash, CPU and
elapsed (from `QueryTimeStats`), degree of parallelism, and the top operators by actual rows/executions (physical
op, object, index, estimated vs actual rows, executions). Bound it with the same agent projection as other
sections. Consider persisting the same rows as `operation_statements` in the journal (also a prerequisite of the
report draft `docs/superpowers/specs/2026-10-07-agent-session-report-draft.md`).

**Acceptance.** For a two-statement batch, `statements` returns two entries whose CPU sums to within rounding of
the plan totals; SQL text stays out of the section unless `storeSensitive` allows it.

## W6 — Guard the dashboard token from agent shells (P2, agent guardrail)

**Evidence.** The agent ran `cat ~/.sqlharness/*.json`, which printed `dashboard.json` including the dashboard
access token into the transcript.

**Change.** Document in `docs/mcp.md` (agent section) that `dashboard.json` holds a live access token, and ship a
sample Claude Code / Codex hook (`scripts/hooks/`) that denies shell reads of `~/.sqlharness/dashboard.json` and
glob reads of `~/.sqlharness/*.json`. Optionally rotate the token when the dashboard sees it echoed (out of scope
unless cheap).

**Acceptance.** Hook test: `cat ~/.sqlharness/*.json` and `type %USERPROFILE%\.sqlharness\dashboard.json` are
denied; `cat ~/.sqlharness/targets.json` is allowed.

## W7 — Safety classifier false positives on XML methods (P3, bug or doc — uncertain)

**Evidence.** `CROSS APPLY plans.PlanXml.nodes('...')` was rejected as `CrossDatabaseReference` (alias.column.method
read as a three-part name); the same logic over a variable (`@x.nodes(...)`) was rejected as `UnsupportedStatement`.

**Change.** First decide whether XML methods (`nodes`, `value`, `query`, `exist`) are intentionally denied. If yes,
return a dedicated reason (`xml_method_unsupported`) instead of `CrossDatabaseReference`. If no, recognise
`alias.column.method(...)` in the multi-part name resolver and allow read-only XML methods.

**Acceptance.** Tests for both shapes return the decided outcome with an accurate reason.

## W8 — Explain table-variable rejection and detect setup-only variables (P2, ux / guardrail)

**Evidence.** `DECLARE @t dbo.SomeType; INSERT @t ...` in compare setup was rejected with
`safety_rejected / NonTemporaryWrite`. This is an intentional type-support boundary, not a permission bug.
The agent also declared its input variable only in setup; separate benchmark batches could not see it even if
the type were supported. An earlier claim that changing to #temp sufficed was corrected after the W2 failure.

**Current state.** `SqlSafety.cs:317-321` maps an unsupported setup write to NonTemporaryWrite.
`CollectBatchScope` at line 515 proves inline table declarations, not named user types. `SqlSafetyDecision.Detail`
and `RejectionDescription` already provide a safe diagnostic channel. Inspect compare preparation and parameter
validation for existing checks before adding a new one.

**Change.** Preserve existing reason codes/exit codes and denied constructs. Add generic detail for the known
unproven table-variable cause: "Table-position variables require an earlier inline TABLE declaration in the same
batch. Named user-defined types are not proven as table variables. Setup variables do not cross into benchmark
batches." Do not include SQL, supplied type/variable names or values. Detect a variable declared only in setup
and referenced by a variant without its own declaration or a supported bound input; reject before connecting.
Use existing AST and batch-scope validation, not name heuristics. This is a tool safeguard, not another skill rule.

**Acceptance.** Focused tests for named-type table use, scalar-as-table, undeclared/use-before-declare and
cross-GO use retain their rejection; valid same-batch inline tables stay allowed. Setup-only scalar/table
references fail offline, while genuine typed scalar inputs and independently declared variant locals still pass.
Persistent writes retain their diagnostics. CLI and MCP tests verify the new detail survives redaction without
revealing supplied identifiers/values. The existing SqlParameterReferenceValidator lives in SqlSafety.cs:1549
and is included in the drift check; stop if these checks require widening safety policy.

## Out of scope

- Raising the 16 KB default budget: W1 fixes the projection order; the budget itself is fine.
- Dashboard write endpoints or agent-authored reports: see the report draft spec.

## STOP conditions

- An item's "Current state" no longer matches the code.
- W2 hypothesis disproved.
- W3 or W7 would change disclosure or safety policy without a user decision.
