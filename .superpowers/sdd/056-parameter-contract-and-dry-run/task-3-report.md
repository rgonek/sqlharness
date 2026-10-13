# Task 3: Rejections carry their cause

## Implementation

Unsupported statement rejections from query and compare-setup classification now include the ScriptDom statement type and start line. Unproven table-variable detail retains precedence. Parameter validation reports the caught safety exception message, including the caller-provided invalid parameter name/value/type as specified by the current contract.

Updated the MCP validation test to use an explicitly caller-owned invalid value and assert that the detail includes the caller parameter name, type, and value.

## TDD evidence

### RED

Command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafetyTests.Unsupported_statement|FullyQualifiedName~Validate"
```

Result: failed as expected. `Unsupported_statement_names_its_kind_and_line` expected `WhileStatement at line 2`, actual `null`. `Parameter_validation_detail_names_the_rejected_parameter` expected `Detail` to contain `'1bad'`, actual `null`. The other 112 tests passed (114 total).

### GREEN

Command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Validat"
```

Result: passed, 591/591.

Command:

```text
dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~Validate_rejects_a_bad_typed_value_and_reports_caller_parameter_detail"
```

Result: passed, 1/1.

Command:

```text
dotnet test SqlHarness.sln --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Validat"
```

Result: passed. MCP tests 39/39 and core tests 591/591.

`git diff --check` passed.

## Changed files

- `src/SqlHarness.Core/SqlSafety.cs`
- `src/SqlHarness.Core/SqlValidation.cs`
- `tests/SqlHarness.Tests/SqlSafetyTests.cs`
- `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`

## Self-review and concerns

The query and setup paths both produce `WhileStatement at line 2`; existing unproven table-variable detail precedence remains intact. Parameter detail intentionally follows the current caller-visible parameter contract. The old MCP no-echo assertion applied a registered-secret expectation to caller-supplied parameter input, so it now uses a separate caller-owned test value while registered credential redaction coverage remains in place.
## Review fix round 1

### Finding and change

Parameter validation could return a caught message containing a value equal to the resolved profile password. After target resolution, validation now registers the resolved credential with `OperationSecrets.AddCredentials` and redacts only exact registered credential matches from the exception message using `SecretRedactor.Redact`. Caller-owned parameter details remain visible when they do not exactly match a configured credential. A focused regression supplies the configured profile password as an invalid `int` value and verifies that the value becomes `[REDACTED]` while the parameter name and type remain in the detail. The existing MCP caller-owned detail test remains intact.

### TDD evidence

RED command:

```text
dotnet test tests/SqlHarness.Tests --no-restore --filter "FullyQualifiedName~Parameter_validation_detail_redacts_a_value_matching_the_resolved_profile_password"
```

Result: failed as expected. `Assert.DoesNotContain` found `profile-password-used-as-bad-int-value` in `Detail` at position 15.

GREEN commands and results:

```text
dotnet test tests/SqlHarness.Tests --no-restore --filter "FullyQualifiedName~Parameter_validation_detail_redacts_a_value_matching_the_resolved_profile_password|FullyQualifiedName~Parameter_validation_detail_names_the_rejected_parameter"
```

Passed 2/2.

```text
dotnet test tests/SqlHarness.Mcp.Tests --no-restore --filter "FullyQualifiedName~Validate_rejects_a_bad_typed_value_and_reports_caller_parameter_detail"
```

Passed 1/1.

```text
dotnet test tests/SqlHarness.Tests --no-restore --filter "FullyQualifiedName~CredentialRedactionTests"
```

Passed 26/26.

```text
dotnet test SqlHarness.sln --no-restore --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Validat"
```

Passed: MCP 39/39; core 592/592.

Changed files in this fix: `src/SqlHarness.Core/SqlValidation.cs` and `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`.
