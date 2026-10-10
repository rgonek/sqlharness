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
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// One smoke per offered revision over the production server options: tools/list,
/// a successful call, a mid-flight client cancellation that releases the gate, and
/// progress for a database call. Full suites stay on 2025-11-25.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpRevisionSmokeTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-smoke-" + Guid.NewGuid().ToString("N"));

    public McpRevisionSmokeTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_home, true);
    }

    public static TheoryData<string> Revisions => new() { "2025-06-18", "2025-11-25", "2026-07-28" };

    private sealed class SmokeModule : ISqlHarnessModule
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; }

        public async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            if (Block && operation is SqlHarnessQueryOperation)
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved.TrySetResult();
                    throw;
                }
            }

            return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        }
    }

    private static McpScope Scope(McpServerOptions? options = null) => McpScope.Create(
        options ?? new McpServerOptions { Profile = "mcp-t5" },
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-t5"] = new("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
        });

    private sealed class CapturingClientOutput(Stream inner) : Stream
    {
        private readonly StringBuilder _pending = new();
        public JsonElement? LastToolCallId { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Capture(buffer.AsSpan(offset, count));
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Capture(buffer);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Capture(buffer.Span);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        private void Capture(ReadOnlySpan<byte> bytes)
        {
            lock (_pending)
            {
                _pending.Append(Encoding.UTF8.GetString(bytes));
                while (true)
                {
                    var text = _pending.ToString();
                    var newline = text.IndexOf('\n');
                    if (newline < 0)
                        return;
                    var frame = text[..newline];
                    _pending.Clear();
                    _pending.Append(text[(newline + 1)..]);
                    using var document = JsonDocument.Parse(frame);
                    var root = document.RootElement;
                    if (root.TryGetProperty("method", out var method) && method.GetString() == "tools/call" && root.TryGetProperty("id", out var id))
                        LastToolCallId = id.Clone();
                }
            }
        }
    }

    private static async Task RunAsync(string revision, SmokeModule module, Func<McpClient, CancellationToken, Stream, Task> body, McpExecutionGate? executionGate = null)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var clientOutput = new CapturingClientOutput(clientToServer.Writer.AsStream());
        var options = McpHost.CreateServerOptions();
        McpToolCatalog.Wire(options, Scope(), module, executionGate ?? new McpExecutionGate());
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "smoke", NullLoggerFactory.Instance),
            options, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientOutput, serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "smoke", Version = "1" }, ProtocolVersion = revision },
                NullLoggerFactory.Instance, cts.Token);
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
            await body(client, cts.Token, clientOutput);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public Task Tools_list_and_a_call_work(string revision) => RunAsync(revision, new SmokeModule(), async (client, ct, _) =>
    {
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Assert.Equal(McpToolCatalog.ToolNames.OrderBy(n => n, StringComparer.Ordinal), tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));

        var gain = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: ct);
        Assert.NotEqual(true, gain.IsError);
    });

    [Theory]
    [MemberData(nameof(Revisions))]
    public Task Client_cancellation_reports_cancelled_and_releases_the_gate(string revision)
    {
        var module = new SmokeModule { Block = true };
        var gate = new McpExecutionGate();
        return RunAsync(revision, module, async (client, ct, clientOutput) =>
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var inflight = client.CallToolAsync("sqlharness_query", new Dictionary<string, object?> { ["sql"] = "SELECT 1" }, cancellationToken: call.Token);
            await module.Entered.Task.WaitAsync(Budget, ct);
            await call.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inflight.AsTask());

            var requestId = Assert.IsType<JsonElement>(clientOutput is CapturingClientOutput capture ? capture.LastToolCallId : null);
            using var frame = new MemoryStream();
            using (var writer = new Utf8JsonWriter(frame))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", "notifications/cancelled");
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                writer.WritePropertyName("requestId");
                requestId.WriteTo(writer);
                writer.WriteString("reason", "smoke cancellation");
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.Flush();
            }
            frame.WriteByte((byte)'\n');
            await clientOutput.WriteAsync(frame.ToArray(), ct);
            await clientOutput.FlushAsync(ct);
            await module.CancellationObserved.Task.WaitAsync(Budget, ct);

            // Client-side cancellation completes before the server finishes
            // cancellation cleanup. Wait until the shared execution gate is
            // observably free before sending the next database request.
            while (!gate.TryEnterDb())
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            gate.ExitDb();

            module.Block = false;
            var next = await client.CallToolAsync("sqlharness_query", new Dictionary<string, object?> { ["sql"] = "SELECT 1" }, cancellationToken: ct);
            Assert.False(next.IsError == true, Assert.Single(next.Content.OfType<TextContentBlock>()).Text);
        }, gate);
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public Task Progress_arrives_for_a_database_call(string revision) => RunAsync(revision, new SmokeModule(), async (client, ct, _) =>
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, async (_, _) =>
        {
            seen.TrySetResult();
            await Task.CompletedTask;
        });
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var query = tools.Single(t => t.Name == "sqlharness_query");

        await query.CallAsync(new Dictionary<string, object?> { ["sql"] = "SELECT 1" }, new Progress<ProgressNotificationValue>(_ => { }), null, ct);

        await seen.Task.WaitAsync(Budget, ct);
    });

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task Request_scope_works_on_every_revision(string revision)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(
            new McpServerOptions { RequestScope = true, AllowedProfiles = ["sample-a"] },
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), new StringWriter(),
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["sample-a"] = new("server-a.invalid", "database-a", new Dictionary<string, string> { ["tenant"] = "^a$" }, "integrated"),
            },
            cts.Token);
        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "smoke", Version = "1" }, ProtocolVersion = revision },
            NullLoggerFactory.Instance, cts.Token))
        {
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
            var capabilities = await client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.NotEqual(true, capabilities.IsError);
            var capabilitiesResult = JsonDocument.Parse(Assert.Single(capabilities.Content.OfType<TextContentBlock>()).Text).RootElement.GetProperty("result");
            Assert.Equal("request", capabilitiesResult.GetProperty("scopeMode").GetString());
            Assert.Equal(new[] { "sample-a" }, capabilitiesResult.GetProperty("allowedProfiles").EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Equal(McpHost.SupportedProtocolVersions,
                capabilitiesResult.GetProperty("supportedProtocolVersions").EnumerateArray().Select(value => value.GetString()!).ToArray());

            var missingScope = await client.CallToolAsync("sqlharness_validate",
                new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" }, cancellationToken: cts.Token);
            Assert.True(missingScope.IsError == true);

            var valid = await client.CallToolAsync("sqlharness_validate", new Dictionary<string, object?>
            {
                ["scope"] = new Dictionary<string, object?> { ["profile"] = "sample-a", ["vars"] = new Dictionary<string, string> { ["tenant"] = "a" } },
                ["usage"] = "query",
                ["sql"] = "SELECT 1",
            }, cancellationToken: cts.Token);
            Assert.NotEqual(true, valid.IsError);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));
    }
}