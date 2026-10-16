# Task 2 report: Core mutation request and approval gate

## Scope delivered

- Added mutation status values and policy decisions for `never`, `operator`, and `yolo`.
- Added an immutable `MutationRequest` snapshot with an ordinally ordered vars map, copied parameter and grantee lists, and a lowercase SHA-256 digest over canonical JSON. Parameter entries include name, type, bound size, precision, scale, and value.
- Added the approval proof type, broker client/result contracts, and mutation operation.
- Added Core dispatch that classifies with `SqlUsage.Mutation`, rejects read-only/session batches, checks `ExpectDatabase`, supports dry run, returns a prepared request without connecting, and verifies the digest before routing an approved request through query execution with the approved writer credential.
- Registered mutation operation report types and updated the closed-operation contract test.
- Preserved Task 1's test friend assemblies; no CLI or MCP internals were granted.

## Changed files

- `src/SqlHarness.Core/Mutations/MutationPolicy.cs`
- `src/SqlHarness.Core/Mutations/MutationRequest.cs`
- `src/SqlHarness.Core/Mutations/ApprovedMutation.cs`
- `src/SqlHarness.Core/Mutations/IMutationBrokerClient.cs`
- `src/SqlHarness.Core/Contracts.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Core/OperationReportContract.cs`
- `tests/SqlHarness.Tests/Mutations/MutationPolicyTests.cs`
- `tests/SqlHarness.Tests/Mutations/MutationRequestTests.cs`
- `tests/SqlHarness.Tests/Mutations/MutationOperationTests.cs`
- `tests/SqlHarness.Tests/ContractsTests.cs`

## TDD and verification evidence

RED command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~MutationPolicyTests|FullyQualifiedName~MutationRequestTests|FullyQualifiedName~MutationOperationTests" --no-restore
```

Result: failed at compile time because the requested policy, status, request, and operation APIs were missing. This was the expected pre-implementation failure.

GREEN scoped regression command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~MutationPolicyTests|FullyQualifiedName~MutationRequestTests|FullyQualifiedName~MutationOperationTests|FullyQualifiedName~Query|FullyQualifiedName~Postgres" --no-restore
```

Result: `Passed: 1274, Skipped: 11, Failed: 0, Total: 1285`. The skipped tests are existing opt-in PostgreSQL integration proofs. The filter includes mutation policy and digest tests, no-connect and digest-mismatch operation cases, SELECT and session-local rejection, database mismatch exit 4, dry-run `dml`, Query regressions, and PostgreSQL regressions.

`git diff --check` completed with no whitespace errors.

The full repository gates are outside Task 2's scope and remain for the final plan gates.

## Review notes

- No new per-field size caps were added; existing query bounds remain authoritative.
- The unapproved path returns a prepared request and never opens a database session. Approval mismatch is rejected before connection. Approved execution reuses the existing query execution and PostgreSQL approved-mutation mode.
- No unresolved Task 2 concerns found in the scoped review.
