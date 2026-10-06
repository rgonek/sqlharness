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

    private sealed class RecordingModule : ISqlHarnessModule
    {
        public List<SqlHarnessOperation> Operations { get; } = [];
        public Func<SqlHarnessOperation, CancellationToken, Task<SqlHarnessOutcome>>? Behavior { get; init; }

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            lock (Operations) Operations.Add(operation);
            return Behavior is null
                ? Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))
                : Behavior(operation, ct);
        }
    }

    [Fact]
    public async Task Request_facade_uses_one_process_gate_across_distinct_scopes()
    {
        var process = Process();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        var module = new RecordingModule
        {
            Behavior = async (_, token) =>
            {
                Interlocked.Increment(ref connections);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
            },
        };
        var handlers = new McpRequestToolHandlers(process, moduleFactory: _ => module);

        var first = handlers.QueryAsync(null!, Scope("sample-a"), "SELECT 1");
        var firstOutcome = await Task.WhenAny(entered.Task, first).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ReferenceEquals(entered.Task, firstOutcome), first.IsCompleted ? Text(await first) : "The fake module was not entered.");
        var busy = await handlers.QueryAsync(null!, Scope("sample-b"), "SELECT 2");

        Assert.True(busy.IsError == true, Text(busy));
        Assert.Equal(McpExecutionGate.BusyCode, Envelope(busy).GetProperty("error").GetProperty("code").GetString());
        Assert.Single(module.Operations);
        Assert.Equal(1, Volatile.Read(ref connections));
        var operation = Assert.IsType<SqlHarnessQueryOperation>(Assert.Single(module.Operations));
        Assert.Equal("sample-a", operation.Target.Profile);
        Assert.Equal("server-a", process.ResolveScope(new McpRequestScope("sample-a", new Dictionary<string, string> { ["tenant"] = "example" })).ResolvedTarget.Server);

        release.TrySetResult();
        Assert.False((await first).IsError == true);

        var second = await handlers.QueryAsync(null!, Scope("sample-b"), "SELECT 3");
        Assert.False(second.IsError == true, Text(second));
        Assert.Equal(2, module.Operations.Count);
        Assert.Equal(2, Volatile.Read(ref connections));
        Assert.Equal("sample-b", Assert.IsType<SqlHarnessQueryOperation>(module.Operations[1]).Target.Profile);
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
                new SqlHarnessModule(new EmptySessionFactory(), new GainStore(), scope.ProfileProvider));

            var matrix = await handlers.CompareAsync(
                null!, new McpSqlSourceArgument { Sql = "SELECT @n" }, new McpSqlSourceArgument { Sql = "SELECT @n" },
                Scope("sample-a"), matrix: new McpMatrixArgument { Name = "n", Type = "int", Values = ["1", "2"] }, repeat: 1);
            var parameterSets = await handlers.MeasureAsync(
                null!, new McpSqlSourceArgument { Sql = "SELECT @n" }, Scope("sample-b"),
                paramSetFiles: [Path.Combine(inputRoot, "small.sqljson"), Path.Combine(inputRoot, "large.sqljson")], repeat: 1);

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
        public IReadOnlyList<string> Messages => [];
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
            Assert.Equal((int)SqlHarnessExitCode.Safety, Envelope(diff).GetProperty("exitCode").GetInt32());
            Assert.Equal("The snapshot is not available in the current scope.", Envelope(diff).GetProperty("error").GetProperty("message").GetString());

            var capture = await handlers.SnapshotAsync(null!, "capture", name, Scope("sample-b"), "SELECT 1");
            Assert.True(capture.IsError == true, Text(capture));
            Assert.Equal((int)SqlHarnessExitCode.Safety, Envelope(capture).GetProperty("exitCode").GetInt32());
            Assert.Equal("The snapshot is not available in the current scope.", Envelope(capture).GetProperty("error").GetProperty("message").GetString());
        }
    }
}
