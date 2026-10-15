# Task 2 report: journal phase recording

Implemented journal schema v10, preserving the existing v8 compare and v9 session annotation migrations. Phase aggregates and opt-in bounded events are written before operation completion; `Complete` derives the `other` row from final wall duration. The journaling wrapper opens a recorder only for a real operation handle and records phases on success, cancellation, and exception paths. Strict `diagnostics.enabled` configuration defaults to false and is passed from the existing CLI and MCP config snapshots.

The first focused RED run failed at compile time because the phase aggregate/event types and `RecordPhases` API were not implemented. After implementation, the migration expectations and focused journal/config/module/retention command passed 80 tests. The final phase journal test run passed 6 tests after adding unordered-event input and verifying stored sequence order and the 512-character detail cap.

Full Core verification passed:

```text
dotnet test tests/SqlHarness.Tests --verbosity minimal
Passed: 3562, Skipped: 29, Failed: 0, Total: 3591
Exit code: 0
```

The durable full Core output is in `task-2-verification/full-core.log`; its exit code is in `task-2-verification/full-core.exit`. Global repository gates are outside this task's scope.

## Task 2 review fix: allowlisted event detail

`ActivityJournal` now parses detail objects and reserializes only the fields allowed for each phase: a GUID or null connection ID, one of the four supported auth strategy labels, a positive attempt number, or a nonnegative numeric artifact `fileCount`. Unknown fields are discarded; malformed JSON, non-object JSON, duplicate properties, and invalid known values produce a null detail. The canonical serialized form is written only when it fits within 512 characters, so persistence never clips JSON into an invalid fragment.

RED:

```text
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~ActivityJournalPhaseTests --verbosity minimal
Failed: 4, Passed: 5, Total: 9
```

The failures showed unknown/sensitive fields being stored and malformed, non-object, and invalid known values being accepted.

GREEN:

```text
dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~ActivityJournalPhaseTests --verbosity minimal
Passed: 9, Skipped: 0, Failed: 0, Total: 9
```

Review-fix coverage is in `src/SqlHarness.Core/Journal/ActivityJournal.cs` and `tests/SqlHarness.Tests/Journal/ActivityJournalPhaseTests.cs`. The full Core suite was not rerun for this fix, as requested; the focused suite passed.
