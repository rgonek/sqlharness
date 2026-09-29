using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

// 003/T4: the MCP snapshot mapper threads the frozen scope owner into the Core
// operation, so the runner can stamp scoped captures and refuse foreign or
// ownerless baselines before touching data. Capture still never sets force.
public sealed class SnapshotScopeMappingTests
{
    private const string ProfileName = "snap-scope-t4";

    [Fact]
    public async Task Snapshot_mapping_threads_frozen_scope_owner()
    {
        var scope = Scope();

        var capture = await McpOperationMapper.MapSnapshotAsync(
            scope, "capture", "n1", "SELECT 1", null, null, 30, 50, CancellationToken.None);
        Assert.False(capture.Diff);
        Assert.False(capture.Force);
        Assert.Same(scope.Owner, capture.Owner);
        Assert.True(scope.Owner.Matches(capture.Owner));

        var diff = await McpOperationMapper.MapSnapshotAsync(
            scope, "diff", "n1", "SELECT 1", null, null, 30, 50, CancellationToken.None);
        Assert.True(diff.Diff);
        Assert.False(diff.Force);
        Assert.Same(scope.Owner, diff.Owner);
    }

    private static McpScope Scope() => McpScope.Create(
        new McpServerOptions
        {
            Profile = ProfileName,
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "frozen" },
        },
        new Dictionary<string, TargetProfile>
        {
            [ProfileName] = new(
                "mcp-unreachable.invalid",
                "snapdb",
                new Dictionary<string, string> { ["tenant"] = "^frozen$" },
                "integrated"),
        });
}