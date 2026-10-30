# Task 1 implementation report

Status: DONE_WITH_CONCERNS. Product commit: `3bfa2fd9`.

## Changes

- `src/SqlHarness.Core/Targets/TargetProfile.cs`, `ProfileStore.cs`: nullable profile mode; strict validation of explicit modes and writers remains, including explicit production YOLO acknowledgement and enabled-mode writer requirements.
- `src/SqlHarness.Core/Mutations/MutationPolicyResolver.cs`: metadata-only resolver for configured/effective mode, source, writer presence, missing production acknowledgement; strict mode validation and no credential/environment value access.
- `src/SqlHarness.Core/Capabilities.cs`: effective profile mode and writer presence flow through resolver; MCP uses this shared projection.
- `src/SqlHarness.Dashboard/DashboardProfiles.cs`, `DashboardServer.cs`: retain existing fields, project effective mode plus configured mode/source/readiness using current global settings.
- `src/SqlHarness.Dashboard/Mutations/MutationEndpoints.cs`, `MutationExecutor.cs`: same policy for submission and execution; inherited unacknowledged prod/production/staging YOLO DML fails closed, including after settings change; non-DML still follows existing click policy.
- Tests: `Targets/ProfileStoreTests.cs`, `Targets/MutationPolicyResolverTests.cs`, `Dashboard/MutationExecutorTests.cs`, `Dashboard/MutationEndpointsTests.cs`, `Dashboard/DashboardProfilesTests.cs`, `Cli/CapabilitiesCommandTests.cs`, `SqlHarness.Mcp.Tests/McpMappingTests.cs`.

## Evidence

Initial focused Core baseline with writer-only RED test added: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ProfileStoreTests|FullyQualifiedName~MutationExecutorTests|FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~DashboardProfilesTests" --nologo -m:1 -nodeReuse:false -p:SkipDashboardUi=true` exited 1: 153 passed, 1 failed, writer-only block rejected with `A profile mutation mode is invalid.` Existing focused cases therefore passed, although this was not a completely unmodified test tree.

RED security: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Writer_only_mutation_block_inherits_mode|FullyQualifiedName~Inherited_yolo_without_acknowledgement" --nologo -m:1 -nodeReuse:false -p:SkipDashboardUi=true` exited 1: 4 failed as expected. All three synthetic inherited YOLO environments returned success instead of failure; writer-only block failed to load. No real database used.

GREEN: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ProfileStoreTests|FullyQualifiedName~MutationPolicyResolverTests|FullyQualifiedName~MutationExecutorTests|FullyQualifiedName~MutationEndpointsTests|FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~DashboardProfilesTests" --nologo -m:1 -nodeReuse:false -p:SkipDashboardUi=true` passed 194/194. `dotnet test tests/SqlHarness.Mcp.Tests --filter FullyQualifiedName~McpMappingTests --nologo -m:1 -nodeReuse:false -p:SkipDashboardUi=true` passed 87/87. `git diff --cached --check` passed before commit. Subsequent product change after tests was indentation only.

## Self-review and limitations

Searched direct mode fallbacks across src: effective profile resolution remains only in resolver; global capability without a profile reports the global setting. Writer credentials remain untouched; explicit mode and writer validation stays strict. Executor re-resolves immediately before dispatch; endpoint also checks before broker submission. Existing synthetic schema/operator tests and acknowledged DML test passed. Tests cover no block, writer-only inheritance, all global and explicit modes, absent writer, changing defaults, invalid values and production YOLO rejection.

MCP focused baseline was not completed before product changes: the initial combined build stalled on dashboard UI dependency setup and was canceled; a focused MCP run after implementation passed 86, then 87 after its new test. Initial unscoped dashboard builds started worktree-local npm dependency setup, canceled while stalled; completion was not established. Controller clarified local dependency setup is authorized. Full `just verify`, Linux, UI build and live DB/profile acceptance were not run, per task allocation. No deployed installation, restart, merge, push, or live writes.
