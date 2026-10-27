### Spec Compliance

- **Approved.** The nullable v22 migration and fresh schema have no default (`src/SqlHarness.Core/Journal/JournalSchema.cs:15`, `:44`); insert sets version 1 while the conflict update leaves it unchanged (`src/SqlHarness.Core/Journal/ActivityJournal.cs:1123`). The operation projection exposes the session marker (`src/SqlHarness.Dashboard/JournalReader.cs:81`, `src/SqlHarness.Dashboard/DashboardModels.cs:73`), with the appended reader index adjusted (`JournalReader.cs:927`). Migration, resumed keys, concurrent new-key upserts, JSON and read-only behavior are exercised (`tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs:19`, `:31`; `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs:18`).
- **Cannot verify:** exact-version gating of the later new projections and writes spans Tasks 2–5; controller should review those changes against the marker boundary. The complete verification gate is outside this task review.

### Strengths

- The migration preserves pre-existing session and operation values and tests the old-key resume path (`tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs:47`). The GET test uses SQLite data_version to detect commits (`tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs:32`).
- Reported focused test evidence is present in `task1-green-final.log`: 99 passed, 0 failed, 0 skipped; no warnings shown. Tests were not rerun.

### Issues

- **Minor — `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs:19`:** concurrency is tested for a new session key, while resuming an old key is only tested sequentially. A concurrent old-key case would strengthen regression coverage of the most sensitive boundary; the conflict clause itself does not update the marker.

### Assessment

**Task quality: Approved.** The schema and upsert implement the requested boundary directly, and focused tests exercise both migration and API behavior. Later feature-specific gating remains for subsequent task reviews.
