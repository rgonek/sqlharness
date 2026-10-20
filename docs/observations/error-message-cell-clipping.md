# Observation: error messages clipped by the result cell limit

Status: collecting observations, not approved for implementation. Recorded 2026-10-20 against
repository HEAD `a063e75c`. No implementation plan is requested.

## Observed workflow

During an optimization (unmapped system label), the agent ran two `query` calls with a small
`maxCellChars` (a few hundred characters), chosen to keep a long SQL-text result column short. Both
calls failed in the connect phase with `authentication_failed`. The first was a server-side network
access rejection; the second was a token acquisition failure in the default directory credential chain.
In both responses `error.message` was clipped to the same cell limit and `truncation` reported one
omitted item. The clipped tail held the actionable part: in the first case the remediation sentence,
in the second the credential-chain cause after the identity library's error prefix. The agent stopped as the skill
requires and gave the operator a likely cause (sign in again through the CLI) that the visible text
did not confirm. Cost: two failed calls; the visible text gave no cause for the second failure.

## Verified source

- `src/SqlHarness.Mcp/McpResultAdapter.cs:361-388` (`RedactError`): `Message`, `Hint` and
  `Location.Path` are each clipped with the caller's `maximumCellCharacters`.
- `src/SqlHarness.Cli/Commands/Renderer.cs:152` applies the same clipping to CLI agent output, so the
  behavior is consistent between MCP and CLI and matches the documented cell parity
  ([runtime contract](../mcp/runtime.md#result-envelope-errors-and-budgets), plan 067).
- The `maxCellChars` schema description says "Cell clip"; it does not mention that it also bounds error
  text.

Classification: provisional ux gap (works as designed; the cell limit chosen for result rows also hides
error causes). Confidence: high for the clipping and its source; medium for impact, since a retry with
a larger limit was not attempted (the skill forbids automatic retries after access failures) and the full
messages were not recovered from the journal. Recurrence: two calls in one session.

Existing coverage: none found in `plans/` or `docs/observations/`. Related:
[telemetry plan clipping](query-store-plan-cell-clipping.md) (cell limit on a result cell, not an error).

## Questions for later synthesis

- Should error text have its own floor (for example the default cell limit) independent of a smaller
  caller limit for result cells, given that the error envelope is small compared with the byte budget?
- Is the full redacted message already in the journal (`error_message`), and would pointing to it be
  enough instead of a contract change?
- Do other sessions set small `maxCellChars` and then hit errors?

Cheapest next checks: compare a journaled `error_message` with the emitted clipped message for one
failed operation (local read-only); an offline test that renders a long synthetic error with
`maxCellChars` 200 and checks which part survives.
