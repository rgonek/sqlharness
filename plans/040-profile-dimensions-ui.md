# Dynamic profile dimensions and target matrix

Status: implementation complete; Windows gate verified; Linux verification outstanding.

## Goal

Turn the Statistics page's flat Targets list into a profile-driven view of
activity by scope variable. Dimension names, values, filters, and matrix axes
must work with any configured profile. No organization-specific profile names,
database conventions, or fixed variable names belong in the implementation.

For example, `app-{project}-{component}-{region}` exposes `project`,
`component`, and `region`; another profile exposes its own dimensions.

## Statistics UI

1. Select the profile with the most operations in the selected time range by
   default, using a deterministic name tie-break. Show a profile selector and
   the selected profile's database template. Preserve a manual selection when
   the time range changes, including when that profile has no activity.
2. Discover dimensions from the profile's variable definitions. Include
   historical recorded dimension names so profile edits or deletion do not
   hide history. Distinguish a missing definition from an empty definition.
3. Show a compact breakdown for each dimension: operations, percentage of the
   selected profile's filtered operations, total execution duration, failed
   operations, and rejected operations. Duration means journaled operation
   duration, not user working time; unavailable durations remain unavailable.
4. Provide Rows, Columns, and Metric selectors for a two-dimensional matrix.
   Choose two distinct dimensions initially when available. Other dimensions
   remain filters. Default metric: operation count; alternative metrics:
   total execution duration, failed operations, and rejected operations.
5. Display numeric cell values with a subtle heatmap, row/column totals, and
   accessible labels. Clicking a cell opens the corresponding operation list,
   preserving profile, time range, and dimension filters. Distinguish no
   operations from operations whose selected metric is unavailable.
6. Selecting a dimension value filters all breakdowns, the matrix, and the
   database list within Targets. Show active filters and a clear action.
   Other Statistics cards retain their existing scope in the first version.
7. One dimension uses a breakdown table. Zero dimensions retain the database
   table. Large matrices provide value search, scrolling, and an explicit
   display limit; label totals as visible or full-scope totals. Hidden values
   must not silently alter full-scope totals or the default profile choice.

Example (operation count):

| project / component | api | portal | Total |
|---|---:|---:|---:|
| alpha | 42 | 18 | 60 |
| beta | 11 | 7 | 18 |
| Total | 53 | 25 | 78 |

## Data and attribution

- Reuse the existing read-only profile metadata and journaled operation scope
  variables. Values recorded with an operation are authoritative; do not
  reinterpret them using today's template or validation rules.
- Aggregate the complete requested time range on the backend. The existing
  Targets response limits the most frequent profile/database pairs and cannot
  be the source of complete dimension statistics.
- For historical entries lacking variables, optionally infer values from the
  operation's recorded profile and the current database template only when
  the match and captures are unambiguous. Escape literal template text,
  require a full-name match, and check variable rules. Never guess a profile
  from a matching database name alone. Label inferred values and explain that
  attribution uses the current template, not a historical template snapshot.
- Ambiguous or missing values appear as Unknown, with missing values kept
  distinct internally from a literal value named "Unknown". An unavailable
  or changed profile must not remove operations from totals.
- Preserve profile, engine, server, and database identity in operation links
  and details. Profile-level aggregation may combine endpoints, but must not
  imply that identical database names identify the same physical target.
- Use one shared dimension resolver and summary representation for Statistics
  and later consumers. Keep reads local: no database probes, profile writes,
  or parameter-value collection are needed.

## Gain visibility and data coverage

The Statistics Tokens saved card must explain which operations contribute to
its estimated gain. Keep gain scoped to the selected time range, like the
other Statistics cards; Targets profile/dimension filters do not alter it.

- Report the count of operations with both raw and emitted estimates against
  the total operation count, and identify operations lacking one or both.
- Compute estimated savings only from paired raw/emitted data. MCP operations
  historically have raw estimates without emitted estimates and cannot contribute
  a measured savings ratio. Do not invent emitted values or backfill history.
- When no paired data is available, show gain unavailable and a clear reason,
  rather than a dash accompanied by misleading zero raw/emitted totals. Empty
  activity and incomplete coverage must remain distinguishable.
- Label tokens as estimates derived from output bytes, not actual model usage.
  Preserve negative net savings when emitted output exceeds raw output.
- Verify all-MCP/raw-only activity, mixed CLI/MCP coverage, empty activity,
  complete pairs, and negative savings with backend and UI tests.

## MCP emitted estimates

Record emitted output footprints for new MCP executions after the final
budgeted CallToolResult has been built, using its SDK-serialized UTF-8 bytes
and the existing ceil(bytes / 4) estimator. This is an estimate of the tool
result representation, not actual model token usage or the full JSON-RPC
transport envelope. Include the complete returned result representation.

- Cover fixed-profile handlers and request-scoped target-free/target-dependent
  handlers. Complete each logical operation's emission receipt once and only
  once; keep execution gates, cancellation and response byte budgets intact.
- Use the existing journal emission pipeline. Storage/accounting failures must
  not alter valid tool output or leak sensitive values. Respect any existing
  receipt semantics for genuine artifact-finalization failures.
- Gain and Statistics may include CLI and new MCP operations with paired raw
  and emitted footprints. Preserve negative net savings and document the
  estimation method and coverage. Historical raw-only rows stay unavailable.
- Update AGENTS.md, relevant MCP/gain documentation and tests to remove the
  old promise that MCP emitted counts are never recorded. No sensitive text,
  values or secrets are added to journal fields; store counts only.
- Test fixed/request execution, errors, budgeted output, single-use receipts,
  cancellation/busy paths and journal failure behavior without live databases.

## Reuse elsewhere

After the Statistics flow, reuse the same dimension labels and attribution
markers in operation lists/details and session summaries. Labels link to
filtered operations. Profiles can show usage summaries and link to their
Statistics matrix. Session summaries must represent multiple values rather
than assign one scope to a session containing several targets.

## Delivery order

1. Add shared dimension resolution and complete backend aggregates, including
   profile totals, attribution, metric availability, and time-range filtering.
2. Add the profile selector, dynamic filters/breakdowns, and matrix to Targets.
3. Add emitted footprint accounting for new MCP responses and its journal/gain
   coverage tests.
4. Add cell drill-down to a paginated operation list with matching backend
   filters; reuse labels in operation details where practical.
5. Extend the same presentation to sessions and Profiles as a follow-up.

Likely touchpoints: `DashboardModels.cs`, `JournalReader.cs`,
`DashboardProfiles.cs`, dashboard route wiring, UI API types/queries,
`StatsPage.tsx`, and shared UI helpers/components.

## Acceptance and verification

- Verify arbitrary profiles with zero, one, two, and several variables,
  variable values containing separators, and non-database scope variables.
- Cover changed/deleted profiles, malformed or missing recorded variables,
  ambiguous inference, and unavailable durations.
- Verify default profile selection and aggregate totals with more targets
  than the existing top-list limit, including time boundaries and empty ranges.
- Verify matrix totals against filtered operation counts, and drill-down
  against exactly the same scope. Filters and manual selection survive range
  changes; changing profiles replaces incompatible axes and filters.
- Check keyboard access, non-color cell information, and narrow layouts.
- Run focused backend/UI tests, then both repository gates:
  `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1`.
  Do not start the operator dashboard or call its API as an agent.
