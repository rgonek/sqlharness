# Observation: runtime list validation differs from inspected source

Recorded 2026-10-23 against repository HEAD `fdc161d1`.
Status: collecting observations, not approved for implementation.

## Observed evidence

Two investigation compare requests used the same typed list on baseline and candidate, with named
parameter sets. The bare IN form produced `safety_rejected` / `ParseError`. After adding parentheses,
the response reported a collision with an expanded parameter that the caller had not supplied.
The investigation stopped that branch; no scalar substitution or safety bypass followed.

During the retrospective, two synthetic offline compare dry runs reproduced those error shapes.
Both variants were identical, each named set supplied one integer list value, and no scalar parameter
was supplied. One SQL form was `SELECT 1 AS Value WHERE 1 IN @Ids;`; the other was
`SELECT 1 AS Value WHERE 1 IN (@Ids);`. The latter response named `@Ids1` as colliding with another
supplied parameter. These dry runs did not connect to a database. They demonstrate the installed
runtime symptom, not a successful end-to-end benchmark or the identity of the installed build.

## Current source and coverage

- [CompareCellRunner.cs](../../src/SqlHarness.Core/CompareCellRunner.cs), lines 75-84, expands each
  SQL text and deduplicates representative list bindings before combining them with scalar bindings.
- [SqlSafety.cs](../../src/SqlHarness.Core/SqlSafety.cs), lines 110-140, defines collision validation
  and deduplication, including rejection of inconsistent representative bindings.
- [CompareTests.cs](../../tests/SqlHarness.Tests/CompareTests.cs), test
  `Compare_dry_run_deduplicates_a_list_shared_by_multiple_SQL_texts`, covers shared lists.
- [ListParameterExpanderTests.cs](../../tests/SqlHarness.Tests/ListParameterExpanderTests.cs), test
  `Expands_a_list_in_both_supported_IN_forms_with_typed_parameters`, covers both IN forms.
- Deduplication is present in source history at `ee75266dd`. Its presence on HEAD is not proof that
  the server used during the investigation loaded it. No installed binary/source match was verified.
- [Plan 083](../../plans/083-table-and-list-parameters.md) owns typed-list support and describes
  representative expansion before classification. Its local implementation status is separate from
  installed behavior; do not create a duplicate implementation plan on this evidence alone.

Classification: confirmed runtime/source divergence; provisional defect relative to the documented
list contract. The cause may be installation drift or another uncovered path; neither is established.
Confidence is high in the reproduced symptom and source inspection, limited for the cause. Existing
source tests were read, not rerun in this documentation-only retro.

## Open question and cheapest check

Identify the actual loaded build and repeat the same synthetic offline requests against that build
and a known build of the inspected source, including named sets and identical baseline/candidate SQL.
A source-only passing test does not identify the installed cause. Do not install, restart, bypass
validation or rerun a live workload merely to complete this observation.

See the [session-report entry](../superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-23---multi-scope-read-batch-candidate-and-changing-evidence-coverage).
This record selects no implementation or deployment change.
