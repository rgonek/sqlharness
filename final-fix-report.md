# Plan 061 final documentation fix

Base: `cc1d571` on `feat/plan-061-parameter-sets`.

## Changes

- `AGENTS.md`: restored CLI `compare --matrix` in the Parameter sets reference row alongside `measure --param-set` and MCP forms.
- `plans/README.md`: marked Plans 060 and 061 done in the execution-order prose and retained Plan 062's separate product-choice status and sequencing guidance.
- `plans/061-verification-evidence.md`: clarified that referenced raw SDD gate logs were temporary working logs removed during authorized cleanup, retained durable command outcomes/counts, and added the fourth final-review scope ruling with rationale and cost.

## Checks

- `git diff --check` — exit 0; no whitespace errors.
- `dotnet test tests/SqlHarness.Tests --no-build --filter FullyQualifiedName~Documentation_covers_shared_measure_and_compare_parameter_sets` — exit 0; 1 passed, 0 failed, 0 skipped.

No full gates or application builds were repeated for this documentation-only fix.

## Self-review

The changes address the two minor whole-review findings and preserve the fourth final-review ruling in durable evidence. The parameter-set CLI grammar and runtime behavior are unchanged. The plan index now agrees with its status table. Historical verification counts and recovery details remain recorded after the temporary raw log paths are annotated as removed. `git diff --check` is clean.
