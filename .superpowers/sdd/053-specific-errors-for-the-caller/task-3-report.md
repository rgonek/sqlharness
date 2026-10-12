# Plan 053 Task 3 report

## Result

Task 3 is implemented. The journal now persists the caller-facing credential-redacted error message by default, including existing truncation at 4096 characters. SQL text and full plans remain gated by `journal.storeSensitive`. The dashboard displays an unavailable-message note for legacy rows that have no stored message.

README, the SQLHarness skill digest, MCP documentation, and `Capabilities.cs` describe the storage policy. README, the skill digest, MCP docs, and the Contracts pin now say that parameter-set JSON accepts a UTF-8 BOM, JSON comments, and trailing commas while keeping the existing shape and 64 KiB limit. The plan and index status say implementation is complete and controller verification is pending; the plan remains in the active queue.

## Test-first evidence

RED command:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter 'FullyQualifiedName~ActivityJournalTests.Redacted_error_message_is_stored_by_default|FullyQualifiedName~ContractsTests.Documentation_covers_measure_parameter_sets|FullyQualifiedName~CapabilitiesCommandTests.Capabilities_reports_build_engines_limits_and_command_support_without_dispatch' --verbosity minimal
```

Result: 3 failed, 0 passed. The expected error message was `null`, the capability field was absent, and docs lacked the accepted `UTF-8 BOM` form.

GREEN journal/contracts/capability verification:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter 'FullyQualifiedName~ActivityJournalTests|FullyQualifiedName~ActivityJournalBenchmarkTests|FullyQualifiedName~ContractsTests.Documentation_covers_measure_parameter_sets|FullyQualifiedName~CapabilitiesCommandTests.Capabilities_reports_build_engines_limits_and_command_support_without_dispatch' --verbosity minimal
Passed: 29, Failed: 0
```

This includes the default redacted-message persistence and truncation checks, SQL text remains opt-in, full plans remain opt-in, and updated docs/capability pins.

GREEN dashboard UI check:

```text
npm test -- src/components/StatusBadge.test.tsx
Test Files: 1 passed
Tests: 6 passed
```

`git diff --check` passed. Full Windows and Linux gates were not run; the controller owns those gates.

## Changed files

- Journal storage and contract comments: `src/SqlHarness.Core/Journal/ActivityJournal.cs`, `JournalModels.cs`, `OperationJournalDescriber.cs`.
- Dashboard missing-message copy and test: `src/SqlHarness.Dashboard/ui/src/components/StatusBadge.tsx`, `StatusBadge.test.tsx`.
- Behavior digest and docs: `src/SqlHarness.Core/Capabilities.cs`, `README.md`, `docs/mcp.md`, `skills/sqlharness/SKILL.md`.
- Behavioral pins: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `tests/SqlHarness.Tests/ContractsTests.cs`, `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`.
- Status: `plans/053-specific-errors-for-the-caller.md`, `plans/README.md`.

## Self-review and concerns

The journal has no credential values and does not redact messages itself; `OperationJournalDescriber` receives Core's already-redacted `MachineError` and passes that diagnostic to the journal. Task 1 and Task 2 reports cover known-secret redaction across their error paths. SQL text and plan storage controls were unchanged and their focused journal tests passed. No new security path was introduced. Controller Windows/Linux gates and final queue removal remain pending.
