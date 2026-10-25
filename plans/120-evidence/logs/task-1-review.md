# Task 1 review — Plan 120

Reviewed range: `eb28657d..86ffc20c` (commit `86ffc20c`). Scope: Task 1 brief, Task 1 report, Plan 120 context, and the supplied diff. The diff output was truncated, so only its omitted section was retrieved; changed source files were not reopened.

## Verdicts

- **Spec: Partial; one scenario needs correction.** The inventory, two synthetic workspaces, exact hashes, labeled ergonomics estimate, and stated demand limitation are present. Ticket B contains repeated mock rows, but its outcome does not depend on duplicate multiplicity, so the required duplicate-sensitive scenario is not demonstrated.
- **Quality: Needs fixes.** The scenario issue below is the only finding. The rest of the change is clearly bounded, source-oriented, and explicit about proposed behavior versus current product guarantees.

## Strengths

- `inventory.md` maps manifest inputs to current CLI/Core/MCP behavior and identifies unsupported input combinations instead of implying parity. It distinguishes study limits from current limits and records the source authority for bounds.
- The fixtures are isolated under `plans/120-evidence/fixtures/`, use relative references, include exact hashes, and encode the intentionally stale Ticket B pin as a pre-dispatch rejection.
- The report labels the 60-versus-26 count as a synthetic placement estimate and explicitly states that it is neither observed demand, user time, nor model-token evidence. It also records the remaining demand uncertainty without inventing interviews.
- The inventory keeps file hashes separate from execution authorization, target permissions, database snapshot identity, and artifact validity. No product behavior or CLI/MCP parity change appears in the diff.

## Findings

### P2 — Ticket B does not isolate duplicate-sensitive comparison

`plans/120-evidence/fixtures/ticket-b/mock-outcomes.json:44-65` gives both baseline and candidate two copies of the same respective row, but the row values differ (`[4, 3]` versus `[4, 2]`). The comparison would still be different after deduplicating both sides, so this mock outcome does not exercise the effect of multiplicity. The paired SQL at `plans/120-evidence/fixtures/ticket-b/revisions/pair-02/baseline.sql:1` and `candidate.sql:1` groups by `account_id`, which also produces at most one row per account and does not match the duplicated mock rows. Use a multiplicity-only difference (same distinct rows, different counts) with SQL that can produce those rows, or add a separate duplicate-only case. This is a partial miss against Task 1's requested “duplicate-sensitive results.”

## Cannot verify

Task 1 adds study evidence and fixtures, not a simulator or resolver. This review cannot verify actual dispatch-stop behavior, symlink/reparse containment, runtime input-root enforcement, or real operation/artifact ownership. The inventory identifies these as future design constraints, and the Task 1 report states that no product command, MCP call, or database was run.

## Scoped checks

- Reviewed the implementer-provided focused evidence; did not rerun the generator, checksum/integrity checks, or tests.
- One outside-diff source-map check was run for the fixed-parameter-file mapping: `rg` over `docs/mcp/inputs.md`, `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`, and `src/SqlHarness.Mcp/McpInputReader.cs`. It confirmed that fixed `--params-json` is CLI-only and MCP exposes `parameterSetFiles` without a fixed-parameter-file argument, as the inventory states.
