# Compare as the dashboard centre

Status: design agreed with the user, 2026-10-12. Implementation: [Plan 062](../../../plans/062-compare-context-dashboard.md).

## Goal

The operator is to see the course of the agent's SQL optimization: successive attempts, fragment tests and the directly measured result of the whole. SQLHarness still saves tokens and harness-building time and maintains the boundaries of credentials and mutations.

## compare context

- Two `compare` context fields: `subject` identifies the optimized SQL, the textual `scope` identifies the tested fragment. For the whole, the agent uses exactly `whole`.
- Example: `{ "subject": "orders-report", "scope": "history" }`. The context contains no SQL descriptions, versions nor justifications.
- The global setting `compare.requireContext`, `true` by default, independent of the profile and the journal setting.
- With `true`: both fields are visible and required in the MCP schema, and required as `--subject` and `--scope` in the CLI. A missing correct pair is a validation error before connecting to the DB.
- With `false`: both `compare` context fields (`subject` and the textual `scope`) are completely removed from the MCP schema and are not accepted as hidden arguments. This does not apply to the request-scoped `target`, which still serves for database selection. In the CLI the context fields are optional; if the user gives one, they must give both.
- MCP freezes the setting for the lifetime of the process. A change takes effect after a server restart and a client reconnect; the CLI reads it at the start of the invocation.
- There is no compatibility period for beta calls. Older journal records remain readable without context.
- Labels: 1..64 ASCII characters, pattern `^[a-z0-9][a-z0-9._-]{0,63}$`, ordinal comparison; do not change or truncate the input. The agent uses short labels without business data and secrets, and consistently repeats existing names.

## Separating `target` and `scope` in MCP (breaking change)

- In **all tools dependent on the database target in request-scoped MCP mode** the existing input object `scope: {profile, vars}` is renamed to `target: {profile, vars}`. This is a deliberately incompatible API change, without an alias for the old `scope` object. It does not apply to internal type names nor to the CLI database-selection options.
- In `compare` the `scope` argument means **exclusively the textual SQL optimization fragment** (`whole`, `aggregation` etc.); it appears when `compare.requireContext=true`. `subject` also applies only to `compare`.
- In request-scoped `compare` mode with context enabled, `target` (object), `subject` (string) and `scope` (string) are required separately. In fixed MCP mode there is no `target` argument, because the target is chosen by the server configuration.
- When `compare.requireContext=false`, `subject` and `scope` do not occur in the `compare` schema; the request-scoped `target` still occurs. The remaining tools do not accept a textual `scope` or `subject` in any mode.
- Do not accept the old `scope` object in request-scoped: for `compare` with context it is the wrong SQL scope type; without context or in other tools it is an unknown argument. Both cases must end in validation before connecting to the DB.

Example `compare` in request-scoped MCP with `compare.requireContext=true`:

```json
{
  "target": { "profile": "staging", "vars": {} },
  "subject": "orders-report",
  "scope": "aggregation",
  "baseline": { "sql": "SELECT ..." },
  "candidate": { "sql": "SELECT ..." }
}
```

## Session and versions

- Layout: session → `subject` → `scope` → chronological compare attempts. Other operations remain in the All operations tab. Session metadata are in a collapsible panel.
- Plan 062 does not change the session boundary: it uses the existing `sessionId` (the current MCP process key or the existing CLI session identification), without introducing a new agent conversation identifier. If the MCP process lifecycle does not match the conversation lifecycle, the dashboard still shows the boundary of the current journal session.
- Within `(sessionId, subject, scope)` each new SQL content gets `v0`, `v1`, etc. according to the first occurrence of the hash; the baseline is first on a tie within one attempt. A reused hash keeps its number.
- Baseline/candidate are roles in an attempt. `v1` may occur on both sides. The number does not mean version provenance nor a choice made by the agent.
- A missing hash means an unknown version. Do not compute identity by SQL similarity, file name nor operation order.
- The context comes from the client and is not a verified SQL description. Do not derive the fragment–whole relation from the hash.
- Records without a context pair have a separate No scope assigned group. Do not assume a single SQL and do not present them as `whole`.
- Numbering is based on the full retained history in the journal, before pagination and filtering. We do not store separate version state. After retention the numbering may change; the UI explains that it shows retained history.

## subject header

- Shows the newest completed compare `scope=whole`, the version pair and the result of that specific attempt. Completed means `succeeded`, `failed`, `rejected` or `abandoned`; running does not replace a completed result. Order by start, then by ID.
- An error/rejection/abandoned has an appropriate label and does not borrow the green result of an earlier attempt.
- Without a whole compare: No final whole comparison. With only a running whole attempt: Whole comparison in progress, no result.
- Improvement from the start is presented only for the direct attempt `v0 → the candidate version of the last completed whole compare`, with its own measurements and conditions. Do not multiply the percentages of successive attempts nor sum the improvements of fragments.
- There is no mechanism for marking a chosen version, a winner, an automatic recommendation nor a claim of business correctness.

## compare detail

- Header: subject / scope / version pair, a link to the session and the appropriate group.
- Separate information: execution status; performance (improvement, regression, inconclusive or no assessment); agreement of observed results (consistent, different, not checked, check incomplete).
- Performance uses the existing Core verdict from Plan 051. The median percentage alone does not constitute a statistical verdict. Do not recreate a new classifier in the UI.
- Side by side baseline, candidate and delta: elapsed, CPU, logical reads/buffers of the appropriate engine, grant and spills. The repetition count and the min–max range are visible; a missing metric and a zero baseline do not give an invented percentage.
- For matrix/parameter sets the case table contains a separate result, agreement and metrics for each case. Do not hide a single-cell regression in the aggregate. Case names and values respect the journal's data retention policy.
- Tabs: Cases, SQL changes (diff by default, full versions available), Plans and IO (both sides), Measurement conditions (profile, resolved database, vars/parameters if stored, setup, repeat, timeout, equivalence mode, available cache and spill settings).
- Data not persisted or historically unavailable are described as unavailable; do not fetch new measurements nor read arbitrary artifact paths for the purposes of the view.
- `journal.storeSensitive=false` still allows showing the context, hashes and safe metrics, but SQL/setup, parameter values and full plans remain not persisted.
- raw/emitted tokens remain supplementary information; do not equate compare time with the user's saved time.

## Boundaries

No changes to the connection rules and the scope of DB permissions (the new name of the `target` input does not change the semantics of database selection), secrets, the persistent-mutation classifier and output budgets. Do not run the operator dashboard or its API while the agent works: verify through an isolated fixture/test host and the UI. No live DB, user configuration changes, installation, publication or push as part of implementing this plan. Readable SQL and data are not replaced by masking; the current retention and credential filtering policy applies.
