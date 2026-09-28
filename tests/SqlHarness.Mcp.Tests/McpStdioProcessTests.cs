using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

[CollectionDefinition("McpStdioProcess", DisableParallelization = true)]
public sealed class McpStdioProcessCollection;

/// <summary>
/// Shared harness for T6: publish the real single-file CLI and drive it as a
/// child process with the official SDK client. Synthetic SQLHARNESS_HOME only;
/// user profiles are never touched.
/// </summary>
internal static class McpStdioProcessHarness
{
    internal const string PinnedProtocolVersion = "2025-11-25";
    internal const string ProfileName = "mcp-stdio";

    internal static string CurrentRid =>
        OperatingSystem.IsWindows() ? "win-x64" :
        OperatingSystem.IsLinux() ? "linux-x64" :
        OperatingSystem.IsMacOS() ? "osx-arm64" :
        throw new PlatformNotSupportedException("No supported release RID for this OS.");

    private static readonly SemaphoreSlim PublishGate = new(1, 1);
    private static readonly Dictionary<string, string> Published = new(StringComparer.OrdinalIgnoreCase);

    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SqlHarness.sln")))
                return directory.FullName;
        }

        throw new FileNotFoundException("Could not locate the repository root (SqlHarness.sln).");
    }

    internal static string FindRepositoryFile(params string[] path)
    {
        var candidate = Path.Combine([FindRepositoryRoot(), .. path]);
        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException($"Could not locate repository file: {string.Join('/', path)}.");
    }

    /// <summary>
    /// Publishes the CLI exactly like the release workflow (self-contained,
    /// single-file, untrimmed) for the given RID. Cached per RID per run.
    /// </summary>
    internal static async Task<string> PublishAsync(string rid, CancellationToken ct)
    {
        lock (Published)
        {
            if (Published.TryGetValue(rid, out var cached) && File.Exists(cached))
                return cached;
        }

        await PublishGate.WaitAsync(ct);
        try
        {
            lock (Published)
            {
                if (Published.TryGetValue(rid, out var cached) && File.Exists(cached))
                    return cached;
            }

            var root = FindRepositoryRoot();
            var output = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-stdio-publish-" + Guid.NewGuid().ToString("N"), rid);
            Directory.CreateDirectory(output);
            var psi = new ProcessStartInfo(
                "dotnet",
                $"publish src/SqlHarness.Cli -c Release -r {rid} --self-contained true"
                + " -p:PublishSingleFile=true -p:PublishTrimmed=false"
                + " -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false"
                + $" -o \"{output}\"")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet publish did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException($"dotnet publish -r {rid} timed out after 10 minutes.");
            }

            var outText = await stdout;
            var errText = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"dotnet publish -r {rid} failed with exit {process.ExitCode}.\nSTDOUT:\n{Tail(outText)}\nSTDERR:\n{Tail(errText)}");

            var exe = Path.Combine(output, OperatingSystem.IsWindows() || rid.StartsWith("win-", StringComparison.Ordinal) ? "sqlharness.exe" : "sqlharness");
            if (!File.Exists(exe))
                throw new InvalidOperationException($"Publish succeeded but the single-file binary is missing: {exe}.");

            lock (Published)
                Published[rid] = exe;
            return exe;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private static string Tail(string text) =>
        text.Length <= 4000 ? text : text[^4000..];

    internal static string CreateSyntheticHome(string profileName, string targetsJson)
    {
        var home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-stdio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "targets.json"), targetsJson);
        return home;
    }

    internal static void DeleteHome(string home)
    {
        try
        {
            Directory.Delete(home, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed record StdioChild(Process Process, RecordingStream StdoutTee, Task<string> Stderr);

    internal static StdioChild StartServer(string exe, string home, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["SQLHARNESS_HOME"] = home;
        // The child must not inherit a stray test password; startup never
        // needs one for the synthetic integrated-auth profile.
        psi.Environment.Remove("SQLHARNESS_MCP_STDIO_PROBE_PASSWORD");
        var process = Process.Start(psi) ?? throw new InvalidOperationException("The published MCP server did not start.");
        var tee = new RecordingStream(process.StandardOutput.BaseStream);
        var stderr = process.StandardError.ReadToEndAsync();
        return new StdioChild(process, tee, stderr);
    }

    internal static async Task<McpClient> ConnectAsync(StdioChild child, Stream stdin, CancellationToken ct) =>
        await McpClient.CreateAsync(
            new StreamClientTransport(stdin, child.StdoutTee, NullLoggerFactory.Instance),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-stdio-tests", Version = "1.0.0" },
                ProtocolVersion = PinnedProtocolVersion,
            },
            NullLoggerFactory.Instance,
            ct);

    internal static JsonElement Envelope(CallToolResult result) =>
        JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement;

    internal static void AssertStdoutIsPureProtocol(byte[] recorded)
    {
        var text = Encoding.UTF8.GetString(recorded);
        var lines = text.Split('\n');
        var frames = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            frames++;
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("jsonrpc", out _), $"Stdout line is not a JSON-RPC frame: {line}");
        }

        Assert.True(frames > 0, "The server wrote no protocol frames to stdout.");
    }
}

/// <summary>
/// Read-only recording wrapper: every byte the SDK reads from the child
/// stdout is kept for the purity assertion. No bytes are ever written here.
/// </summary>
internal sealed class RecordingStream(Stream inner) : Stream
{
    private readonly MemoryStream _record = new();

    public byte[] Recorded
    {
        get
        {
            lock (_record)
                return _record.ToArray();
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            lock (_record)
                _record.Write(buffer, offset, read);
        }

        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        if (read > 0)
        {
            lock (_record)
                _record.Write(buffer, offset, read);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        if (read > 0)
        {
            lock (_record)
            {
                var span = buffer.Span[..read];
                foreach (var chunk in span)
                    _record.WriteByte(chunk);
            }
        }

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// T6 protocol proof over a real process: the official SDK client drives the
/// published single-file <c>sqlharness mcp serve</c> for the current RID
/// (initialize 2025-11-25, tools/list of 11, offline validate + plan,
/// malformed input survival, shutdown). No database tool runs here.
/// </summary>
[Collection("McpStdioProcess")]
public sealed class McpStdioProcessTests
{
    private static readonly TimeSpan SmokeBudget = TimeSpan.FromMinutes(6);

    [Fact]
    public async Task Published_single_file_server_passes_the_stdio_smoke_on_this_rid() =>
        await RunSmokeAsync(McpStdioProcessHarness.CurrentRid);

    [NativeRidFact("linux-x64")]
    public async Task Published_single_file_server_passes_the_stdio_smoke_on_linux_x64() =>
        await RunSmokeAsync("linux-x64");

    [NativeRidFact("osx-arm64")]
    public async Task Published_single_file_server_passes_the_stdio_smoke_on_osx_arm64() =>
        await RunSmokeAsync("osx-arm64");

    internal static async Task RunSmokeAsync(string rid)
    {
        using var cts = new CancellationTokenSource(SmokeBudget);
        var ct = cts.Token;
        var exe = await McpStdioProcessHarness.PublishAsync(rid, ct);
        var home = McpStdioProcessHarness.CreateSyntheticHome(
            McpStdioProcessHarness.ProfileName,
            """{"mcp-stdio": {"server": "mcp-unreachable.invalid", "database": "mcp-stdio-db", "vars": {"tenant": "^stdio$"}, "auth": "integrated"}}""");
        McpStdioProcessHarness.StdioChild? child = null;
        try
        {
            child = McpStdioProcessHarness.StartServer(exe, home, "mcp serve mcp-stdio --var tenant=stdio");
            var stdin = child.Process.StandardInput.BaseStream;
            await using var client = await McpStdioProcessHarness.ConnectAsync(child, stdin, ct);

            Assert.Equal(McpStdioProcessHarness.PinnedProtocolVersion, client.NegotiatedProtocolVersion);
            Assert.Equal(McpHost.ServerName, client.ServerInfo.Name);

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            Assert.Equal(
                McpToolCatalog.ToolNames.Order(StringComparer.Ordinal),
                tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());

            var validate = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" },
                cancellationToken: ct);
            AssertValidateSuccess(validate, "sqlharness_validate");

            var planXml = File.ReadAllText(McpStdioProcessHarness.FindRepositoryFile(
                "tests", "SqlHarness.Tests", "Fixtures", "distiller-sample.sqlplan"));
            var plan = await client.CallToolAsync(
                "sqlharness_plan",
                new Dictionary<string, object?> { ["content"] = planXml },
                cancellationToken: ct);
            AssertValidateSuccess(plan, "sqlharness_plan");

            // Malformed wire input: raw non-JSON bytes straight to stdin. The
            // process must survive and serve the next valid call.
            var garbage = Encoding.UTF8.GetBytes("THIS IS NOT JSON-RPC\r\n");
            await stdin.WriteAsync(garbage, ct);
            await stdin.FlushAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            Assert.False(child.Process.HasExited, "The published server died on malformed input.");
            var afterGarbage = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 2" },
                cancellationToken: ct);
            AssertValidateSuccess(afterGarbage, "sqlharness_validate");

            // Unknown tool is a protocol error, not a process failure.
            await Assert.ThrowsAsync<McpProtocolException>(async () => await client.CallToolAsync(
                "sqlharness_nope",
                new Dictionary<string, object?>(),
                cancellationToken: ct));
            Assert.False(child.Process.HasExited, "The published server died after an unknown tool call.");
            var afterUnknown = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 3" },
                cancellationToken: ct);
            AssertValidateSuccess(afterUnknown, "sqlharness_validate");

            await client.DisposeAsync();
            child.Process.StandardInput.Close();
            Assert.True(child.Process.WaitForExit(30_000), "The published server did not exit after stdin EOF.");
            Assert.Equal(0, child.Process.ExitCode);

            McpStdioProcessHarness.AssertStdoutIsPureProtocol(child.StdoutTee.Recorded);

            var stderr = await child.Stderr.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.DoesNotContain("mcp-unreachable.invalid", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("mcp-stdio-db", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("tenant=stdio", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("password", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (child is not null && !child.Process.HasExited)
            {
                try
                {
                    child.Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                child.Process.WaitForExit(10_000);
            }

            child?.Process.Dispose();
            McpStdioProcessHarness.DeleteHome(home);
        }
    }

    private static void AssertValidateSuccess(CallToolResult result, string command)
    {
        Assert.False(result.IsError == true, McpStdioProcessHarness.Envelope(result).ToString());
        var envelope = McpStdioProcessHarness.Envelope(result);
        Assert.Equal("success", envelope.GetProperty("status").GetString());
        Assert.Equal(0, envelope.GetProperty("exitCode").GetInt32());
        Assert.Equal(command, envelope.GetProperty("command").GetString());
        Assert.Empty(McpResultAdapter.ValidateEnvelope(envelope));
    }
}

/// <summary>
/// A same-shape smoke for a foreign RID. It runs only on a native runner of
/// that RID; anywhere else it reports Skip (never Pass), so a foreign binary
/// is never claimed working without native execution.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class NativeRidFactAttribute : FactAttribute
{
    public NativeRidFactAttribute(string rid)
    {
        if (!string.Equals(rid, McpStdioProcessHarness.CurrentRid, StringComparison.OrdinalIgnoreCase))
            Skip = $"Requires a native {rid} runner (this host is {McpStdioProcessHarness.CurrentRid}).";
    }
}

/// <summary>
/// Opt-in live scenarios against disposable databases of both engines. Each
/// case needs an explicit JSON profile object and never reads user profiles:
/// SQL Server via <c>SQLHARNESS_MCP_LIVE_SQLSERVER_JSON</c>, PostgreSQL via
/// <c>SQLHARNESS_MCP_LIVE_POSTGRES_JSON</c>, e.g.
/// <c>{"server":".","database":"mcp_probe","auth":"integrated"}</c>. Without
/// the variable the case skips. A SQL-auth profile may name its secret only
/// through <c>passwordEnvVar</c>; values never enter reports.
/// </summary>
[Collection("McpStdioProcess")]
public sealed class McpStdioLiveTests
{
    private static readonly TimeSpan LiveBudget = TimeSpan.FromMinutes(10);

    [McpLiveSqlServerFact]
    public async Task Live_sqlserver_stdio_drive() =>
        await RunLiveAsync(McpLiveSqlServerFactAttribute.Variable, "sqlserver");

    [McpLivePostgresFact]
    public async Task Live_postgres_stdio_drive() =>
        await RunLiveAsync(McpLivePostgresFactAttribute.Variable, "postgres");

    internal static async Task RunLiveAsync(string variable, string engine)
    {
        using var cts = new CancellationTokenSource(LiveBudget);
        var ct = cts.Token;
        var profileJson = Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"{variable} is not configured.");
        using var profile = JsonDocument.Parse(profileJson);
        Assert.Equal(JsonValueKind.Object, profile.RootElement.ValueKind);

        var home = McpStdioProcessHarness.CreateSyntheticHome(
            "mcp-live", "{\"mcp-live\": " + profileJson + "}");
        McpStdioProcessHarness.StdioChild? child = null;
        try
        {
            var exe = await McpStdioProcessHarness.PublishAsync(McpStdioProcessHarness.CurrentRid, ct);
            child = McpStdioProcessHarness.StartServer(exe, home, "mcp serve mcp-live");
            var stdin = child.Process.StandardInput.BaseStream;
            await using var client = await McpStdioProcessHarness.ConnectAsync(child, stdin, ct);
            Assert.Equal(McpStdioProcessHarness.PinnedProtocolVersion, client.NegotiatedProtocolVersion);

            var ping = await client.CallToolAsync(
                "sqlharness_inspect",
                new Dictionary<string, object?> { ["kind"] = "ping" },
                cancellationToken: ct);
            Assert.False(ping.IsError == true, McpStdioProcessHarness.Envelope(ping).ToString());

            var query = await client.CallToolAsync(
                "sqlharness_query",
                new Dictionary<string, object?> { ["sql"] = "SELECT 1 AS probe" },
                cancellationToken: ct);
            Assert.False(query.IsError == true, McpStdioProcessHarness.Envelope(query).ToString());

            var measure = await client.CallToolAsync(
                "sqlharness_measure",
                new Dictionary<string, object?>
                {
                    ["query"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1 AS probe" },
                    ["repeat"] = 2,
                },
                cancellationToken: ct);
            Assert.False(measure.IsError == true, McpStdioProcessHarness.Envelope(measure).ToString());

            var compare = await client.CallToolAsync(
                "sqlharness_compare",
                new Dictionary<string, object?>
                {
                    ["baseline"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1 AS probe" },
                    ["candidate"] = new Dictionary<string, object?> { ["sql"] = "SELECT 1 AS probe" },
                    ["repeat"] = 2,
                    ["compareResults"] = "multiset",
                },
                cancellationToken: ct);
            Assert.False(compare.IsError == true, McpStdioProcessHarness.Envelope(compare).ToString());

            // Persistent mutation is always off in MCP v1: the refusal must
            // arrive as a tool error and leave the process serving.
            var mutation = await client.CallToolAsync(
                "sqlharness_query",
                new Dictionary<string, object?> { ["sql"] = "DELETE FROM dbo.McpLiveProbe" },
                cancellationToken: ct);
            Assert.True(mutation.IsError == true, "MCP must refuse the persistent mutation.");
            Assert.False(child.Process.HasExited, "The server died while refusing the mutation.");

            // Cancel a long watch, then prove cleanup: the gate is free, the
            // next call succeeds, and EOF still shuts the process down.
            using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watch = client.CallToolAsync(
                "sqlharness_watch",
                new Dictionary<string, object?>
                {
                    ["sql"] = "SELECT 1 AS probe",
                    ["untilUnchanged"] = 10_000,
                    ["interval"] = "1s",
                    ["maxDuration"] = "30m",
                },
                cancellationToken: watchCts.Token);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            await watchCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watch);
            Assert.False(child.Process.HasExited, "The server died after watch cancellation.");

            var afterCancel = await client.CallToolAsync(
                "sqlharness_validate",
                new Dictionary<string, object?> { ["usage"] = "query", ["sql"] = "SELECT 1" },
                cancellationToken: ct);
            Assert.False(afterCancel.IsError == true, McpStdioProcessHarness.Envelope(afterCancel).ToString());

            await client.DisposeAsync();
            child.Process.StandardInput.Close();
            Assert.True(child.Process.WaitForExit(30_000), $"The {engine} live server did not exit after stdin EOF.");
            Assert.Equal(0, child.Process.ExitCode);
        }
        finally
        {
            if (child is not null && !child.Process.HasExited)
            {
                try
                {
                    child.Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                child.Process.WaitForExit(10_000);
            }

            child?.Process.Dispose();
            McpStdioProcessHarness.DeleteHome(home);
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class McpLiveSqlServerFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_MCP_LIVE_SQLSERVER_JSON";

    public McpLiveSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not configured. Point it at a disposable SQL Server profile object; user profiles are never used.";
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class McpLivePostgresFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_MCP_LIVE_POSTGRES_JSON";

    public McpLivePostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not configured. Point it at a disposable PostgreSQL profile object; user profiles are never used.";
    }
}
