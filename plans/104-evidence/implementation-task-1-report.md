# Plan 104 Task 1 implementation report

Status: DONE_WITH_CONCERNS

The SQL Server STATISTICS IO parser now records typed provenance from decoded raw names. Exact bare `#` plus eight hexadecimal characters form one anonymous-table-variable identity. Names normalized by `TempTableKey` retain local-temp identity. Other names keep Unknown identity; older run data without typed metadata remains Unknown and is not pattern-classified.

Benchmark reporting sums typed counters inside each run before calculating distributions across repetitions. Per-run string keys remain intact. The typed metadata is journal-only and excluded from JSON. If an Unknown object collides with the aggregate display label, the anonymous aggregate is ordered first and keeps `table variables`; the colliding object receives a separate display key.

The regression fixture parses raw IO messages through `StatisticsIoParser` for repeat=2 and repeat=3. It covers two generated names per run, missing object samples, explicit local temp suffixes, stable object names, and long escaped names. Initial RED failed because no aggregate identity was present. Final GREEN passed 34 focused tests.

Evidence: `plans/104-evidence/implementation-task-1/red-focused.log` and `green-focused.log`. Command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~Compare_table_variable_reads_are_summed_within_each_run_before_distributions|FullyQualifiedName~BenchmarkRunTests|FullyQualifiedName~StatisticsParserTests|FullyQualifiedName~StatisticsIoDetailParserTests"`; final result: 34 passed, 0 failed, 0 skipped. `git diff --check` passed. No live database, SQL Server, full gate, or broader suite was run.

Self-review found no task-scope blocker. Task review is pending controller workflow; this Task 1 report does not claim Task 2 journal/saved-metric unification or final collision coverage. The raw-name rule remains the user-accepted experimental rule and is not a Microsoft cross-version guarantee.

## Fix round 1/5 (base `26401c26`)

**Review finding addressed:** P2 — incomplete IO evidence was counted as a zero sample. `BenchmarkReports.CreateVariantReport` now omits a distribution for any producer-derived non-Unknown identity when any repetition lacks typed provenance, and adds the explicit `TableIoDistributionUnavailable` warning. A complete typed run still contributes zero for an identity genuinely absent from that run. SQL Server attaches typed metadata only when the consumed message set is complete and no messages were omitted; incomplete or truncated evidence leaves provenance null. Legacy string maps remain Unknown and are not reclassified.

A behavior-level regression built a complete typed anonymous-variable run and a second legacy/incomplete run. Before the fix, the focused test failed because `table variables` appeared in the distribution. It now asserts the distribution is omitted and the warning is present. The existing repeat=2/3 test continues to assert zero samples for genuinely absent objects in complete typed runs.

RED command:
`dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter FullyQualifiedName~Compare_omits_typed_io_distribution_when_a_run_has_incomplete_provenance --logger "console;verbosity=normal"`

RED output: **Test Run Failed; Total tests: 1; Failed: 1, Passed: 0.** Failure: `Assert.DoesNotContain()` found `table variables` in the distribution keys `['table variables', '#D4C3B2A1']`. Raw output: `plans/104-evidence/implementation-task-1/fix-round1-red.log`.

GREEN command:
`dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~BenchmarkSummaryTests|FullyQualifiedName~BenchmarkRunTests|FullyQualifiedName~StatisticsParserTests|FullyQualifiedName~StatisticsIoDetailParserTests" --logger "console;verbosity=minimal"`

GREEN output: **Passed! Failed: 0, Passed: 57, Skipped: 0.** Raw output: `plans/104-evidence/implementation-task-1/fix-round1-green.log`. `git diff --check` passed. No full suite, live database, SQL probe, or full gate was run.
