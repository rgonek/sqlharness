### Spec Compliance

- ❌ Issues found: `src/SqlHarness.Core/StatementMetrics.cs:723` caps each row's hidden-work list at 16 with `.Take(...)`, but exposes no omitted-item count or truncation flag. `NameTruncated` only marks a clipped name; callers cannot tell whether the list itself is complete. The generic counter limitation does not disclose projection omissions. Add explicit hidden-work omission metadata.
- ⚠️ Cannot verify from this diff: Task 3's compact measure/compare responses and MCP workflow/docs remain pending in `.superpowers/sdd/103-agent-evidence-completeness/task-2-report.md:48`.

### Strengths

- The projector keeps one row per repetition with variant, set, cell, statement ordinal, and repetition, and derives counter attribution through the shared reconciler/classifier (`src/SqlHarness.Core/StatementMetrics.cs:706`, `:742`, `:743`).
- Versioned coverage metadata limits its claim to collected statement counters, while hidden UDF/TVF rows retain explicit cost limitations (`src/SqlHarness.Core/StatementMetrics.cs:749`, `:769`, `:775`).
- The offline reader keeps legacy rows unavailable and handles current evidence pagination (`src/SqlHarness.Core/ArtifactReader.cs:788`, `:798`, `:814`). The writer serializes the projector output to `statements.json` (`src/SqlHarness.Core/Artifacts.cs:556`).
- The report records 207/207 focused tests and successful formatting (`.superpowers/sdd/103-agent-evidence-completeness/task-2-report.md:33`, `:38`); its tests cover legacy/PostgreSQL absence, incomplete identity, long-name clipping, continuation, and node-ID collisions (`task-2-report.md:36`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `src/SqlHarness.Core/StatementMetrics.cs:723`: Hidden-work items beyond 16 disappear without a count or flag. This makes the bounded projection's list appear complete even when known function rows were dropped. Expose the number of omitted items or a truncation marker, while retaining the existing name-level `NameTruncated` field.

#### Minor (Nice to Have)

None.

### Assessment

**Spec verdict:** Needs fix.

**Code quality verdict:** Needs fixes.

**Task quality:** Needs fixes.

**Reasoning:** The statement identity, counter semantics, legacy behavior, and paging are implemented cleanly. The silent hidden-work list truncation leaves a required evidence-completeness gap.

### Checks

- Focused outside check: `src/SqlHarness.Core/Journal/StatementReconciler.cs:12` defines attribution as collected-counter completeness independent of hidden function work.
- Focused outside check: `src/SqlHarness.Core/AgentOutputProjection.cs:584` bounds the returned statement page by serialized bytes and records omitted rows; `src/SqlHarness.Core/ArtifactReader.cs:219` and `:285` retain the 16 MiB read limit and 128-row default page.
- No tests rerun, per reviewer contract. The report says the full `just verify` gate remains controller-owned (`.superpowers/sdd/103-agent-evidence-completeness/task-2-report.md:50`).
- Cannot verify here: whole-branch gate and Task 3 acceptance.
