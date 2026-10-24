# Plan 106 gate failure diagnosis

Date: 2026-10-23
Starting revision: `43d0a189903cf6c73aa6bc1d0608693281794bdc`
Branch base for formatting: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`

## Initial Windows gate

The controller's initial `just verify -LogDirectory D:/temp/sqlharness-plan106-final-verify` run exited 1 in `test-SqlHarness.Tests.log`: 4,659 passed, 2 failed, 11 skipped. MCP passed 516 with 4 skipped; UI passed 372 and build passed with 0 warnings. The archived log remains at `D:/temp/sqlharness-plan106-final-verify/test-SqlHarness.Tests.log` and under the controller-owned `plans/106-evidence/windows-gate-initial-failure/` archive.

## CLI help assertion

Cause: the test compared the flattened terminal help text against the source constant, including its final period. Spectre's option-table rendering wrapped the description and omitted that trailing period at the rendered boundary. The full semantic clauses were present; this was a presentation-format mismatch, not missing contract text.

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~Query_help_explains_error_text_cell_limit"` — failed before correction (1 failed).
- `dotnet src/SqlHarness.Cli/bin/Debug/net10.0/sqlharness.dll query --help` with redirected output — confirmed all five semantic clauses were emitted, but the last ended in `applies` without the source constant's final period.
- The assertion now checks the five clauses independently after whitespace normalization, retaining coverage for range, default, redaction, path and byte-budget semantics.
- Same focused `dotnet test` command after correction — passed (1 passed, 0 failed).

No CLI behavior or shared description changed.

## ProcessRunner test

The initial gate reported `PidPublicationError_RetriesErrorMarkerAfterTransientSharingViolation` timing out after 480 attempts. At failure, the PID file had one line and the publisher task exited 1. This is outside Plan 106's changed code.

- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~PidPublicationError_RetriesErrorMarkerAfterTransientSharingViolation"` — passed on isolated reproduction (1 passed, 0 failed, 1 second).

The failure did not reproduce, so its cause remains unproven. No ProcessRunner source or test changes were made.

## Scoped verification

- `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 6d543e509734a374caf882bc8c05ffe5ccaa98b2` — first reported the repository's required no-final-newline convention on `SmokeTests.cs`; after restoring that convention, rerun exited 0 with `format: OK` (13 changed C# files checked).
- `git diff --check` — exit 0.
- No full gate rerun was performed here. The controller owns the next gate and final evidence closeout.