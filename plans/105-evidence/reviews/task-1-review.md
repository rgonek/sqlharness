# Task 1 Review

### Spec Compliance

- ❌ Issues found: [src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs:83] accepts any XML root named `ShowPlanXML` by local name, and the selectors at lines 85–91 also accept `StmtSimple`, `QueryPlan`, and `MemoryGrantInfo` from any namespace. Such input can be labeled as `sql-server-showplan-query-plan`, despite not being SQL Server Showplan evidence.
- ⚠️ Cannot verify from this task diff: the full `just verify` gate and branch formatting were not run; the task report defers them to the controlling workflow. These are outside Task 1's focused gate.

### Strengths

- [src/SqlHarness.Core/StatementMetrics.cs:717] uses a dedicated source-preserving extraction path and retains plan document and statement locations for attachment, while leaving the legacy aggregate extractor separate.
- [src/SqlHarness.Core/ArtifactReader.cs:134] adds the diagnostic shape additively, and [src/SqlHarness.Core/ArtifactReader.cs:896] preserves older artifacts as unavailable.
- [tests/SqlHarness.Tests/ArtifactReaderTests.cs:736] covers an offline write/read roundtrip with different statement grants, absent and zero values, a second plan document, repeated runs and parameter sets, batch scope, PostgreSQL unavailability, and partial UDF/TVF attribution.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- [src/SqlHarness.Core/Journal/PlanMetricsExtractor.cs:83] Validate the canonical SQL Server Showplan namespace on the root and diagnostic elements. Matching local names alone can turn unrelated XML into apparently available SQL Server measurements. Add a wrong-namespace fixture that stays unavailable.

#### Minor (Nice to Have)

- [tests/SqlHarness.Tests/ArtifactReaderTests.cs:807] The regression does not assert persisted `degreeOfParallelism`, `grantMaxUsedKb`, or `compileCpuMs`, although the frozen contract lists them at [plans/105-evidence/task-1-shape.md:15]. Add direct-value assertions for those fields so the roundtrip locks all six mappings.

### Assessment

**Spec compliance:** Issues found; the required persisted shape and roundtrip are present, but source identification is too permissive.

**Task quality:** Needs fixes.

**Reasoning:** The implementation preserves source scope and statement/run identity, and the reported focused suites cover the requested scenarios. The namespace check is required before the source tag can reliably mean SQL Server Showplan evidence.

**Reported validation:** The task report records RED followed by 216 Core tests and 58 MCP tests passing, plus `git diff --check` succeeding; suites were not rerun during this review per reviewer instructions. See [.superpowers/sdd/105-artifact-diagnostic-evidence/task-1-report.md:53] and [plans/105-evidence/task-1-shape.md:41].
