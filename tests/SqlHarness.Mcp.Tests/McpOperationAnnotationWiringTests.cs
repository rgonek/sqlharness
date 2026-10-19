using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

using Xunit;

namespace SqlHarness.Mcp.Tests;

public sealed class McpOperationAnnotationWiringTests
{
    private sealed class CaptureModule : ISqlHarnessModule
    {
        public SqlHarnessOperation? Operation { get; private set; }

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Operation = operation;
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }
    }

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
    {
        ["profile"] = new("server.invalid", "db", new Dictionary<string, string>(), "integrated"),
    };

    private static SqlHarnessConfig EnabledConfig() => new()
    {
        Journal = new JournalConfig { OperationAnnotations = true },
    };

    [Fact]
    public async Task Fixed_scope_query_forwards_call_time_annotations()
    {
        var scope = McpScope.Create(new McpServerOptions { Profile = "profile" }, Profiles());
        var module = new CaptureModule();
        var handlers = new McpToolHandlers(scope, module, operationAnnotationsEnabled: true);

        await handlers.QueryAsync(null!, sql: "SELECT 1", label: "fixed label", keep: true);

        var query = Assert.IsType<SqlHarnessQueryOperation>(module.Operation);
        Assert.Equal("fixed label", query.AgentLabel);
        Assert.True(query.AgentKeep);
        Assert.True(query.OperationAnnotationsEnabled);
    }

    [Fact]
    public async Task Request_scope_query_forwards_call_time_annotations()
    {
        var process = McpProcessContext.Create(new McpServerOptions { RequestScope = true, AllowedProfiles = ["profile"] }, Profiles, config: EnabledConfig());
        var module = new CaptureModule();
        var handlers = new McpRequestToolHandlers(process, moduleFactory: _ => module);

        await handlers.QueryAsync(null!, new McpRequestScopeArgument("profile", new Dictionary<string, string>()),
            sql: "SELECT 1", label: "request label", keep: false);

        var query = Assert.IsType<SqlHarnessQueryOperation>(module.Operation);
        Assert.Equal("request label", query.AgentLabel);
        Assert.False(query.AgentKeep);
        Assert.True(query.OperationAnnotationsEnabled);
    }
}