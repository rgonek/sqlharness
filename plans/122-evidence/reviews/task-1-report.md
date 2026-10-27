# Task 1 report — dashboard evidence boundary

Base 43720796; commit bc56c0c0. Controller baseline: JournalContractTests 12 passed, 0 failed/skipped, exit 0, before edits.

## TDD

RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter "FullyQualifiedName~Dashboard_evidence_|FullyQualifiedName~Operation_json_exposes_session_evidence_version" --logger "console;verbosity=normal"` exited 1; 3 failed, each `SQLite Error 1: no such column: dashboard_evidence_version` (task1-red.log).

First GREEN attempt: same command, 2 passed, 1 failed. The test's file timestamp assertion was invalid for SQLite WAL checkpoint activity; replaced it with a held connection's `PRAGMA data_version` before/after to detect database commits. No product change was needed for this failure (task1-green.log).

Final focused GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:SkipDashboardUi=true --filter "FullyQualifiedName~Dashboard_evidence_|FullyQualifiedName~Operation_json_exposes_session_evidence_version|FullyQualifiedName~JournalContractTests|FullyQualifiedName~MutationJournalTests|FullyQualifiedName~JournalReaderTests" --logger "console;verbosity=minimal"` exited 0, passed 99, failed 0, skipped 0 (task1-green-final.log). Migration dictionary entry reordered after compilation to numerical order only; no executable semantics changed. `git diff --check` passed. Full `just verify` reserved for controller.

## Changes and review

- `JournalSchema.cs`: version 22, nullable column in fresh schema and additive ALTER migration without default or UPDATE.
- `ActivityJournal.cs`: INSERT supplies 1; conflict update does not touch marker. New operations inherit session version through join.
- `DashboardModels.cs`, `JournalReader.cs`: nullable operation property and JSON camel-case serialization; joins project session marker, shifted vars_json reader index to 43. All uses of shared OperationColumns reviewed. No new projection yet; following tasks must gate their fields by exact supported version, not merely non-null.
- `ActivityJournalTests.cs`: migration preserves old session/operation fields, old and resumed operations stay NULL, new key is 1; concurrent same-key new inserts produce one marked session and eight operations.
- `JournalReaderTests.cs`: marker JSON values and no database commit during reads.
- `JournalContractTests.cs`, `MutationJournalTests.cs`: schema pin updated; simulated old schema drops column before migration. DashboardModels and MutationJournalTests additions outside brief file list are necessary for requested backend interface and schema pin.

Self-review: no backfill, history UPDATE, time cutoff, UI changes, mutation policy changes or live database use. Tests simulate v21 by dropping the added column; values checked before and after migration and old session upsert. Concurrency tested for a new key, sequential resume tested for old key. Read-only assertion uses data_version; SQLite can change file metadata due to checkpoint without a logical commit. Concurrent old-key upsert is not explicitly tested; SQL ON CONFLICT omits marker and transaction semantics are shared with concurrent fresh-key test. No other known concern. Scratch logs not staged.
