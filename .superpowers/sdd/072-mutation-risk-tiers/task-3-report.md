# Task 3 implementation report: T-SQL privilege tiers and grantees

## Implemented

- Populated `SqlMutationStatementMap.PrivilegeStatementTypes` with GRANT, DENY, REVOKE, role, user, and application-role statements.
- Added grantee extraction for security principals, `ALTER ROLE ... ADD MEMBER`, and CREATE/ALTER USER. `PUBLIC` is returned as `public`; repeated names remain repeated and statement/control-flow order is retained through nested IF and BEGIN/END blocks.
- Classified recognized privilege statements as `SqlRiskTier.Privilege` under `Mutation`, and attached collected grantees to `SqlSafetyDecision`. Existing Query rejection remains covered.
- Preserved the existing server-scope/rejected-statement classification path.

## TDD evidence

**RED** — Ran `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~SqlSafetyTierTests` before production changes. Result: 5 failed, 45 passed. The combined GRANT/ALTER ROLE test was rejected as `UnsupportedStatement. GrantStatement at line 1`; REVOKE, DENY, CREATE USER, and server-level GRANT CONTROL reported `ReadOnly` instead of `Privilege`. These failures showed the requested behavior was missing.

**GREEN** — Ran `dotnet test tests/SqlHarness.Tests --no-restore --filter "FullyQualifiedName~SqlSafety"` after implementation. Result: 481 passed, 0 failed, 0 skipped. This includes the unchanged safety tests and the new privilege tests for repeated grantees, PUBLIC normalization, nested IF/ELSE source order, server-level GRANT, and Query rejection.

## Files changed

- `src/SqlHarness.Core/SqlMutationStatementMap.cs`
- `src/SqlHarness.Core/SqlSafety.cs`
- `tests/SqlHarness.Tests/SqlSafetyTierTests.cs`

## Self-review

The task changes only the requested classifier/map and focused test file. Query and CompareSetup continue through the same existing statement classifier path; grantee collection is enabled only for Mutation. `git diff --check` passed. No unresolved concerns.