# Plan 102 Task 1 report

## Baseline and RED

Implementation base: `1266881c3e340ac3e4d5e2abeebd5725100145f9` (`plan-093`). The reviewed implementation rebuilt and serialized the whole containing projection for each candidate entry. The deterministic all-fitting fixture had 128 unique table identities, 9,917 UTF-8 bytes in its serialized input map, significance-sorted values, and escaped quote, backslash, control, and multibyte characters.

| Shape on reviewed implementation | Allocated bytes | Elapsed ms (reported only) | Full response bytes including newline |
|---|---:|---:|---:|
| Measure | 1,912,024 | 384.74 | 10,512 |
| Paired compare | 3,581,016 | 279.51 | 20,828 |

The new 1,500,000-byte stress assertion is deliberately above the optimized renderer's fixed serialization and envelope overhead. The measured pre-fix measure and compare fixtures exceed it, demonstrating RED on the reviewed behavior. Wall time is diagnostic only and is not asserted.

The before run did not capture artifact metrics or the mostly-rejected fixture. The same selector served artifact metrics, but no before/after artifact performance saving is claimed. Artifact and mostly-rejected behavior are covered by final correctness/work-bound regressions below.

## Change and deterministic work bound

Selection now serializes the empty containing projection once and serializes each source entry once as a singleton map, using the runtime `ProjectionJson` options. It subtracts the map braces to obtain exact property-name/value bytes, then adds a comma only when that retained map already has an entry. Compare selection applies both maps against one fixed-shell budget and advances them round-robin. Rejected entries do not consume a detail slot; later fitting entries remain eligible. The outer 384-byte reserve and final render checks remain authoritative.

An internal test seam records the fixed-shell and entry-content accounting work without adding a public field or runtime output. The deterministic test checks that the shell is accounted once and that all 128 escaped/multibyte entries sum to their exact serialized singleton-property bytes once. It also asserts that all identities and values remain present at a fitting budget.

The 4,096-byte mostly-rejected compare and artifact fixtures use 128 entries, including 16 highest-ranked 5,000-character keys followed by short escaped/multibyte keys. Both renderers retain later fitting entries, omit the oversized keys, report omissions equal to source minus retained counts (twice for compare), and stay within their byte ceilings. This covers the previously identified failure mode without claiming a pre-fix artifact measurement.

## After measurements

The same all-fitting 128-entry fixture was measured after the change at 1,048,576 bytes. Input map size remained 9,917 UTF-8 bytes for each source map; complete response size includes the trailing newline from `RenderAgentResult`.

| Shape | Allocated bytes | Elapsed ms (reported only) | Full response bytes including newline |
|---|---:|---:|---:|
| Measure | 327,944 | 10.73 | 10,512 |
| Paired compare | 436,064 | 4.83 | 20,828 |
| Artifact metrics | 227,960 | 2.67 | 10,299 |

These local timings and allocation totals are fixture observations, not token usage or machine-independent performance guarantees. The deterministic byte-accounting assertions are the work-bound evidence.

## Verification

Passed:

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~High_cardinality_table_selection_stress|FullyQualifiedName~Table_identity_selection_accounts_fixed_shell_and_each_source_entry_once|FullyQualifiedName~Table_identity_selection_skips_oversized_entries_and_keeps_later_fitting_entries|FullyQualifiedName~Agent_compare_keeps_fitting_table_after_oversized_highest_ranked_identity" --logger "console;verbosity=detailed"` — 8 passed.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~AgentOutputTests --logger "console;verbosity=minimal"` — 87 passed.
- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter FullyQualifiedName~McpTokenBudgetTests --logger "console;verbosity=minimal"` — 45 passed.

The full Windows gate was intentionally not run; the controller will coordinate it after the separate dashboard matcher repair. No database or provider calls were made.

## Review round 1/5 follow-up

Added per-entry source attribution assertions for every retained table identity. The all-fitting high-cardinality measure and artifact fixtures validate each emitted key and its min/median/max against the source. The paired compare fixture validates all entries on both sides. The mostly-rejected compare and artifact fixtures validate every retained key/value pair against the source while retaining their omission and budget assertions.

Focused command:

`dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~High_cardinality_table_selection_stress|FullyQualifiedName~Table_identity_selection_skips_oversized_entries_and_keeps_later_fitting_entries|FullyQualifiedName~Table_identity_selection_accounts_fixed_shell_and_each_source_entry_once" --logger "console;verbosity=minimal"`

Result: passed, 5/5 tests, 0 failed, 0 skipped (564 ms).
