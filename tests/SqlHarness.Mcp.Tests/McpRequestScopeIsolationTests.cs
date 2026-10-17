using System.Text.Json;

using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpRequestScopeIsolationTests : IDisposable
{
    private readonly string _home;
    private readonly string? _savedHome;
    private static readonly IReadOnlyDictionary<string, TargetProfile> Profiles =
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["sample-a"] = Profile("server-a", "database-a"),
            ["sample-b"] = Profile("server-b", "database-b"),
        };

    public McpRequestScopeIsolationTests()
    {
        _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        _home = Path.Combine(Path.GetTempPath(), "sqlharness-request-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private static TargetProfile Profile(string server, string database) => new(
        server, database, new Dictionary<string, string> { ["tenant"] = "^example$" }, "integrated");

    private static McpProcessContext Process() => McpProcessContext.Create(
        new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a", "sample-b"] },
        () => Profiles);

    private static McpRequestScopeArgument Scope(string profile) => new(profile, new Dictionary<string, string> { ["tenant"] = "example" });

    private static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    private static JsonElement Envelope(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

    private static string OwnerJson(ArtifactOwner owner) => JsonSerializer.Serialize(owner, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static SqlHarnessCompareReport ArtifactReport(string marker) => new(
        new SqlHarnessTargetIdentityReport("server-a", "database-a", "server-a", "database-a", "sample-a"),
        1, 1, true,
        new CompareVariantReport("baseline", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], [marker]),
        new CompareVariantReport("candidate", new(1, 1, 1), new(1, 1, 1), new(1, 1, 1), new Dictionary<string, long>(), [], []),
        null);

    private static string WriteArtifact(string id, ArtifactOwner? owner, string marker)
    {
        var directory = Path.Combine(SqlHarnessPaths.CompareDir, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"summary\",\"metrics\",\"operators\"]"
            + (owner is null ? string.Empty : ",\"owner\":" + OwnerJson(owner)) + "}");
        File.WriteAllText(Path.Combine(directory, "report.json"),
            JsonSerializer.Serialize(ArtifactReport(marker), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return id;
    }

    private static void WriteSnapshot(string name, ArtifactOwner? owner)
    {
        var directory = SqlHarnessPaths.SnapshotsDir;
        Directory.CreateDirectory(directory);
        var json = "{\"version\":1,\"createdAt\":\"2026-10-06T00:00:00Z\",\"resultSets\":[],\"resultHash\":\"synthetic-hash\""
            + (owner is null ? string.Empty : ",\"owner\":" + OwnerJson(owner)) + "}";
        File.WriteAllText(Path.Combine(directory, name + ".json"), json);
    }

    [Fact]
    public async Task Concurrent_queries_on_two_request_scopes_each_return_their_own_database()
    {
        var process = Process();
        var sessions = new CoreSessionFactory(blockedSessions: 2);
        var handlers = new McpRequestToolHandlers(process, moduleFactory: scope => new SqlHarnessModule(sessions, scope.ProfileProvider));
        var budget = TimeSpan.FromSeconds(30);

        var first = handlers.QueryAsync(null!, Scope("sample-a"), "SELECT 1 AS A");
        await sessions.SessionEntered(1).WaitAsync(budget);
        var second = handlers.QueryAsync(null!, Scope("sample-b"), "SELECT 2 AS A");
        // A second call that returns before reaching Core was rejected, not run.
        await Task.WhenAny(sessions.SessionEntered(2), second).WaitAsync(budget);
        Assert.False(second.IsCompleted, second.IsCompleted ? Text(await second) : null);

        // Both scopes are executing inside Core at the same time.
        Assert.Equal(["database-a", "database-b"], sessions.Targets.Select(target => target.Database));
        sessions.ReleaseSession(2);
        sessions.ReleaseSession(1);
        var secondResult = await second.WaitAsync(budget);
        var firstResult = await first.WaitAsync(budget);

        Assert.False(firstResult.IsError == true, Text(firstResult));
        Assert.False(secondResult.IsError == true, Text(secondResult));
        Assert.Equal("database-a", Envelope(firstResult).GetProperty("result").GetProperty("database").GetString());
        Assert.Equal("database-b", Envelope(secondResult).GetProperty("result").GetProperty("database").GetString());
    }

    [Fact]
    public async Task Matrix_and_parameter_set_calls_keep_their_single_invocation_owner()
    {
        var inputRoot = Path.Combine(Path.GetTempPath(), "sqlharness-request-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputRoot);
        try
        {
            File.WriteAllText(Path.Combine(inputRoot, "small.sqljson"), "{\"name\":\"small\",\"parameters\":[\"n:int=1\"]}");
            File.WriteAllText(Path.Combine(inputRoot, "large.sqljson"), "{\"name\":\"large\",\"parameters\":[\"n:int=2\"]}");
            var process = McpProcessContext.Create(
                new McpServerOptions
                {
                    RequestScope = true,
                    AllowedProfiles = ["sample-a", "sample-b"],
                    InputRoots = [inputRoot],
                },
                () => Profiles);
            var handlers = new McpRequestToolHandlers(process, moduleFactory: scope =>
                new SqlHarnessModule(new EmptySessionFactory(), scope.ProfileProvider));

            var matrix = await handlers.CompareAsync(
                null!, new McpSqlSourceArgument { Sql = "SELECT @n" }, new McpSqlSourceArgument { Sql = "SELECT @n" },
                Scope("sample-a"), "batch-query", "whole", matrix: new McpMatrixArgument { Name = "n", Type = "int", Values = ["1", "2"] }, repeat: 1);
            var parameterSets = await handlers.MeasureAsync(
                null!, new McpSqlSourceArgument { Sql = "SELECT @n" }, Scope("sample-b"),
                parameterSetFiles: [Path.Combine(inputRoot, "small.sqljson"), Path.Combine(inputRoot, "large.sqljson")], repeat: 1);

            Assert.False(matrix.IsError == true, Text(matrix));
            Assert.False(parameterSets.IsError == true, Text(parameterSets));
            var manifests = Directory.GetFiles(SqlHarnessPaths.CompareDir, "manifest.json", SearchOption.AllDirectories)
                .Select(path => (Path: path, Owner: JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty("owner", out var owner)
                    ? owner.Deserialize<ArtifactOwner>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    : null))
                .ToArray();
            var matrixManifests = manifests.Where(item => item.Owner?.Profile == "sample-a").ToArray();
            var setManifest = Assert.Single(manifests, item => item.Owner?.Profile == "sample-b");
            Assert.Equal(2, matrixManifests.Length);
            var expectedA = process.ResolveScope(new McpRequestScope("sample-a", new Dictionary<string, string> { ["tenant"] = "example" })).Owner;
            var expectedB = process.ResolveScope(new McpRequestScope("sample-b", new Dictionary<string, string> { ["tenant"] = "example" })).Owner;
            Assert.All(matrixManifests, item => Assert.True(expectedA.Matches(item.Owner)));
            Assert.True(expectedB.Matches(setManifest.Owner));

            // The writer-produced artifact is readable by its request owner
            // and denied when the other request scope presents the same ID.
            var setId = Path.GetFileName(Path.GetDirectoryName(setManifest.Path))!;
            var ownRead = await handlers.ArtifactAsync(null!, setId, "summary", Scope("sample-b"));
            var foreignRead = await handlers.ArtifactAsync(null!, setId, "summary", Scope("sample-a"));
            Assert.False(ownRead.IsError == true, Text(ownRead));
            Assert.True(foreignRead.IsError == true, Text(foreignRead));
        }
        finally
        {
            Directory.Delete(inputRoot, recursive: true);
        }
    }

    private sealed class EmptySessionFactory : ISqlSessionFactory
    {
        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ISqlSession>(new EmptySession(target));
        }
    }

    private sealed class EmptySession(ResolvedTarget target) : ISqlSession
    {
        public IReadOnlyList<SessionMessage> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = new(target.Server, target.Database, target.Server, target.Database, "profile");
        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ISqlReader>(new EmptyReader());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyReader : ISqlReader
    {
        public int FieldCount => 0;
        public int RecordsAffected => 0;
        public string GetName(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Type GetFieldType(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public bool GetAllowNull(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public object GetValue(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));
        public Task<bool> ReadAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(false); }
        public Task<bool> NextResultAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(false); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Request_facade_artifact_reads_use_each_scope_and_survive_process_restart()
    {
        var firstProcess = Process();
        var scopeA = firstProcess.ResolveScope(new McpRequestScope("sample-a", new Dictionary<string, string> { ["tenant"] = "example" }));
        var scopeB = firstProcess.ResolveScope(new McpRequestScope("sample-b", new Dictionary<string, string> { ["tenant"] = "example" }));
        var idA = WriteArtifact("request-scope-a", scopeA.Owner, "artifact-from-a-marker");
        var idB = WriteArtifact("request-scope-b", scopeB.Owner, "artifact-from-b-marker");
        var idLegacy = WriteArtifact("request-scope-legacy", null, "legacy-artifact-marker");

        // A new process context uses the same frozen startup definitions and
        // reads A's owned artifact. It has no remembered mutable selection.
        var restarted = Process();
        var handlers = new McpRequestToolHandlers(restarted);
        var aRead = handlers.ArtifactAsync(null!, idA, "summary", Scope("sample-a"));
        var bRead = handlers.ArtifactAsync(null!, idB, "summary", Scope("sample-b"));
        var results = await Task.WhenAll(aRead, bRead);
        Assert.All(results, result => Assert.False(result.IsError == true, Text(result)));
        Assert.Contains("artifact-from-a-marker", Text(results[0]), StringComparison.Ordinal);
        Assert.Contains("artifact-from-b-marker", Text(results[1]), StringComparison.Ordinal);

        var foreign = await handlers.ArtifactAsync(null!, idA, "summary", Scope("sample-b"));
        Assert.True(foreign.IsError == true);
        Assert.DoesNotContain("artifact-from-a-marker", Text(foreign), StringComparison.Ordinal);
        var legacy = await handlers.ArtifactAsync(null!, idLegacy, "summary", Scope("sample-a"));
        Assert.True(legacy.IsError == true);
        Assert.DoesNotContain("legacy-artifact-marker", Text(legacy), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_facade_refuses_foreign_and_legacy_snapshot_before_connecting()
    {
        var process = Process();
        var scopeA = process.ResolveScope(new McpRequestScope("sample-a", new Dictionary<string, string> { ["tenant"] = "example" }));
        var handlers = new McpRequestToolHandlers(process);
        WriteSnapshot("owned-by-a", scopeA.Owner);
        WriteSnapshot("legacy-without-owner", null);

        foreach (var name in new[] { "owned-by-a", "legacy-without-owner" })
        {
            var diff = await handlers.SnapshotAsync(null!, "diff", name, Scope("sample-b"), "SELECT 1");
            Assert.True(diff.IsError == true, Text(diff));
            Assert.Equal("error", Envelope(diff).GetProperty("status").GetString());
            Assert.False(Envelope(diff).TryGetProperty("exitCode", out _));
            Assert.Equal("The snapshot is not available in the current scope.", Envelope(diff).GetProperty("error").GetProperty("message").GetString());

            var capture = await handlers.SnapshotAsync(null!, "capture", name, Scope("sample-b"), "SELECT 1");
            Assert.True(capture.IsError == true, Text(capture));
            Assert.Equal("error", Envelope(capture).GetProperty("status").GetString());
            Assert.False(Envelope(capture).TryGetProperty("exitCode", out _));
            Assert.Equal("The snapshot is not available in the current scope.", Envelope(capture).GetProperty("error").GetProperty("message").GetString());
        }
    }
}