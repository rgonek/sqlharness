# ADR T2: typed operation result stays internal, `object? Report` stays public

**Status (2026-10-08):** ACCEPTED (decyzja obowiązuje). Follow-upy poniżej świadomie odroczone, bez planu.

Date: 2026-09-28. Scope: plan 05 task T2, checkbox 4.

## Decision

`SqlHarnessOutcome.Report` keeps its public `object?` type (compatible
adapter, no versioning break). The typed contract is enforced internally:

- `src/SqlHarness.Core/OperationReportContract.cs` maps each of the 14
  `SqlHarnessOperation` types to its allowed report type(s)
  (`Measure` allows `SqlHarnessMeasureReport` and
  `SqlHarnessMeasureSetReport`; failure outcomes carry `null`).
- The facade dispatch wraps every return in `Checked(...)`, so a mismatched
  operation/report pair throws `InvalidOperationException` instead of
  flowing to renderers.
- `ContractsTests.Every_operation_accepts_only_its_own_report_types`
  covers all 14 x 15 pairs; `Report_contract_covers_the_closed_operation_family`
  fails if a new operation is added without a contract entry.

## Why not a fully typed public result

Replacing `object? Report` with a generic/union result would ripple through
`ISqlHarnessModule`, the CLI renderers, `AgentOutputProjection`, artifact
writers and every test fake — a wide migration for no behavior gain, and a
public API break without versioning. Per the plan fallback, the adapter plus
this note is the scoped outcome.

## Follow-ups (not T2)

- `ISqlSession.Identity` keeps `{ get; set; }`; only the read-only flow is
  tested (`Compare_never_reassigns_session_identity_after_connect`).
  Getter-only still requires migrating ~20 test fakes and both production
  factories — deferred.
- If renderers ever need exhaustiveness over report types, revisit a
  closed union here; the contract map is the single place to extend.
