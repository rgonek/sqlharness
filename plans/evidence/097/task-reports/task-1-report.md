# Task 1: Freeze singleton semantics and boundary failures

## Status

Completed the regression-test stage with the requested intended RED. No product code was changed.

## Changes

- Added named-set validation boundaries for 0, 1, 2, 32, and 33 cases. Singleton fixtures cover a scalar typed NULL, a SQL Server TVP, and a SQL Server list parameter.
- Added matrix parsing and resolver boundaries for 0, 1, 2, 32, and 33 values, including preservation of matrix index and parameter value identity.
- Updated the CLI regression for one named parameter-set file to require dispatch as one named set. Added 32/33-file forwarding checks that prove the CLI does not silently partition the request; the ordinary no-file path remains covered.
- Added module-level singleton measure coverage for setup-once/repeat behavior, typed metadata/hash, report and run identity, and subject/scope journal context.
- Added singleton compare coverage requiring both variants, the named case, matrix index, artifact directory and subject/scope context.
- Changed the explicitly empty parameter-set collection case to require a pre-connect safety rejection, and added singleton failure coverage requiring one failed case with zero phantom not-executed cases.

## Verification

Controller baseline before edits:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ParameterSet|FullyQualifiedName~SqlParameterMatrixTests"
Passed: 280/280; exit 0.
```

Task 1 RED command and persisted output:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ParameterSet|FullyQualifiedName~SqlParameterMatrixTests" --logger "console;verbosity=minimal"
Failed: 13, Passed: 286, Total: 299; exit 1.
Log: .superpowers/sdd/097-singleton-parameter-batches/task-1-red.log
```

The REDs are expected for this stage: singleton matrix/set binding and CLI dispatch are still rejected; an explicitly empty collection is still treated as the ordinary single-query path; and the existing 33-case errors do not yet carry split guidance. The singleton execution, failure-accounting, and compare assertions are reached only after the corresponding current pre-connect singleton rejection is removed.

`git diff --check` passed. No full suite or product verification was run because Task 1 is test-only and requires intended RED.

## Files changed

- `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`
- `tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs`
- `tests/SqlHarness.Tests/MeasureParameterSetValidationTests.cs`
- `tests/SqlHarness.Tests/SqlParameterMatrixTests.cs`
- `.superpowers/sdd/097-singleton-parameter-batches/task-1-red.log`

## Self-review and concerns

- The cases use the repository's existing module/session/artifact fakes. The compare singleton uses the same read-only SQL for both variants so the existing measure fake can provide deterministic results; the test still checks that baseline and candidate runs both retain set and matrix identity.
- The full RED confirms the expected pre-connect guards. Assertions after successful singleton execution will become active when Task 2 removes those guards.
- No outstanding implementation concern for this test stage.

## Fix round 1

Expanded the singleton compare test to verify that both baseline and candidate commands receive the typed `@id = 11` value. It now uses the existing `CompareArtifactWriter` and `ActivityJournal` to assert the matrix artifact's index, set name, artifact ID, subject/scope, and stored operation/case fingerprints and reproduction identity. No product code or test infrastructure was added.

Command:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ParameterSet|FullyQualifiedName~SqlParameterMatrixTests" --logger "console;verbosity=minimal"
```

Actual output:

```text
Failed: 13, Passed: 286, Total: 299; exit 1.
The compare singleton test compiled and reached its expected pre-connect Safety rejection at the current singleton-count guard. All 13 failures remain the intended Task 1 REDs.
Log: .superpowers/sdd/097-singleton-parameter-batches/task-1-red-fix1.log
```

`git diff --check` passed. The identity assertions past the singleton guard remain unexecuted until Task 2 enables singleton calls.
