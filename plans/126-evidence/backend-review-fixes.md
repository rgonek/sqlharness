# Backend review corrections

Status: fixes and C# formatting pass; backend source is frozen for fresh review.
This is not fresh reviewer approval or a full gate.

Scope: P1 in [task review](task-review-backend.md), the actual seeded-journal
operation-heading numbering defect, and exact selected-pair scans requested by the
comparison implementer/coordinator.

## Reproduced failures

- Initial focused run: **8 failures, exit 1**. Direct and independent duplicate
  measurement fingerprints selected the last case's reads/timing; unique-case
  coverage hid the ambiguous contributors; duplicate case metric rows selected the
  last representative; operation detail allocated a version for a preceding failed
  attempt. [Output](backend-review-red.log).
- Additional contract run: **6 failures, exit 1**. Ambiguous groups undercounted
  retained cases, selected cross-condition actions remained resolvable despite
  duplicate representatives, and scans were absent from the exact direct pair.
  [Output](backend-review-contract-red.log).

## Corrections

- Snapshot identity collisions and duplicate representatives produce explicit null
  placeholders, not an arbitrary metric row. Both direct and independent paths keep
  the unavailable case option and explain its coverage. A unique selected case next
  to the ambiguous group retains its own metrics.
- All-case actions retain every original case ID. Fingerprint lookup contains only
  uniquely resolvable keys; explicitly selecting an ambiguous key returns no action
  and a readable reason. The frontend owner was notified that a present fingerprint
  dictionary is authoritative and must not fall back to a colliding raw case ID.
- Ambiguous outcome groups carry `caseCount` (default 1). Counts describe all retained
  contributors; a labelled group of two unavailable cases counts as two unavailable
  outcomes. No policy is borrowed from one case in that group.
- Version-table own per-case timings remain retained. Duplicate metric representatives
  for a single case are unavailable in summaries too.
- Session optimization, session history, operation list labels and operation detail
  use the same eligibility decision and frozen label mapping. Failed attempts remain
  readable without allocating a fresh label. Existing frozen labels and reserved
  numbering gaps are retained. All projections use the same SQLite read snapshot.
- Comparison scans use the exact selected baseline/candidate snapshots and existing
  sum/mean aggregation, matching CPU/elapsed and null ambiguity semantics.

## Evidence boundary

The initial implementation's tests and gate evidence remain historical.

- Intermediate focused backend run: **207 passed**, exit 0.
  [Output](backend-review-focused.log).
- Metric-only collision additionally reproduced missing coverage (exit 1), then
  received the same conservative placeholder/raw-ID handling.
  [Red output](backend-review-metric-identity-red.log).
- Broader Dashboard run excluding UI asset tests: **572 passed, 2 failed, 3 skipped**.
  [Output](backend-review-dashboard.log). One history endpoint fixture still had no
  measurements; it now seeds genuine synthetic representatives without changing
  its version/paging assertions. One unchanged lifecycle HTTP deadline failed under
  parallel load; isolated rerun passed both endpoint-status cases in 264 ms. No
  lifecycle product/test changes were made.
- Final focused run after fixes: **260 passed**, exit 0. Command:
  `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore -p:SkipDashboardUi=true --filter 'FullyQualifiedName~Dashboard.Optimization|FullyQualifiedName~Dashboard.JournalReaderTests|FullyQualifiedName~Dashboard.CompareHistoryBuilderTests|FullyQualifiedName~Agent_optimization|FullyQualifiedName~Dashboard.DashboardServerTests|FullyQualifiedName~Old_or_unsupported_endpoint_returns_Failed_with_pid_guidance' -v minimal`.
  [Output](backend-review-final-focused.log). This includes the corrected API fixture,
  lifecycle cases, strict/frozen labels, raw script scope and scans provenance.
- `pwsh -NoProfile ./scripts/format-changed.ps1 -Fix`: **11 changed C# files**,
  `format: OK`, exit 0. [Output](backend-review-format-working.log). Its completed
  output and the 260-test result were rechecked after the usage-limit reset; the
  completed suites were not rerun.

The coordinator owns a fresh review, final Windows gate and actual browser reruns.
No real workload, mutation approval, push or publication is part of these fixes.
