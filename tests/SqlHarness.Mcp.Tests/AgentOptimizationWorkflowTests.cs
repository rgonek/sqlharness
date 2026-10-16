using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Mcp.Tools;
using SqlHarness.Mcp.WorkflowHost;

using Xunit.Abstractions;

namespace SqlHarness.Mcp.Tests;

[Collection("McpStdioProcess")]
public sealed class AgentOptimizationWorkflowTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);
    private static readonly int[] ScriptedBudgets = [4096, 65536];
    private static readonly string[] ScriptedScenarios =
    [
        WorkflowScenarios.NoImprovementId,
        WorkflowScenarios.PartialMatrixId,
        WorkflowScenarios.CollidingOperatorsId,
        WorkflowScenarios.UnavailableMetricsId,
    ];

    private readonly ITestOutputHelper _output;

    public AgentOptimizationWorkflowTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Manifest_pins_portable_scenario_ids_without_machine_paths()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Fixture("manifest.json")));
        var root = document.RootElement;
        Assert.Equal("synthetic-optimization-workflows", root.GetProperty("kind").GetString());
        Assert.True(root.GetProperty("replayable").GetBoolean());
        Assert.DoesNotContain(":\\", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", root.GetRawText(), StringComparison.Ordinal);
        var ids = root.GetProperty("scenarios").EnumerateArray().Select(scenario => scenario.GetProperty("id").GetString()).ToArray();
        Assert.Contains(WorkflowScenarios.NoImprovementId, ids);
        Assert.Contains(WorkflowScenarios.PartialMatrixId, ids);
        Assert.Contains(WorkflowScenarios.CollidingOperatorsId, ids);
        Assert.Contains("oracle/no-improvement.json", root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("requiredClaims", File.ReadAllText(Fixture("prompts", "no-improvement.md")), StringComparison.Ordinal);

        var summaryText = File.ReadAllText(Fixture("trials", "prechange-summary.json"));
        Assert.DoesNotContain(":\\", summaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", summaryText, StringComparison.Ordinal);
        using var summary = JsonDocument.Parse(summaryText);
        Assert.Equal(9, summary.RootElement.GetArrayLength());
        Assert.All(summary.RootElement.EnumerateArray(), row =>
        {
            Assert.Equal("prechange", row.GetProperty("phase").GetString());
            Assert.Equal("grok-4.6", row.GetProperty("model").GetString());
            Assert.Equal("medium", row.GetProperty("effort").GetString());
            Assert.True(row.TryGetProperty("contaminated", out _));
        });
        var run1 = summary.RootElement.EnumerateArray().Single(row =>
            row.GetProperty("family").GetString() == "no-improvement" && row.GetProperty("run").GetInt32() == 1);
        Assert.True(run1.GetProperty("contaminated").GetBoolean());
        Assert.Equal("temp-host-dll-search", run1.GetProperty("contamination").GetString());
        Assert.NotEqual("incomplete", run1.GetProperty("outcome").GetString());
        var ni3 = summary.RootElement.EnumerateArray().Single(row =>
            row.GetProperty("family").GetString() == "no-improvement" && row.GetProperty("run").GetInt32() == 3);
        Assert.Equal("failed-oracle", ni3.GetProperty("outcome").GetString());
        Assert.Contains(
            "sqlharness_query",
            ni3.GetProperty("outsideAllowedSelectors").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            4,
            summary.RootElement.EnumerateArray().Count(row => row.GetProperty("outcome").GetString() == "evidence-complete"));
        Assert.All(
            summary.RootElement.EnumerateArray().Where(row =>
                row.GetProperty("family").GetString() != "no-improvement" || row.GetProperty("run").GetInt32() != 1),
            row => Assert.False(row.GetProperty("contaminated").GetBoolean()));
    }

    [Fact]
    public async Task Host_handshake_lists_nine_tools_serves_capabilities_and_rejects_unknown_sql()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.NoImprovementId, Budget);
        var tools = await session.Client.ListToolsAsync(cancellationToken: session.Token);
        Assert.Equal(
            McpToolCatalog.ToolNames.Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("sqlharness_mutation", tools.Select(tool => tool.Name));

        var capabilities = await session.Call("sqlharness_capabilities", new Dictionary<string, object?>());
        Assert.False(capabilities.IsError == true, Text(capabilities));
        var envelope = Envelope(capabilities);
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.Equal("fixed", envelope.GetProperty("result").GetProperty("scopeMode").GetString());
        Assert.Single(capabilities.Content.OfType<TextContentBlock>());

        var unknown = await session.Call("sqlharness_compare", CompareArguments("SELECT 1", "SELECT 2"));
        Assert.True(unknown.IsError == true);
        Assert.Contains(WorkflowModule.UnknownSqlMessage, Text(unknown), StringComparison.Ordinal);
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Compare_follow_up_reads_a_real_offline_artifact()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.NoImprovementId, Budget);
        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.NoImprovementBaselineSql, WorkflowScenarios.NoImprovementCandidateSql));
        Assert.False(compare.IsError == true, Text(compare));
        var artifactId = ArtifactId(Envelope(compare));
        Assert.False(string.IsNullOrWhiteSpace(artifactId));

        var summary = await session.Call("sqlharness_artifact", new Dictionary<string, object?>
        {
            ["id"] = artifactId,
            ["section"] = "summary",
        });
        Assert.False(summary.IsError == true, Text(summary));
        var summaryText = Text(summary);
        Assert.Contains("inconclusive", summaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT Id, Name FROM dbo.Orders", summaryText, StringComparison.Ordinal);
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Request_scoped_host_maps_the_synthetic_profile()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.RequestScopeId, Budget);
        var capabilities = await session.Call("sqlharness_capabilities", new Dictionary<string, object?>());
        Assert.False(capabilities.IsError == true, Text(capabilities));
        Assert.Equal("request", Envelope(capabilities).GetProperty("result").GetProperty("scopeMode").GetString());

        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.NoImprovementBaselineSql,
            WorkflowScenarios.NoImprovementCandidateSql,
            requestScope: true));
        Assert.False(compare.IsError == true, Text(compare));
        await session.ShutdownAsync();
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Child_process_stdout_is_pure_protocol_and_records_zero_connections()
    {
        await using var session = await Session.StartAsync(WorkflowScenarios.UnavailableMetricsId, Budget);
        var compare = await session.Call("sqlharness_compare", CompareArguments(
            WorkflowScenarios.UnavailableBaselineSql, WorkflowScenarios.UnavailableCandidateSql));
        Assert.False(compare.IsError == true, Text(compare));
        var text = Text(compare);
        Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.4", text, StringComparison.Ordinal);

        await session.ShutdownAsync();
        McpStdioProcessHarness.AssertStdoutIsPureProtocol(session.Child.StdoutTee.Recorded);
        session.AssertNoDatabaseConnections();
    }

    [Fact]
    public async Task Scripted_workflows_fit_4096_and_65536_and_freeze_portable_records()
    {
        var identities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "SqlHarness.Mcp.WorkflowHost",
            ["manifest"] = "tests/SqlHarness.Mcp.Tests/Fixtures/OptimizationWorkflows/manifest.json",
        };
        var recordsDir = Path.Combine(
            McpStdioProcessHarness.FindRepositoryRoot(),
            "tests", "SqlHarness.Mcp.Tests", "Fixtures", "OptimizationWorkflows", "records");
        Directory.CreateDirectory(recordsDir);

        foreach (var scenario in ScriptedScenarios)
        {
            foreach (var budget in ScriptedBudgets)
            {
                await using var session = await Session.StartAsync(scenario, Budget);
                var discovery = await session.Client.ListToolsAsync(cancellationToken: session.Token);
                var discoveryBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                    discovery.Select(tool => tool.ProtocolTool).ToArray()));
                Assert.InRange(discoveryBytes, 1, McpLimits.ToolsListBudgetBytes);

                var calls = new List<object>();
                var artifactId = await RunScriptedAsync(session, scenario, budget, calls);
                Assert.False(string.IsNullOrWhiteSpace(artifactId));

                await session.ShutdownAsync();
                var protocolBytes = session.Child.StdoutTee.Recorded.Length;
                var record = new Dictionary<string, object?>
                {
                    ["scenario"] = scenario,
                    ["budget"] = budget,
                    ["discoveryUtf8Bytes"] = discoveryBytes,
                    ["identities"] = identities,
                    ["calls"] = calls,
                };
                var json = Normalize(JsonSerializer.Serialize(record, JsonOptions), session.Home);
                AssertScenarioRecord(scenario, budget, json);
                var path = Path.Combine(recordsDir, $"{scenario}-{budget}.json");
                Assert.True(File.Exists(path), $"Frozen pre-change record missing: {path}");
                _ = Normalize(File.ReadAllText(path));
                _output.WriteLine($"{scenario}@{budget}: discovery={discoveryBytes} protocolFrameBytes={protocolBytes} (machine-local; omitted from snapshot) calls={calls.Count}");
                session.AssertNoDatabaseConnections();
            }
        }
    }

    private static async Task<string?> RunScriptedAsync(Session session, string scenario, int budget, List<object> calls)
    {
        string? artifactId = null;
        if (scenario == WorkflowScenarios.PartialMatrixId)
        {
            var compare = await RecordAsync(session, calls, "sqlharness_compare", MatrixArguments(budget), budget);
            artifactId = MatrixArtifactId(Envelope(compare)) ?? ArtifactId(Envelope(compare));
            var cursor = Continuation(Envelope(compare));
            while (cursor is int next)
            {
                var page = await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
                {
                    ["id"] = artifactId,
                    ["section"] = "matrix-cells",
                    ["cursor"] = next,
                }, McpLimits.DefaultCallToolResultBudgetBytes);
                cursor = Continuation(Envelope(page));
            }

            return artifactId;
        }

        var sql = scenario switch
        {
            WorkflowScenarios.NoImprovementId => (WorkflowScenarios.NoImprovementBaselineSql, WorkflowScenarios.NoImprovementCandidateSql),
            WorkflowScenarios.CollidingOperatorsId => (WorkflowScenarios.CollidingBaselineSql, WorkflowScenarios.CollidingCandidateSql),
            WorkflowScenarios.UnavailableMetricsId => (WorkflowScenarios.UnavailableBaselineSql, WorkflowScenarios.UnavailableCandidateSql),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var produced = await RecordAsync(session, calls, "sqlharness_compare", CompareArguments(sql.Item1, sql.Item2, budget: budget), budget);
        artifactId = ArtifactId(Envelope(produced));
        if (scenario == WorkflowScenarios.UnavailableMetricsId)
            return artifactId;

        if (scenario == WorkflowScenarios.CollidingOperatorsId)
        {
            await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = artifactId,
                ["section"] = "operators",
            }, McpLimits.DefaultCallToolResultBudgetBytes);
            await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
            {
                ["id"] = artifactId,
                ["section"] = "plan",
                ["variant"] = "candidate",
                ["statement"] = 1,
            }, McpLimits.DefaultCallToolResultBudgetBytes);
            return artifactId;
        }

        await RecordAsync(session, calls, "sqlharness_artifact", new Dictionary<string, object?>
        {
            ["id"] = artifactId,
            ["section"] = "summary",
        }, McpLimits.DefaultCallToolResultBudgetBytes);
        return artifactId;
    }

    private static async Task<CallToolResult> RecordAsync(
        Session session, List<object> calls, string tool, Dictionary<string, object?> arguments, int budget)
    {
        var result = await session.Call(tool, arguments);
        var text = Normalize(Text(result), session.Home);
        var visible = Encoding.UTF8.GetByteCount(text);
        Assert.InRange(visible, 1, budget);
        var continuation = Continuation(Envelope(result));
        calls.Add(new Dictionary<string, object?>
        {
            ["tool"] = tool,
            ["arguments"] = StripSql(arguments),
            ["isError"] = result.IsError == true,
            ["modelVisibleUtf8Bytes"] = visible,
            ["text"] = text.TrimEnd(),
            ["omittedItems"] = Omitted(Envelope(result)),
            ["continuation"] = continuation,
            ["terminal"] = result.IsError == true ? "error" : continuation is null ? "complete" : "continuation",
            ["selectedArtifacts"] = ArtifactId(Envelope(result)) is { } id ? new[] { id } : Array.Empty<string>(),
        });
        return result;
    }

    private static Dictionary<string, object?> CompareArguments(string baseline, string candidate, bool requestScope = false, int? budget = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["baseline"] = new Dictionary<string, object?> { ["sql"] = baseline },
            ["candidate"] = new Dictionary<string, object?> { ["sql"] = candidate },
            ["subject"] = "workflow-fixture",
            ["scope"] = "whole",
            ["repeat"] = 5,
        };
        if (requestScope)
        {
            arguments["target"] = new Dictionary<string, object?>
            {
                ["profile"] = WorkflowScenarios.SqlServerProfile,
                ["vars"] = new Dictionary<string, string>(),
            };
        }

        if (budget is not null)
            arguments["maxResultBytes"] = budget;
        return arguments;
    }

    private static Dictionary<string, object?> MatrixArguments(int budget)
    {
        var arguments = CompareArguments(WorkflowScenarios.PartialMatrixBaselineSql, WorkflowScenarios.PartialMatrixCandidateSql, budget: budget);
        arguments["matrix"] = new Dictionary<string, object?>
        {
            ["name"] = "BatchSize",
            ["type"] = "int",
            ["values"] = Enumerable.Range(1, 32).Select(value => value.ToString()).ToArray(),
        };
        return arguments;
    }

    private static Dictionary<string, object?> StripSql(Dictionary<string, object?> arguments)
    {
        var copy = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
        foreach (var key in new[] { "baseline", "candidate" })
        {
            if (copy.TryGetValue(key, out var value) && value is Dictionary<string, object?> nested && nested.ContainsKey("sql"))
                copy[key] = new Dictionary<string, object?> { ["sql"] = "<synthetic>" };
        }

        return copy;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static JsonElement Envelope(CallToolResult result) =>
        JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement.Clone();

    private static string? ArtifactId(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        if (result.TryGetProperty("artifactId", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString();
        if (result.TryGetProperty("matrixArtifactId", out var matrix) && matrix.ValueKind == JsonValueKind.String)
            return matrix.GetString();
        if (result.TryGetProperty("artifactDirectory", out var directory) && directory.ValueKind == JsonValueKind.String)
            return Path.GetFileName(directory.GetString());
        return null;
    }

    private static string? MatrixArtifactId(JsonElement envelope) =>
        envelope.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("matrixArtifactId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : ArtifactId(envelope);

    private static int? Continuation(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        if (!result.TryGetProperty("continuation", out var continuation) || continuation.ValueKind != JsonValueKind.Number)
            return null;
        return continuation.GetInt32();
    }

    private static int? Omitted(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("truncation", out var truncation) || truncation.ValueKind != JsonValueKind.Object)
            return null;
        return truncation.TryGetProperty("omittedItems", out var omitted) && omitted.ValueKind == JsonValueKind.Number
            ? omitted.GetInt32()
            : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Normalize(string text, string? home = null)
    {
        var unix = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var full = Path.GetFullPath(home).TrimEnd('\\', '/');
            foreach (var form in new[]
                     {
                         JsonSerializer.Serialize(full).Trim('"'),
                         JsonSerializer.Serialize(full.Replace('\\', '/')).Trim('"'),
                         full.Replace('\\', '/'),
                         full,
                     }.Distinct(StringComparer.Ordinal))
            {
                unix = unix.Replace(form, "<home>", StringComparison.OrdinalIgnoreCase);
            }
        }

        unix = Regex.Replace(unix, @"\d{8}T\d{9}Z-[A-Za-z0-9._-]+-[0-9a-f]{32}", "<artifact-id>");
        unix = Regex.Replace(unix, @"""(?:parentOperationRef|operationRef)""\s*:\s*""[0-9a-f]{32}""", m =>
            m.Value.Contains("parent", StringComparison.Ordinal) ? "\"parentOperationRef\":\"<operation-ref>\"" : "\"operationRef\":\"<operation-ref>\"");
        unix = Regex.Replace(unix, @"cfg_[0-9a-f]+", "<configuration-ref>");
        unix = Regex.Replace(unix, @"sqlharness-077-[0-9a-f]{32}", "<home-id>");
        unix = unix.Replace("<home>\\\\", "<home>/", StringComparison.Ordinal);
        unix = unix.Replace("<home>\\", "<home>/", StringComparison.Ordinal);
        unix = unix.Replace("\\\\compare\\\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("\\compare\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("/compare\\\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("/compare\\", "/compare/", StringComparison.Ordinal);
        unix = unix.Replace("\\\\<artifact-id>", "/<artifact-id>", StringComparison.Ordinal);
        unix = unix.Replace("\\<artifact-id>", "/<artifact-id>", StringComparison.Ordinal);
        return unix.TrimEnd() + "\n";
    }

    private static void AssertScenarioRecord(string scenario, int budget, string json)
    {
        Assert.DoesNotContain("<home>\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/compare\\\\", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var first = document.RootElement.GetProperty("calls")[0].GetProperty("text").GetString()!;
        using var envelope = JsonDocument.Parse(first);
        var result = envelope.RootElement.GetProperty("result");
        var artifact = result.TryGetProperty("artifactId", out var artifactId)
            ? artifactId.GetString()
            : result.TryGetProperty("matrixArtifactId", out var matrixId)
                ? matrixId.GetString()
                : result.TryGetProperty("artifactDirectory", out var directory)
                    ? directory.GetString()
                    : null;
        Assert.False(string.IsNullOrWhiteSpace(artifact), first[..Math.Min(400, first.Length)]);
        Assert.DoesNotContain("\\", artifact, StringComparison.Ordinal);
        Assert.DoesNotContain(":", artifact, StringComparison.Ordinal);
        switch (scenario)
        {
            case WorkflowScenarios.UnavailableMetricsId:
                Assert.Equal("[]", result.GetProperty("parameters").GetRawText());
                Assert.Equal(0.8m, result.GetProperty("baseline").GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetDecimal());
                Assert.Equal(0.4m, result.GetProperty("candidate").GetProperty("elapsedTimeMilliseconds").GetProperty("median").GetDecimal());
                Assert.Equal(JsonValueKind.Null, result.GetProperty("baseline").GetProperty("cpuTimeMilliseconds").ValueKind);
                Assert.Equal(JsonValueKind.Null, result.GetProperty("baseline").GetProperty("logicalReads").ValueKind);
                var regression = result.GetProperty("regression");
                Assert.Equal("unavailable", regression.GetProperty("logicalReads").GetProperty("status").GetString());
                Assert.Equal("unavailable", regression.GetProperty("cpu").GetProperty("status").GetString());
                Assert.False(regression.GetProperty("elapsed").TryGetProperty("baselineMedian", out _));
                break;
            case WorkflowScenarios.CollidingOperatorsId:
                Assert.Equal("[]", result.GetProperty("parameters").GetRawText());
                Assert.Contains("Clustered Index Scan", first, StringComparison.Ordinal);
                Assert.Contains("[dbo].[Accounts]", first, StringComparison.Ordinal);
                Assert.Equal("pass", result.GetProperty("regression").GetProperty("verdict").GetString());
                Assert.DoesNotContain("unstable-elapsed", first, StringComparison.Ordinal);
                break;
            case WorkflowScenarios.PartialMatrixId when budget == 65536:
                JsonElement Detail(int index) => result.GetProperty("details").EnumerateArray()
                    .Single(item => item.GetProperty("index").GetInt32() == index)
                    .GetProperty("compare").GetProperty("regression");
                var fail = Detail(7);
                Assert.Equal("fail", fail.GetProperty("verdict").GetString());
                Assert.Contains("elapsed-regressed", fail.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal("regressed", fail.GetProperty("elapsed").GetProperty("status").GetString());
                var inconclusive = Detail(15);
                Assert.Equal("inconclusive", inconclusive.GetProperty("verdict").GetString());
                Assert.Contains("unstable-elapsed", inconclusive.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal("inconclusive", inconclusive.GetProperty("elapsed").GetProperty("status").GetString());
                var verdicts = result.GetProperty("details").EnumerateArray()
                    .Select(cell => cell.GetProperty("compare").GetProperty("regression").GetProperty("verdict").GetString())
                    .ToArray();
                Assert.Equal(1, verdicts.Count(value => value == "fail"));
                Assert.Equal(1, verdicts.Count(value => value == "inconclusive"));
                Assert.Equal(30, verdicts.Count(value => value == "pass"));
                break;
            case WorkflowScenarios.NoImprovementId:
                Assert.Contains("Region", result.GetProperty("parameters").GetRawText(), StringComparison.Ordinal);
                var noImprovement = result.GetProperty("regression");
                Assert.Equal("inconclusive", noImprovement.GetProperty("verdict").GetString());
                var reasons = noImprovement.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()).ToArray();
                Assert.Contains("results-not-equivalent", reasons);
                Assert.DoesNotContain("unstable-elapsed", reasons);
                break;
        }
    }

    private static string Fixture(params string[] path) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", "OptimizationWorkflows", .. path]);

    private static string ConfigurationName() =>
        typeof(AgentOptimizationWorkflowTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>()
            .Single().Configuration;

    private static string HostDll()
    {
        var dll = Path.Combine(
            McpStdioProcessHarness.FindRepositoryRoot(),
            "tests", "SqlHarness.Mcp.WorkflowHost", "bin", ConfigurationName(), "net10.0",
            "SqlHarness.Mcp.WorkflowHost.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"Expected the workflow host at {dll}. Build the solution before the tests.", dll);
        return dll;
    }

    private sealed class Session : IAsyncDisposable
    {
        private Session(string home, McpStdioProcessHarness.StdioChild child, McpClient client, CancellationTokenSource cts)
        {
            Home = home;
            Child = child;
            Client = client;
            Token = cts.Token;
            _cts = cts;
        }

        public string Home { get; }
        public McpStdioProcessHarness.StdioChild Child { get; }
        public McpClient Client { get; }
        public CancellationToken Token { get; }
        private readonly CancellationTokenSource _cts;
        private bool _stopped;

        public static async Task<Session> StartAsync(string scenario, TimeSpan budget)
        {
            var cts = new CancellationTokenSource(budget);
            var home = Path.Combine(Path.GetTempPath(), "sqlharness-077-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(home);
            var psi = new ProcessStartInfo("dotnet", $"\"{HostDll()}\" --scenario {scenario} --home \"{home}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["SQLHARNESS_HOME"] = home;
            psi.Environment["SQLHARNESS_077_FIXTURE_PW"] = "unused-fixture-password";
            var process = Process.Start(psi) ?? throw new InvalidOperationException("The workflow host did not start.");
            var tee = new RecordingStream(process.StandardOutput.BaseStream);
            var stderr = process.StandardError.ReadToEndAsync();
            var child = new McpStdioProcessHarness.StdioChild(process, tee, stderr);
            try
            {
                var client = await McpClient.CreateAsync(
                    new StreamClientTransport(process.StandardInput.BaseStream, tee, NullLoggerFactory.Instance),
                    new McpClientOptions
                    {
                        ClientInfo = new Implementation { Name = "sqlharness-077-tests", Version = "1.0.0" },
                        ProtocolVersion = McpStdioProcessHarness.DefaultProtocolVersion,
                    },
                    NullLoggerFactory.Instance,
                    cts.Token);
                return new Session(home, child, client, cts);
            }
            catch (Exception exception)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                var error = "";
                try { error = await stderr.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
                try { Directory.Delete(home, true); } catch (IOException) { }
                cts.Dispose();
                throw new InvalidOperationException($"Workflow host failed to start: {error}", exception);
            }
        }

        public ValueTask<CallToolResult> Call(string tool, Dictionary<string, object?> arguments) =>
            Client.CallToolAsync(tool, arguments, cancellationToken: Token);

        public async Task ShutdownAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            await Client.DisposeAsync();
            try { Child.Process.StandardInput.Close(); } catch (ObjectDisposedException) { }
            if (!Child.Process.WaitForExit(10_000))
                Child.Process.Kill(entireProcessTree: true);
            await Child.StdoutTee.DrainRemainingAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }

        public void AssertNoDatabaseConnections()
        {
            var stats = Path.Combine(Home, "workflow-host-stats.json");
            Assert.True(File.Exists(stats), "The host did not record connection attempts.");
            using var document = JsonDocument.Parse(File.ReadAllText(stats));
            Assert.Equal(0, document.RootElement.GetProperty("connectionAttempts").GetInt32());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ShutdownAsync();
            }
            finally
            {
                _cts.Dispose();
                try { Directory.Delete(Home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}