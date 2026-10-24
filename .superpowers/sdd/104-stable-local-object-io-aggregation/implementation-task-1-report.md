# Plan 104 Task 1 implementation report

Status: DONE_WITH_CONCERNS

The SQL Server STATISTICS IO parser now records typed provenance from decoded raw names. Exact bare `#` plus eight hexadecimal characters form one anonymous-table-variable identity. Names normalized by `TempTableKey` retain local-temp identity. Other names keep Unknown identity; older run data without typed metadata remains Unknown and is not pattern-classified.

Benchmark reporting sums typed counters inside each run before calculating distributions across repetitions. Per-run string keys remain intact. The typed metadata is journal-only and excluded from JSON. If an Unknown object collides with the aggregate display label, the anonymous aggregate is ordered first and keeps `table variables`; the colliding object receives a separate display key.

The regression fixture parses raw IO messages through `StatisticsIoParser` for repeat=2 and repeat=3. It covers two generated names per run, missing object samples, explicit local temp suffixes, stable object names, and long escaped names. Initial RED failed because no aggregate identity was present. Final GREEN passed 34 focused tests.

Evidence: `plans/104-evidence/implementation-task-1/red-focused.log` and `green-focused.log`. Command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~Compare_table_variable_reads_are_summed_within_each_run_before_distributions|FullyQualifiedName~BenchmarkRunTests|FullyQualifiedName~StatisticsParserTests|FullyQualifiedName~StatisticsIoDetailParserTests"`; final result: 34 passed, 0 failed, 0 skipped. `git diff --check` passed. No live database, SQL Server, full gate, or broader suite was run.

Self-review found no task-scope blocker. Task review is pending controller workflow; this Task 1 report does not claim Task 2 journal/saved-metric unification or final collision coverage. The raw-name rule remains the user-accepted experimental rule and is not a Microsoft cross-version guarantee.
