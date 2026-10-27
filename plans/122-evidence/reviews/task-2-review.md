### Spec Compliance

- ❌ Issues found: operation detail emits new metrics without a single-case context or complete attribution (JournalReader.cs:714, :731, :743); comparison combines LOB evidence by object name while discarding the source kind (JournalReader.cs:607, OptimizationComparer.cs:295). The required edge-case evidence is incomplete (OptimizationComparerTests.cs:8, JournalReaderTests.cs:299).
- ⚠️ Cannot verify from this diff: marker insertion/resumption and migration invariants were assigned elsewhere; exact plan link availability depends on retained plan documents and storeSensitive. UI explanation and presentation are explicitly outside this review's backend-only scope. Reported test results are implementer evidence, not independently rerun.

### Strengths

- JournalReader.cs:667 gates projections on exact session version and SQL Server; JournalReaderTests.cs:299 exercises zero, NULL/unknown marker and PostgreSQL.
- OptimizationComparer.cs:118, :155, :278 keeps new comparison values per side and suppresses them for All cases; JournalBenchmarkBuilderTests.cs:37 verifies selection by reads and timing provenance without changing run selection.
- JournalReader.cs:607 preserves the existing total reads calculation while projecting LOB separately; statement occurrence is retained in OptimizationComparer.cs:284.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- JournalReader.cs:714, :731, :743: OperationStatements loads all statements for an operation and outputs the new metrics for each confirmed row, without checking single-case scope or including case/variant/parameter-set identity in StatementRow. A multi-case operation can therefore present unidentifiable metrics; a confirmed row also exposes new values even when the operation's attribution is partial due to omitted or unconfirmed statements. Gate the new fields on a supported single-case and complete evidence, or provide an explicitly selected case and source identity, and cover both multi-case and partial detail paths.
- JournalReader.cs:607 and OptimizationComparer.cs:295: source `o.kind` is loaded but dropped before new LOB projection, and LobForObject sums rows with the same name regardless of kind. The journal key explicitly distinguishes `(statement_id, object, kind)` (JournalSchema.cs:204); consequently different source objects can be merged and reported as one LOB value, contrary to the object-identity requirement. Carry the full source identity through matching/output, or make the metric unavailable when identity is ambiguous; add a same-name/different-kind test.
- OptimizationComparerTests.cs:8 and JournalReaderTests.cs:299: the new assertions do not establish the explicitly requested mixed old/new integration path, unmatched/hidden work, repeated statement occurrences, missing LOB contribution or multi-case operation detail. Existing tests of old fields do not establish new field behavior. Add targeted behavioral assertions for those scenarios, including no partial LOB sum and preserving correct side/occurrence; the implementer report acknowledges these combinations were not independently exercised.

#### Minor (Nice to Have)

- OptimizationComparerTests.cs:8: the All cases test checks null fields but does not assert StatementMetricsUnavailableReason (OptimizationComparer.cs:155), so the required explanation is unprotected.

### Assessment

**Task quality: Needs fixes.** New projections preserve the selected run and version gate, but operation-detail scope, object identity and required edge-case evidence need correction before this task can be trusted.

**Focused checks:** JournalSchema.cs:197-204 for the named object-identity risk; JournalReader.cs:1067 and OptimizationModels.cs:150 for the named multi-case operation-detail risk. The review package output was initially truncated, so only the cut-off comparer diff hunks were re-read. No tests rerun.
