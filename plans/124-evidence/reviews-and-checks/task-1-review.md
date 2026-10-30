### Spec Compliance

- ✅ Spec compliant for Task 1: nullable profile mode and writer-only inheritance are implemented in `src/SqlHarness.Core/Targets/TargetProfile.cs:18` and `src/SqlHarness.Core/Targets/ProfileStore.cs:88`; effective mode and readiness are resolved centrally in `src/SqlHarness.Core/Mutations/MutationPolicyResolver.cs:10` and projected to dashboard and capabilities at `src/SqlHarness.Dashboard/DashboardProfiles.cs:77` and `src/SqlHarness.Core/Capabilities.cs:178`. Submission and execution both reject unacknowledged production/staging YOLO DML at `src/SqlHarness.Dashboard/Mutations/MutationEndpoints.cs:127` and `src/SqlHarness.Dashboard/Mutations/MutationExecutor.cs:54`.
- ⚠️ Complete UI integration and the full verification gate belong to later tasks; this review does not assert their completion. The report describes successful focused runs but does not attach their complete output, so warning-free output cannot be independently established from the supplied evidence. The initial MCP baseline was canceled; its subsequent focused run is reported green.

### Strengths

- `src/SqlHarness.Core/Mutations/MutationPolicyResolver.cs:12` resolves policy from metadata only, preserves explicit overrides, and separates writer presence from the selected mode.
- `tests/SqlHarness.Tests/Targets/MutationPolicyResolverTests.cs:9`, `tests/SqlHarness.Tests/Dashboard/MutationExecutorTests.cs:17`, and `tests/SqlHarness.Tests/Dashboard/DashboardProfilesTests.cs:10` cover global modes, dangerous inherited YOLO environments, and a changed global default without rewriting the profile.
- `src/SqlHarness.Dashboard/Mutations/MutationExecutor.cs:48` re-resolves the current policy at execution, while `src/SqlHarness.Dashboard/Mutations/MutationExecutor.cs:73` independently fails closed when no writer exists.

### Issues

#### Critical (Must Fix)

- None found.

#### Important (Should Fix)

- None found.

#### Minor (Nice to Have)

- None found.

### Assessment

**Task quality:** Approved.

**Reasoning:** The scoped implementation is consistent with the requested inheritance and security contract. Focused cross-cutting checks addressed the risk of remaining direct mode fallbacks (`rg` across `src`; consumers use the resolver or report global configuration) and missing writer enforcement (`MutationExecutor.cs:73`); tests were not rerun. The full gate and MCP baseline limitation remain as disclosed above.
