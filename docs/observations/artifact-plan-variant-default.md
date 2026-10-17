# Observation: artifact plan section rejects an omitted variant that has only one valid value

Status: collecting observations, not approved for implementation. Recorded 2026-10-16 against
repository HEAD `566628e`.

## Observed workflow

After a `measure`, the agent read `artifact` with `section = plan` and a statement ordinal but without
`variant`. The call failed with `safety_rejected` (phase `validation`, exit 2) and the message
"The artifact plan variant is invalid." The agent retried with `variant = "query"` and succeeded. One
extra call; the message did not name the accepted values.

## Verified source

- `src/SqlHarness.Core/ArtifactReader.cs:434-441`: for measure and measure-set artifacts the only valid
  variant is `query`; for compare artifacts `baseline` or `candidate`. Anything else, including an
  omitted variant, throws `ArtifactReadException` with `SqlHarnessExitCode.Safety`.
- The MCP schema describes `variant` as optional ("Plan variant: baseline, candidate, or query").

Classification: ux gap. Confidence: high for the behavior; recurrence across sessions not recorded.

## Questions for later synthesis

- Should an omitted `variant` default when the artifact kind admits exactly one value (`query` for
  measure)?
- Should the message list the accepted values for the artifact's kind?
- Should an invalid argument use a validation code rather than the safety exit code? (Plan 077 already
  revisits error classification; check its outcome first.)

Cheapest next check: an offline artifact-reader test with a measure fixture and no variant.
