using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp;
using SqlHarness.Mcp.Tools;

using Xunit;
using Xunit.Abstractions;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpParameterBatchTests
{
    private const string ProfileName = "parameter-batches";
    private static readonly IReadOnlyDictionary<string, TargetProfile> Profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
    {
        [ProfileName] = new("unreachable.invalid", "batchdb", new Dictionary<string, string> { ["tenant"] = "^frozen$" }, "integrated"),
    };

    private readonly ITestOutputHelper _output;

    public McpParameterBatchTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Served_fixed_and_request_tools_accept_named_and_matrix_singletons_offline(bool requestScope)
    {
        var attempts = 0;
        await using var served = await ServedCatalog.CreateAsync(requestScope, () => Interlocked.Increment(ref attempts));
        var schemas = await served.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var measure = ServedSchema(schemas.Single(tool => tool.Name == "sqlharness_measure"));
        var compare = ServedSchema(schemas.Single(tool => tool.Name == "sqlharness_compare"));
        AssertCollectionBounds(measure, "parameterSets");
        AssertCollectionBounds(measure, "parameterSetFiles");
        AssertCollectionBounds(Properties(measure).GetProperty("matrix"), "values");
        AssertCollectionBounds(compare, "parameterSets");
        AssertCollectionBounds(compare, "parameterSetFiles");
        AssertCollectionBounds(Properties(compare).GetProperty("matrix"), "values");

        object? target = requestScope ? new { profile = ProfileName, vars = new Dictionary<string, string> { ["tenant"] = "frozen" } } : null;
        var setCall = new Dictionary<string, object?>
        {
            ["query"] = new { sql = "SELECT @id" },
            ["parameterSets"] = new[] { new { name = "only", parameters = new[] { new { name = "id", type = "int", value = "7" } } } },
            ["repeat"] = 1,
            ["dryRun"] = true,
        };
        var matrixCall = new Dictionary<string, object?>
        {
            ["baseline"] = new { sql = "SELECT @id" },
            ["candidate"] = new { sql = "SELECT @id" },
            ["subject"] = "singleton",
            ["scope"] = "whole",
            ["matrix"] = new { name = "id", type = "int", values = new string?[] { "7" } },
            ["repeat"] = 1,
            ["dryRun"] = true,
        };
        if (requestScope)
        {
            setCall["target"] = target;
            matrixCall["target"] = target;
        }

        var setResult = await served.Client.CallToolAsync("sqlharness_measure", setCall, cancellationToken: CancellationToken.None);
        var matrixResult = await served.Client.CallToolAsync("sqlharness_compare", matrixCall, cancellationToken: CancellationToken.None);

        Assert.False(setResult.IsError == true, Text(setResult));
        Assert.False(matrixResult.IsError == true, Text(matrixResult));
        Assert.Equal("success", Envelope(setResult).GetProperty("status").GetString());
        Assert.Equal("success", Envelope(matrixResult).GetProperty("status").GetString());
        Assert.Equal(0, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task Explicit_empty_collection_is_rejected_and_33_cases_get_split_guidance()
    {
        var scope = FixedScope();
        var query = new McpSqlSourceArgument { Sql = "SELECT @id" };
        var empty = await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapMeasureAsync(
            scope, query, null, null, null, 1, null, CancellationToken.None,
            parameterSets: Array.Empty<McpParameterSetArgument>()));
        Assert.Contains("between 1 and 32", empty.Message, StringComparison.Ordinal);

        var tooMany = await Assert.ThrowsAsync<McpMappingException>(() => McpOperationMapper.MapCompareAsync(
            scope, query, query, null, null, 1, null, "ordered",
            new McpMatrixArgument { Name = "id", Type = "int", Values = Enumerable.Range(0, 33).Select(value => (string?)value.ToString()).ToArray() },
            CancellationToken.None, dryRun: true, subject: "query", optimizationScope: "whole"));
        Assert.Contains("split into calls of at most 32 cases", tooMany.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("singleton remainder", tooMany.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Split_32_plus_1_measure_workflow_executes_each_named_case_once_and_keeps_operation_artifacts_separate()
    {
        using var temp = new McpBatchTempDirectory();
        var sessionFactory = new BatchRecordingSessionFactory();
        var module = new SqlHarnessModule(sessionFactory, () => Profiles);
        await using var served = await ServedCatalog.CreateAsync(requestScope: false, moduleFactory: _ => module);
        var all = Enumerable.Range(1, 33).Select(value => new { name = $"case-{value:D2}", parameters = new[] { new { name = "id", type = "int", value = value.ToString() } } }).ToArray();
        var calls = new List<(string Operation, int RequestBytes, int OutputBytes, string Artifact)>();
        long recoveryRequestBytes = 0;
        long recoveryOutputBytes = 0;
        var recoveryCallCount = 0;

        foreach (var (operation, sets) in new[] { ("split-32", all[..32]), ("split-1", all[32..]) })
        {
            var arguments = new Dictionary<string, object?>
            {
                ["query"] = new { sql = "SELECT @id" },
                ["parameterSets"] = sets,
                ["repeat"] = 1,
                ["subject"] = operation,
                ["scope"] = "whole",
            };
            var (inputStart, outputStart) = served.TransportByteCounts;
            var result = await served.Client.CallToolAsync("sqlharness_measure", arguments, cancellationToken: CancellationToken.None);
            var (inputEnd, outputEnd) = served.TransportByteCounts;
            Assert.False(result.IsError == true, Text(result));
            var output = Text(result);
            var inputBytes = checked((int)(inputEnd - inputStart));
            var outputBytes = checked((int)(outputEnd - outputStart));
            using var envelope = JsonDocument.Parse(output);
            var report = envelope.RootElement.GetProperty("result");
            Assert.True(report.TryGetProperty("artifactId", out var artifactElement), report.GetRawText());
            var artifact = artifactElement.GetString();
            Assert.False(string.IsNullOrWhiteSpace(artifact));
            Assert.Equal(sets.Select(set => set.name), report.GetProperty("sets").EnumerateArray().Select(set => set.GetProperty("name").GetString()));
            Assert.InRange(inputBytes, 1, int.MaxValue);
            Assert.InRange(outputBytes, 1, int.MaxValue);
            calls.Add((operation, inputBytes, outputBytes, artifact!));
        }

        Assert.Equal(2, calls.Count);
        Assert.NotEqual(calls[0].Operation, calls[1].Operation);
        Assert.NotEqual(calls[0].Artifact, calls[1].Artifact);
        foreach (var call in calls)
        {
            var (requestStart, outputStart) = served.TransportByteCounts;
            var artifactResult = await served.Client.CallToolAsync("sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = call.Artifact,
                ["section"] = "summary",
            }, cancellationToken: CancellationToken.None);
            Assert.False(artifactResult.IsError == true, Text(artifactResult));
            Assert.False(string.IsNullOrWhiteSpace(Text(artifactResult)));
            var (requestEnd, outputEnd) = served.TransportByteCounts;
            recoveryRequestBytes += requestEnd - requestStart;
            recoveryOutputBytes += outputEnd - outputStart;
            recoveryCallCount++;
        }
        var executed = sessionFactory.ExecutedValues.Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 33).SelectMany(value => new[] { value, value }), executed);
        Assert.Equal(66, sessionFactory.QueryExecutionCount); // one warmup and one measured execution per case
        Assert.Equal(33, sessionFactory.ConnectionCount);

        var references = calls.Select(call => call.Artifact).ToArray();
        Assert.Equal(2, references.Distinct(StringComparer.Ordinal).Count());
        Assert.All(references, reference => Assert.True(Directory.Exists(Path.Combine(temp.Path, "compare", reference))));
        // The injected factory creates only in-memory fake sessions; this workflow has no live DB connection path.
        _output.WriteLine($"Plan 097 split 32+1: workflow calls={calls.Count + recoveryCallCount} (measure={calls.Count}, artifact recovery={recoveryCallCount}), request={calls.Sum(call => call.RequestBytes) + recoveryRequestBytes} wire bytes, output={calls.Sum(call => call.OutputBytes) + recoveryOutputBytes} wire bytes; named cases=33 each reported once; fake sessions={sessionFactory.ConnectionCount}, measured query executions={sessionFactory.QueryExecutionCount} (one warmup plus one measured execution per case), live DB connections=0.");
    }

    private static void AssertCollectionBounds(JsonElement parent, string name)
    {
        var schema = Properties(parent).GetProperty(name);
        Assert.Equal(1, schema.GetProperty("minItems").GetInt32());
        Assert.Equal(32, schema.GetProperty("maxItems").GetInt32());
    }

    private static JsonElement ServedSchema(McpClientTool tool) => JsonDocument.Parse(JsonSerializer.Serialize(tool.ProtocolTool)).RootElement.GetProperty("inputSchema");

    private static JsonElement Properties(JsonElement schema) => schema.GetProperty("properties");

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static JsonElement Envelope(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

    private static McpScope FixedScope() => McpScope.Create(
        new McpServerOptions { Profile = ProfileName, Vars = new Dictionary<string, string> { ["tenant"] = "frozen" } },
        Profiles);

    private sealed class ServedCatalog : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private RecordingWriteStream _clientWriter = null!;
        private RecordingWriteStream _serverWriter = null!;
        private Task _serverTask = Task.CompletedTask;

        public McpClient Client { get; private set; } = null!;
        public (long Input, long Output) TransportByteCounts => (_clientWriter.BytesWritten, _serverWriter.BytesWritten);

        public static async Task<ServedCatalog> CreateAsync(bool requestScope, Action onConnect)
            => await CreateAsync(requestScope, scope => new SqlHarnessModule(new ForbiddenSessionFactory(onConnect), scope.ProfileProvider));

        public static async Task<ServedCatalog> CreateAsync(bool requestScope, Func<McpScope, ISqlHarnessModule> moduleFactory)
        {
            var catalog = new ServedCatalog();
            catalog._clientWriter = new RecordingWriteStream(catalog._clientToServer.Writer.AsStream());
            catalog._serverWriter = new RecordingWriteStream(catalog._serverToClient.Writer.AsStream());
            var options = new ModelContextProtocol.Server.McpServerOptions
            {
                ServerInfo = new Implementation { Name = "sqlharness-tests", Version = "1.0" },
                ProtocolVersion = McpHost.FallbackProtocolVersion,
            };
            if (requestScope)
            {
                var process = McpProcessContext.Create(
                    new SqlHarness.Mcp.McpServerOptions { RequestScope = true, AllowedProfiles = [ProfileName] },
                    () => Profiles);
                McpToolCatalog.Wire(options, process, moduleFactory: moduleFactory);
            }
            else
            {
                var scope = FixedScope();
                var module = moduleFactory(scope);
                McpToolCatalog.Wire(options, scope, module);
            }
            var server = McpServer.Create(
                new StreamServerTransport(catalog._clientToServer.Reader.AsStream(), catalog._serverWriter, "batch-test-server", NullLoggerFactory.Instance),
                options,
                NullLoggerFactory.Instance,
                serviceProvider: null);
            catalog._serverTask = server.RunAsync(catalog._cts.Token);
            catalog.Client = await McpClient.CreateAsync(
                new StreamClientTransport(catalog._clientWriter, catalog._serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "batch-test-client", Version = "1.0" },
                    ProtocolVersion = McpHost.FallbackProtocolVersion,
                },
                NullLoggerFactory.Instance,
                catalog._cts.Token);
            return catalog;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await Client.DisposeAsync(); } catch (OperationCanceledException) { }
            try { await _serverTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

    private sealed class ForbiddenSessionFactory(Action onConnect) : ISqlSessionFactory
    {
        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            onConnect();
            throw new InvalidOperationException("Dry-run must not connect.");
        }
    }

    private sealed class McpBatchTempDirectory : IDisposable
    {
        private readonly string? _previousHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlharness-097-" + Guid.NewGuid().ToString("N"));
        public McpBatchTempDirectory()
        {
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", Path);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _previousHome);
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class BatchRecordingSessionFactory : ISqlSessionFactory
    {
        public List<int> ExecutedValues { get; } = [];
        public int ConnectionCount { get; private set; }
        public int QueryExecutionCount { get; private set; }

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectionCount++;
            return Task.FromResult<ISqlSession>(new BatchRecordingSession(this, target));
        }

        private sealed class BatchRecordingSession(BatchRecordingSessionFactory owner, ResolvedTarget target) : ISqlSession
        {
            public IReadOnlyList<SessionMessage> Messages => [];
            public SqlHarnessTargetIdentityReport Identity { get; set; } = new(target.Server, target.Database, target.Server, target.Database, target.Mode);

            public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
            {
                if (command.Sql.Equals("SELECT @id", StringComparison.Ordinal))
                {
                    owner.QueryExecutionCount++;
                    foreach (var parameter in command.Parameters)
                        if (parameter.Name.Equals("@id", StringComparison.OrdinalIgnoreCase))
                            owner.ExecutedValues.Add(Convert.ToInt32(parameter.Value, System.Globalization.CultureInfo.InvariantCulture));
                }
                return Task.FromResult<ISqlReader>(command.Sql.StartsWith("SELECT @@OPTIONS", StringComparison.Ordinal)
                    ? EmptyReader.SessionState()
                    : EmptyReader.Empty());
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class EmptyReader : ISqlReader
        {
            private readonly string[] _names;
            private readonly object?[][] _rows;
            private int _row = -1;

            private EmptyReader(string[] names, object?[][] rows) { _names = names; _rows = rows; }
            public static EmptyReader Empty() => new([], []);
            public static EmptyReader SessionState() => new(
                ["Options", "Isolation", "LockTimeout", "DateFirst", "DateFormat", "DeadlockPriority"],
                [[29560, 2, -1, 7, "mdy", 0]]);
            public int FieldCount => _names.Length;
            public int RecordsAffected => 0;
            public string GetName(int ordinal) => _names[ordinal];
            public Type GetFieldType(int ordinal) => _rows[0][ordinal]?.GetType() ?? typeof(object);
            public bool GetAllowNull(int ordinal) => false;
            public object GetValue(int ordinal) => _rows[_row][ordinal] ?? DBNull.Value;
            public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++_row < _rows.Length);
            public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
