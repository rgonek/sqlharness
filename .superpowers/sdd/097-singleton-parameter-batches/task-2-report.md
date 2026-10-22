# Task 2 implementation report

Implemented the 1..32 parameter-batch contract. A singleton named set, parameter-set file list, or matrix value is accepted and retains its supplied case identity in reports and run artifacts. An explicitly empty collection remains invalid; omitted batch inputs retain the ordinary single-query path. Inputs above 32 fail before a database connection with split-call guidance, including support for a singleton remainder.

## Verification

Focused Core gate:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~ParameterSet|FullyQualifiedName~SqlParameterMatrixTests"
Passed! - Failed: 0, Passed: 300, Skipped: 0, Total: 300
```

MCP schema, mapper, and served-tool gate (explicit `-p:IsTestProject=true` so test discovery is active):

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpParameterBatchTests|FullyQualifiedName~McpMappingTests"
Passed! - Failed: 0, Passed: 151, Skipped: 0, Total: 151
```

The dedicated batch-test discovery filter found and passed all three cases, including both fixed/request scope variants:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~McpParameterBatchTests" --logger "console;verbosity=detailed"
Discovered: SqlHarness.Mcp.Tests
Passed: Served_fixed_and_request_tools_accept_named_and_matrix_singletons_offline(requestScope: False)
Passed: Served_fixed_and_request_tools_accept_named_and_matrix_singletons_offline(requestScope: True)
Passed: Explicit_empty_collection_is_rejected_and_33_cases_get_split_guidance
Total tests: 3; Passed: 3
```

Focused compare identity regression:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Compare_singleton_parameter_set_executes_both_variants_and_keeps_case_identity"
Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

MCP tool-list size check:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config" --logger "console;verbosity=detailed"
annotations-off tools/list: 28564 UTF-8 bytes
annotations-on tools/list: 32172 UTF-8 bytes of 32768
Passed: 1; the fixed/request annotation schema budget holds.
```

Before description compaction, the request annotation-enabled listing was 32809 bytes and failed the 32768-byte cap. The final listing is 32172 bytes. `git diff --check` passed.

## Changes and scope

- Lowered only the collection minimums to one across Core resolution, matrix validation, CLI, MCP mapper/schema, fixed and request handlers, and Core capabilities; maximum 32 and existing type, safety, repeat, identity, and execution limits remain.
- Preserved explicit-empty versus absent input through `SqlHarnessModule`; 33-case errors now give split-call and singleton-remainder guidance.
- Carried named compare case identity into existing `parameterSet` run fields. Matrix-value cases retain their existing null `parameterSet` behavior. No artifact or journal format or fingerprint algorithm changed.
- Updated README and MCP input/tool guidance. Added `McpParameterBatchTests.cs` for live served fixed/request schemas, singleton measure/compare calls, explicit empty rejection, and 33-case guidance. Updated obsolete boundary assertions in `McpMappingTests.cs` and related Core tests.
- Activated singleton/report assertions exposed a fake session marker (`A` versus the named case), artifact report copy semantics, generated case-id assumptions, and lowercase hex fingerprint expectations. Assertions now verify report data and named case identity without object reference equality or changing fingerprint behavior. A malformed synthetic Showplan fixture was corrected so it reaches plan processing rather than an unrelated LocalStorage failure.
- Parent-authorized scope extensions: `SqlHarnessModule.cs` to keep explicit empty distinct from absent; `CompareCellRunner.cs` and `CompareMatrixRunner.cs` to carry the name into existing run artifacts. `McpToolSchemaTests.cs` and `McpMappingTests.cs` received narrow stale-contract assertion updates.

## Self-review and limits

The 33-case MCP rejection is checked at mapper time; the test’s forbidden session factory confirms accepted dry-runs do not connect. Fixed and request served schemas expose 1..32 bounds for sets, files, and matrix values. Core capabilities report the same bounds and covered surfaces. The full repository gate and broad suites were intentionally left to Task 3 finalization. An unrelated untracked `noop` file was preserved and is not part of this change.
