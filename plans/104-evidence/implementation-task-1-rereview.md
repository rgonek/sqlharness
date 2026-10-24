# Plan 104 Task 1 fixround 1 rereview

Reviewed the Task 1 brief and report plus fixed package `26401c26..71e9f809`. This was a scoped rereview of the prior P2 finding and new breakage in this fix only.

## Verdict

**Prior P2 finding: ADDRESSED. No new confirmed blocker.** The benchmark now skips producer-derived non-Unknown distributions when any run lacks typed provenance and adds `TableIoDistributionUnavailable` before sample construction (`BenchmarkRunner.cs`, fix diff lines 123–128). This avoids treating legacy or incomplete evidence as zero. Legacy string-map keys remain Unknown. The added regression covers one typed anonymous-variable run paired with a legacy run whose generated-looking key must remain Unknown, and the existing complete-run test still expects zero for a genuinely absent object.

The packaged RED1 log shows the new regression failing because `table variables` was present; the GREEN log reports 57 passed, 0 failed, 0 skipped. I did not rerun tests, per instruction.

## Cannot verify

`SqlServerDialect` now attaches typed logical-read identities only when both `OmittedMessageCount == 0` and `TableIoComplete` is true (fix diff line 156). This is conservative and prevents incomplete evidence from becoming zero. From the allowed brief/report/diff alone, I cannot establish whether `TableIoComplete` exactly tracks completeness of the simpler `StatisticsIoParser` logical-read data. If it only tracks the separate detailed table-IO parser, valid logical-read aggregates may be suppressed when that parser is incomplete. No confirmed breakage is established in this scoped pass.

Task 2 journal/persisted behavior and Task 3 consumers remain later work. The package reports `git diff --check` passed; it was not rerun here.
