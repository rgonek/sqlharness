# MCP Protocol Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Negotiate MCP revisions `2025-06-18`, `2025-11-25` and `2026-07-28` instead of pinning `2025-11-25`, keep client identity in the journal on every revision, report the negotiated version in `sqlharness_capabilities`, and keep only SDK warnings and errors on stderr.

**Architecture:** `McpHost.CreateServerOptions` becomes the single place that builds SDK server options: `ProtocolVersion = null`, an incoming message filter that maps unoffered `initialize` versions to `2025-11-25`, and a `tools/call` request filter that records the request-scoped `ClientInfo` into a process-level `McpClientIdentity`. The journal identity reads that holder. Capabilities read `ctx.Server.NegotiatedProtocolVersion`. The stderr logger filters by level.

**Tech Stack:** .NET (net8/net10), xUnit, ModelContextProtocol 2.2.0, PowerShell 7 (acceptance script).

**Spec:** `docs/superpowers/specs/2026-10-10-mcp-protocol-upgrade-design.md` (evidence: `docs/superpowers/specs/2026-10-10-mcp-client-spike.md`).

## Global Constraints

- Offered revisions: `2025-06-18`, `2025-11-25`, `2026-07-28`. Handshake revisions (via `initialize`): `2025-06-18`, `2025-11-25`. Fallback: `2025-11-25`.
- `initialize` asking for `2024-11-05` or `2025-03-26` is answered with `2025-11-25`; an `initialize` without a string `protocolVersion` passes through unchanged.
- `McpHost.PinnedProtocolVersion` is removed; nothing in `src` names a single pinned revision.
- Journal `client_name` / `client_version` keep "first non-null per session" on every revision.
- `sqlharness_capabilities`: existing protocol-version field = negotiated version of the request (fallback `2025-11-25`); new `supportedProtocolVersions` = the three offered revisions in the order above.
- Stderr: only `Warning`, `Error`, `Critical` SDK events; line format unchanged (category, level, numeric event id; never state, exception or formatter output).
- Stdout stays protocol-only. Tools, budgets, gate, cancellation, progress, scope modes and the duplicate JSON field guard are unchanged.
- The acceptance script never edits `~/.claude.json`, `~/.claude/settings*.json`, or `~/.codex/config.toml`.
- Gates, one after the other: `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`.

## Review Focus

1. A `2026-07-28` client whose first `tools/call` lacks `io.modelcontextprotocol/clientInfo` — expected: no crash, identity filled by a later call that has it (Task 3 `Identity_is_taken_from_the_first_request_that_carries_it`).
2. Request-scope mode (`--request-scope`) on `2026-07-28` — expected: tools still require `scope` and a valid scope works (Task 5 `Request_scope_works_on_every_revision`); the duplicate JSON field guard wraps the input stream below the SDK, so it is revision-independent and stays covered by `McpProtocolTests`.
3. A client cancelling mid-flight on `2026-07-28` — expected: cancelled envelope, gate released, next call runs (Task 5 smoke).
4. `initialize` with `protocolVersion` as a number or missing — expected: passes through, SDK behaviour unchanged (Task 1 `Initialize_without_string_version_is_left_alone`).
5. The stdio process on every revision ends cleanly on stdin EOF with exit 0 and pure-protocol stdout (Task 6 `Process_stderr_has_no_trace_debug_or_information_lines`).

---

### Task 1: Negotiation — server options factory and version filter

**Files:**
- Modify: `src/SqlHarness.Mcp/McpHost.cs` (constants, new `CreateServerOptions`, use it in `RunAsync`)
- Create: `src/SqlHarness.Mcp/McpProtocolVersionFilter.cs`
- Create: `tests/SqlHarness.Mcp.Tests/McpProtocolNegotiationTests.cs`

**Interfaces:**
- Produces:
  - `McpHost.SupportedProtocolVersions : IReadOnlyList<string>` = `["2025-06-18", "2025-11-25", "2026-07-28"]`
  - `McpHost.HandshakeProtocolVersions : IReadOnlyList<string>` = `["2025-06-18", "2025-11-25"]`
  - `public const string McpHost.FallbackProtocolVersion = "2025-11-25"`
  - `public static ModelContextProtocol.Server.McpServerOptions McpHost.CreateServerOptions(McpClientIdentity? identity = null)` (the `identity` parameter is used from Task 3; in this task accept and ignore it)
  - `internal static class McpProtocolVersionFilter { McpMessageFilter Create(); }`

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Mcp.Tests/McpProtocolNegotiationTests.cs`:

```csharp
using System.IO.Pipelines;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SqlHarness.Mcp.Tests;

public sealed class McpProtocolNegotiationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static async Task<(string? Client, string? Server)> NegotiateAsync(string requested)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "negotiation", NullLoggerFactory.Instance),
            McpHost.CreateServerOptions(), NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "negotiation-tests", Version = "1.0.0" },
                    ProtocolVersion = requested,
                },
                NullLoggerFactory.Instance, cts.Token);
            await client.PingAsync(cancellationToken: cts.Token);
            return (client.NegotiatedProtocolVersion, server.NegotiatedProtocolVersion);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Offered_revisions_are_negotiated_as_requested(string requested)
    {
        var (client, _) = await NegotiateAsync(requested);

        Assert.Equal(requested, client);
    }

    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-03-26")]
    public async Task Older_handshake_revisions_are_answered_with_the_fallback(string requested)
    {
        var (client, server) = await NegotiateAsync(requested);

        // The SDK client accepts the server's answer when it knows that revision.
        Assert.Equal(McpHost.FallbackProtocolVersion, client);
        Assert.Equal(McpHost.FallbackProtocolVersion, server);
    }

    [Fact]
    public void Constants_describe_the_offered_revisions()
    {
        Assert.Equal(["2025-06-18", "2025-11-25", "2026-07-28"], McpHost.SupportedProtocolVersions);
        Assert.Equal(["2025-06-18", "2025-11-25"], McpHost.HandshakeProtocolVersions);
        Assert.Equal("2025-11-25", McpHost.FallbackProtocolVersion);
        Assert.Null(McpHost.CreateServerOptions().ProtocolVersion);
    }

    [Fact]
    public async Task Initialize_without_string_version_is_left_alone()
    {
        var request = new JsonRpcRequest
        {
            Method = RequestMethods.Initialize,
            Id = new RequestId(1),
            Params = JsonNode.Parse("""{"protocolVersion":20251125,"capabilities":{},"clientInfo":{"name":"x","version":"1"}}"""),
        };
        var seen = await RunFilterAsync(request);
        Assert.Equal(20251125, seen!["protocolVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Filter_rewrites_only_unoffered_initialize_versions()
    {
        static JsonRpcRequest Initialize(string version) => new()
        {
            Method = RequestMethods.Initialize,
            Id = new RequestId(1),
            Params = JsonNode.Parse($$"""{"protocolVersion":"{{version}}","capabilities":{},"clientInfo":{"name":"x","version":"1"}}"""),
        };

        Assert.Equal("2025-11-25", (await RunFilterAsync(Initialize("2024-11-05")))!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("2025-06-18", (await RunFilterAsync(Initialize("2025-06-18")))!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("2099-01-01", (await RunFilterAsync(Initialize("2099-01-01")))!["protocolVersion"]!.GetValue<string>());

        var ping = new JsonRpcRequest { Method = RequestMethods.Ping, Id = new RequestId(2), Params = JsonNode.Parse("""{"protocolVersion":"2024-11-05"}""") };
        Assert.Equal("2024-11-05", (await RunFilterAsync(ping))!["protocolVersion"]!.GetValue<string>());
    }

    private static async Task<JsonNode?> RunFilterAsync(JsonRpcMessage message)
    {
        JsonNode? seen = null;
        var handler = McpProtocolVersionFilter.Create()((context, _) =>
        {
            seen = (context.JsonRpcMessage as JsonRpcRequest)?.Params;
            return Task.CompletedTask;
        });
        await handler(new MessageContext(null!, message), CancellationToken.None);
        return seen;
    }
}
```

Note on `"2099-01-01"`: an unknown future version passes the filter unchanged on purpose; the SDK answers it with `2025-11-25` itself. Only the two known, unoffered revisions are rewritten.

`MessageContext`'s constructor shape is not verified; if `new MessageContext(null!, message)` does not compile, construct it the way the SDK exposes (inspect `ModelContextProtocol.Server.MessageContext` constructors) or drop the two filter-unit tests and rely on the negotiation theories, which exercise the filter end to end.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpProtocolNegotiationTests"`
Expected: compile errors (`CreateServerOptions`, `McpProtocolVersionFilter`, constants missing).

- [ ] **Step 3: Implement**

`src/SqlHarness.Mcp/McpProtocolVersionFilter.cs`:

```csharp
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SqlHarness.Mcp;

/// <summary>
/// Narrows the SDK's handshake revisions to the ones SQLHarness tests. With
/// ProtocolVersion = null the SDK would answer an initialize for 2024-11-05 or
/// 2025-03-26 with that revision; this filter rewrites those two requested values
/// to the fallback before the SDK reads them, which is ordinary MCP negotiation
/// (the server answers with a version it supports). Every other message passes
/// unchanged, including an initialize without a string version.
/// </summary>
internal static class McpProtocolVersionFilter
{
    private static readonly HashSet<string> Unoffered = new(StringComparer.Ordinal) { "2024-11-05", "2025-03-26" };

    internal static McpMessageFilter Create() => next => async (context, ct) =>
    {
        if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.Initialize, Params: JsonObject parameters }
            && parameters["protocolVersion"] is JsonValue value
            && value.TryGetValue<string>(out var requested)
            && Unoffered.Contains(requested))
        {
            parameters["protocolVersion"] = McpHost.FallbackProtocolVersion;
        }

        await next(context, ct);
    };
}
```

`McpHost.cs` — replace the `PinnedProtocolVersion` constant with:

```csharp
    /// <summary>Tested revisions SQLHarness offers, newest last.</summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = ["2025-06-18", "2025-11-25", "2026-07-28"];

    /// <summary>Offered revisions that use the initialize handshake.</summary>
    public static readonly IReadOnlyList<string> HandshakeProtocolVersions = ["2025-06-18", "2025-11-25"];

    /// <summary>Answer to an initialize for a revision SQLHarness does not offer.</summary>
    public const string FallbackProtocolVersion = "2025-11-25";

    /// <summary>
    /// SDK server options shared by the host and protocol tests: no pinned revision
    /// (the SDK negotiates, including server/discover for 2026-07-28) and the
    /// filters that narrow the offered revisions and record client identity.
    /// </summary>
    public static ModelContextProtocol.Server.McpServerOptions CreateServerOptions(McpClientIdentity? identity = null)
    {
        var options = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = ServerName,
                Version = ServerVersion,
            },
            ProtocolVersion = null,
        };
        options.Filters.Message.IncomingFilters.Add(McpProtocolVersionFilter.Create());
        return options;
    }
```

In `RunAsync`, replace the inline `new ModelContextProtocol.Server.McpServerOptions { ... }` with `var serverOptions = CreateServerOptions();`.

`McpClientIdentity` does not exist yet; to keep this task compiling, create the empty class now (Task 3 fills it):

```csharp
namespace SqlHarness.Mcp;

/// <summary>Process-level client identity; filled in Task 3.</summary>
public sealed class McpClientIdentity
{
}
```

in `src/SqlHarness.Mcp/McpClientIdentity.cs`.

Leave `McpOperationMapper`'s two `McpHost.PinnedProtocolVersion` uses and the test uses for Task 2; to compile this task, temporarily keep `public const string PinnedProtocolVersion = FallbackProtocolVersion;` (no `[Obsolete]`: the build may treat warnings as errors) — Task 2 deletes it.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpProtocolNegotiationTests"`
Expected: PASS.

**If `Older_handshake_revisions_are_answered_with_the_fallback` fails because the SDK reads `protocolVersion` before message filters run**, use the stream fallback instead of the message filter: add `src/SqlHarness.Mcp/McpProtocolVersionRewriteInput.cs`, a `Stream` wrapper built like `McpDuplicateJsonFieldGuardInput` (same newline framing and buffering — copy its structure), that parses each complete line with `JsonNode.Parse`, and when it is an object with `"method":"initialize"` and a string `params.protocolVersion` in the unoffered set, re-serializes the line with the fallback value; all other lines pass through byte-for-byte. Wrap it around `eofInput` in `RunAsync` (inside the duplicate guard when request scope is on), remove the message filter registration, and change the filter-unit tests to drive the stream wrapper with `MemoryStream` input the way `Duplicate_wire_fields_are_replaced_before_sdk_parsing_and_next_frame_survives` does. The negotiation theories then construct the server over the wrapped stream. Record which path was taken in the commit message.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests/McpProtocolNegotiationTests.cs
git commit -m "feat(mcp): negotiate 2025-06-18, 2025-11-25 and 2026-07-28"
```

---

### Task 2: Remove the pinned revision

**Files:**
- Modify: `src/SqlHarness.Mcp/McpHost.cs` (delete `PinnedProtocolVersion`)
- Modify: `src/SqlHarness.Mcp/McpOperationMapper.cs:106,146` (temporary `FallbackProtocolVersion`; Task 4 makes it negotiated)
- Modify (tests): `McpGainTests.cs`, `McpJournalTests.cs`, `McpLifecycleTests.cs`, `McpTokenBudgetTests.cs`, `McpStdioProcessTests.cs`, `McpStartupTests.cs`, `McpStderrLeakRegressionTests.cs`, and every other file the compiler reports

**Interfaces:**
- Consumes: `McpHost.FallbackProtocolVersion` (Task 1).
- Produces: `McpStdioProcessHarness.DefaultProtocolVersion = "2025-11-25"` (renamed from `PinnedProtocolVersion`); `McpStdioProcessHarness.ConnectAsync(StdioChild child, Stream stdin, CancellationToken ct, string protocolVersion = DefaultProtocolVersion)`.

- [ ] **Step 1: Delete the constant and fix the compiler errors mechanically**

Delete `PinnedProtocolVersion` from `McpHost`. Then:
- `src/SqlHarness.Mcp/McpOperationMapper.cs`: `McpHost.PinnedProtocolVersion` → `McpHost.FallbackProtocolVersion` (both places).
- Tests: `McpHost.PinnedProtocolVersion` → `McpHost.FallbackProtocolVersion`.
- `McpStdioProcessTests.cs`: rename `McpStdioProcessHarness.PinnedProtocolVersion` to `DefaultProtocolVersion` and update its uses (`McpStderrLeakRegressionTests.cs` and others); add the optional `protocolVersion` parameter to `ConnectAsync` and use it for `McpClientOptions.ProtocolVersion`.
- Local `private const string PinnedProtocolVersion = "2025-11-25";` in `McpProtocolTests.cs` and `McpStartupTests.cs`: rename to `HandshakeVersion` (they build their own pinned SDK server on purpose and stay as they are).

Run: `grep -rn "PinnedProtocolVersion" src tests` — expected: no matches.

- [ ] **Step 2: Run the MCP suite**

Run: `dotnet test tests/SqlHarness.Mcp.Tests`
Expected: PASS (behaviour is unchanged for clients requesting `2025-11-25`). If a host test is flaky under load, rerun it alone first — the MCP host handshake tests have known pre-existing flakiness.

- [ ] **Step 3: Commit**

```bash
git add src tests
git commit -m "refactor(mcp): drop the pinned protocol revision constant"
```

---

### Task 3: Client identity from the request-scoped server

**Files:**
- Modify: `src/SqlHarness.Mcp/McpClientIdentity.cs`
- Modify: `src/SqlHarness.Mcp/McpHost.cs` (`CreateServerOptions` registers the filter; `RunAsync` builds identity from the holder)
- Create: `tests/SqlHarness.Mcp.Tests/McpClientIdentityTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class McpClientIdentity { public void Record(Implementation? clientInfo); public string? Name { get; } public string? Version { get; } }` — first non-null `Implementation` wins; thread safe.
  - `CreateServerOptions(identity)` adds a `tools/call` request filter calling `identity.Record(context.Server?.ClientInfo)` before the handler when `identity` is not null.

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Mcp.Tests/McpClientIdentityTests.cs`:

```csharp
using System.IO.Pipelines;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp.Tests;

[Collection("McpScopeHome")]
public sealed class McpClientIdentityTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-mcp-identity-" + Guid.NewGuid().ToString("N"));

    public McpClientIdentityTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        SqliteConnection.ClearAllPools();
        Directory.Delete(_home, true);
    }

    [Fact]
    public void First_non_null_client_info_wins()
    {
        var identity = new McpClientIdentity();
        identity.Record(null);
        identity.Record(new Implementation { Name = "claude-code", Version = "2.1.296" });
        identity.Record(new Implementation { Name = "other", Version = "9" });

        Assert.Equal(("claude-code", "2.1.296"), (identity.Name, identity.Version));
    }

    [Fact]
    public void Identity_is_taken_from_the_first_request_that_carries_it()
    {
        var identity = new McpClientIdentity();
        identity.Record(null);
        Assert.Null(identity.Name);
        identity.Record(new Implementation { Name = "late", Version = "1" });
        Assert.Equal("late", identity.Name);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Journal_session_has_client_info_on_every_revision(string revision)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var hostTask = McpHost.RunAsync(
            new McpServerOptions { Profile = "mcp-t5" },
            clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), new StringWriter(),
            () => new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["mcp-t5"] = new TargetProfile("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
            },
            cts.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "claude-code", Version = "9.9.9" }, ProtocolVersion = revision },
            NullLoggerFactory.Instance, cts.Token))
        {
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
            var result = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            Assert.NotEqual(true, result.IsError);
        }

        await clientToServer.Writer.CompleteAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask.WaitAsync(Budget, cts.Token));

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_home, "data", "activity.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_kind, source, client_name, client_version FROM sessions";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("claude", "mcp-clientinfo", "claude-code", "9.9.9"),
            (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpClientIdentityTests"`
Expected: compile error (`Record`, `Name`, `Version` missing); after a stub, the `2026-07-28` theory case fails with NULL `client_name` (the regression this task fixes).

- [ ] **Step 3: Implement**

`McpClientIdentity.cs`:

```csharp
using ModelContextProtocol.Protocol;

namespace SqlHarness.Mcp;

/// <summary>
/// Client identity for one serve process. On 2026-07-28 client info arrives per request
/// in _meta and the root server's ClientInfo is null, so every tools/call records the
/// request-scoped value; the first non-null one wins, matching the journal's
/// first-non-null session rule.
/// </summary>
public sealed class McpClientIdentity
{
    private Implementation? _client;

    public void Record(Implementation? clientInfo)
    {
        if (clientInfo is not null)
            Interlocked.CompareExchange(ref _client, clientInfo, null);
    }

    public string? Name => Volatile.Read(ref _client)?.Name;

    public string? Version => Volatile.Read(ref _client)?.Version;
}
```

`McpHost.CreateServerOptions` — after the message filter line:

```csharp
        if (identity is not null)
        {
            options.Filters.Request.CallToolFilters.Add(next => (context, ct) =>
            {
                identity.Record(context.Server?.ClientInfo);
                return next(context, ct);
            });
        }
```

`McpHost.RunAsync`: create `var clientIdentity = new McpClientIdentity();`, call `CreateServerOptions(clientIdentity)`, and change the identity lazy to:

```csharp
        // One identity per serve process: resolved lazily on the first journaled call.
        // Client info comes from the request-scoped server (2026-07-28 has no initialize);
        // the root server's value covers handshake revisions when no call recorded one.
        var identity = new Lazy<SessionIdentity>(
            () => SessionIdentities.Mcp(
                ProcessInfo.Current,
                sessionKey,
                clientIdentity.Name ?? running?.ClientInfo?.Name,
                clientIdentity.Version ?? running?.ClientInfo?.Version,
                mcpMode),
            LazyThreadSafetyMode.ExecutionAndPublication);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpClientIdentityTests|FullyQualifiedName~McpJournalTests"`
Expected: PASS for all three revisions.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests/McpClientIdentityTests.cs
git commit -m "fix(mcp): keep client identity on 2026-07-28 per-request metadata"
```

---

### Task 4: Capabilities report the negotiated version

**Files:**
- Modify: `src/SqlHarness.Mcp/McpOperationMapper.cs` (`McpCapabilitiesDocument`, both `BuildCapabilities` overloads)
- Modify: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`CapabilitiesAsync`)
- Modify: `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs` (`CapabilitiesAsync`)
- Test: `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`, `tests/SqlHarness.Mcp.Tests/McpProtocolNegotiationTests.cs`

**Interfaces:**
- Produces: `McpCapabilitiesDocument(..., IReadOnlyList<string>? AllowedProfiles = null, IReadOnlyList<string>? SupportedProtocolVersions = null)`; `BuildCapabilities(McpScope scope, bool includeDiagnostics, string? protocolVersion = null)` and `BuildCapabilities(McpProcessContext context, bool includeDiagnostics, string? protocolVersion = null)`; JSON field `supportedProtocolVersions`.

- [ ] **Step 1: Write the failing tests**

`McpMappingTests` — next to the existing `BuildCapabilities` tests:

```csharp
    [Fact]
    public void Capabilities_carry_negotiated_and_supported_versions()
    {
        var negotiated = McpOperationMapper.BuildCapabilities(Scope(), includeDiagnostics: false, protocolVersion: "2026-07-28");
        var unknown = McpOperationMapper.BuildCapabilities(Scope(), includeDiagnostics: false);

        Assert.Equal("2026-07-28", negotiated.ProtocolVersion);
        Assert.Equal(McpHost.FallbackProtocolVersion, unknown.ProtocolVersion);
        Assert.Equal(McpHost.SupportedProtocolVersions, negotiated.SupportedProtocolVersions);
    }
```

(Use the file's existing `Scope()` helper, as `BuildCapabilities(Scope(), ...)` at line ~806 does.)

`McpProtocolNegotiationTests` — add a theory that calls the real tool over the wire on each revision:

```csharp
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Capabilities_tool_reports_the_negotiated_revision(string revision)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var options = McpHost.CreateServerOptions();
        var scope = McpScope.Create(
            new McpServerOptions { Profile = "mcp-t5" },
            new Dictionary<string, SqlHarness.Core.Targets.TargetProfile>(StringComparer.Ordinal)
            {
                ["mcp-t5"] = new("mcp-unreachable.invalid", "reportdb", new Dictionary<string, string>(), "integrated"),
            });
        SqlHarness.Mcp.Tools.McpToolCatalog.Wire(options, scope, scope.CreateModule());
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "caps", NullLoggerFactory.Instance),
            options, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "caps", Version = "1" }, ProtocolVersion = revision },
                NullLoggerFactory.Instance, cts.Token);
            var result = await client.CallToolAsync("sqlharness_capabilities", new Dictionary<string, object?>(), cancellationToken: cts.Token);
            var envelope = System.Text.Json.JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement;
            var document = envelope.GetProperty("result");

            Assert.Equal(revision, document.GetProperty("protocolVersion").GetString());
            Assert.Equal(McpHost.SupportedProtocolVersions,
                document.GetProperty("supportedProtocolVersions").EnumerateArray().Select(v => v.GetString()!).ToArray());
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }
```

Before relying on `envelope.GetProperty("result")`, open an existing capabilities test in `McpLifecycleTests` or `McpOutputTests` to confirm the envelope shape (`result` key, camelCase `protocolVersion`) and adjust the property path to match.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~Capabilities"`
Expected: compile error (no `protocolVersion` parameter, no `SupportedProtocolVersions`).

- [ ] **Step 3: Implement**

`McpCapabilitiesDocument`: append `IReadOnlyList<string>? SupportedProtocolVersions = null` after `AllowedProfiles`.

Both `BuildCapabilities` overloads: add `string? protocolVersion = null`; pass `protocolVersion ?? McpHost.FallbackProtocolVersion` where `McpHost.FallbackProtocolVersion` is used today, and `SupportedProtocolVersions: McpHost.SupportedProtocolVersions` as a named argument. The fixed-scope branch of the process overload forwards `protocolVersion`.

`McpToolHandlers.CapabilitiesAsync`:

```csharp
                McpOperationMapper.BuildCapabilities(scope, includeDiagnostics, ctx?.Server?.NegotiatedProtocolVersion),
```

`McpRequestToolHandlers.CapabilitiesAsync`:

```csharp
            McpOperationMapper.BuildCapabilities(process, includeDiagnostics, ctx?.Server?.NegotiatedProtocolVersion), null));
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/SqlHarness.Mcp.Tests`
Expected: PASS. If `McpTokenBudgetTests` or a tools/list size test fails because the capabilities result grew, check the new size stays inside the default 16384 B result budget and update only a byte-count assertion that pins the exact old size.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp tests/SqlHarness.Mcp.Tests
git commit -m "feat(mcp): report negotiated and supported protocol versions in capabilities"
```

---

### Task 5: Per-revision smoke

**Files:**
- Create: `tests/SqlHarness.Mcp.Tests/McpRevisionSmokeTests.cs`
- Modify: `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs` (`Eof_on_stdin_shuts_the_host_down_cleanly` → theory over revisions)

**Interfaces:**
- Consumes: `McpHost.CreateServerOptions`, `McpHost.SupportedProtocolVersions`, `McpToolCatalog.Wire(options, scope, module, gate, clock)`.

- [ ] **Step 1: Write the tests**

`tests/SqlHarness.Mcp.Tests/McpRevisionSmokeTests.cs`:

```csharp
using System.IO.Pipelines;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

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
        public bool Block { get; set; }

        public async Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            if (Block && operation is SqlHarnessQueryOperation)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
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

    private static async Task RunAsync(string revision, SmokeModule module, Func<McpClient, CancellationToken, Task> body)
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var options = McpHost.CreateServerOptions();
        McpToolCatalog.Wire(options, Scope(), module, new McpExecutionGate());
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "smoke", NullLoggerFactory.Instance),
            options, NullLoggerFactory.Instance, serviceProvider: null);
        var serverTask = server.RunAsync(cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "smoke", Version = "1" }, ProtocolVersion = revision },
                NullLoggerFactory.Instance, cts.Token);
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
            await body(client, cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public Task Tools_list_and_a_call_work(string revision) => RunAsync(revision, new SmokeModule(), async (client, ct) =>
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
        return RunAsync(revision, module, async (client, ct) =>
        {
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var inflight = client.CallToolAsync("sqlharness_query", new Dictionary<string, object?> { ["sql"] = "SELECT 1" }, cancellationToken: call.Token);
            await module.Entered.Task.WaitAsync(Budget, ct);
            await call.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inflight);

            module.Block = false;
            var next = await client.CallToolAsync("sqlharness_query", new Dictionary<string, object?> { ["sql"] = "SELECT 1" }, cancellationToken: ct);
            Assert.NotEqual(true, next.IsError);
        });
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public Task Progress_arrives_for_a_database_call(string revision) => RunAsync(revision, new SmokeModule(), async (client, ct) =>
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (_, _) =>
        {
            seen.TrySetResult();
            return ValueTask.CompletedTask;
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
```

`McpRequestScopeTests` / `McpLifecycleTests` hold the exact error envelope for a missing scope; this smoke only checks that the call is an error on every revision. The class joins the `McpScopeHome` collection and isolates `SQLHARNESS_HOME` because `McpHost.RunAsync` writes to the journal.

`McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly`: turn it into `[Theory] [InlineData("2025-06-18")] [InlineData("2025-11-25")] [InlineData("2026-07-28")]` with a `string revision` parameter, use `ProtocolVersion = revision`, and assert `Assert.Equal(revision, client.NegotiatedProtocolVersion)`.

- [ ] **Step 2: Run them**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpRevisionSmokeTests|FullyQualifiedName~Eof_on_stdin"`
Expected: PASS on all three revisions. A `2026-07-28` failure here is a real finding: fix the host (not the test), or stop and report if the SDK behaves against its documentation.

- [ ] **Step 3: Commit**

```bash
git add tests/SqlHarness.Mcp.Tests
git commit -m "test(mcp): smoke every offered protocol revision"
```

---

### Task 6: Stderr keeps only warnings and errors

**Files:**
- Modify: `src/SqlHarness.Mcp/McpHost.cs` (`McpStderrLogger.IsEnabled` and its comment)
- Modify: `tests/SqlHarness.Mcp.Tests/McpStderrLeakRegressionTests.cs` (new stdio test)

- [ ] **Step 1: Write the failing test**

Add to `McpStderrLeakRegressionTests` (same collection, same harness):

```csharp
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Process_stderr_has_no_trace_debug_or_information_lines(string revision)
    {
        using var cts = new CancellationTokenSource(LeakBudget);
        var ct = cts.Token;
        var exe = await McpStdioProcessHarness.PublishAsync(McpStdioProcessHarness.CurrentRid, ct);
        var home = McpStdioProcessHarness.CreateSyntheticHome(
            McpStdioProcessHarness.ProfileName,
            """{"mcp-stdio": {"server": "mcp-unreachable.invalid", "database": "mcp-stdio-db", "vars": {}, "auth": "integrated"}}""");
        McpStdioProcessHarness.StdioChild? child = null;
        try
        {
            child = McpStdioProcessHarness.StartServer(exe, home, "mcp serve mcp-stdio");
            var stdin = child.Process.StandardInput.BaseStream;
            var client = await McpStdioProcessHarness.ConnectAsync(child, stdin, ct, revision);
            Assert.Equal(revision, client.NegotiatedProtocolVersion);
            var gain = await client.CallToolAsync("sqlharness_gain", new Dictionary<string, object?>(), cancellationToken: ct);
            Assert.NotEqual(true, gain.IsError);
            await client.DisposeAsync();
            child.Process.StandardInput.Close();
            Assert.True(child.Process.WaitForExit(30_000), "The published server did not exit after stdin EOF.");
            Assert.Equal(0, child.Process.ExitCode);
            await child.StdoutTee.DrainRemainingAsync(TimeSpan.FromSeconds(10), ct);
            McpStdioProcessHarness.AssertStdoutIsPureProtocol(child.StdoutTee.Recorded);

            var stderr = await child.Stderr.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.DoesNotContain(": Trace (event", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(": Debug (event", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(": Information (event", stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (child is not null && !child.Process.HasExited)
                child.Process.Kill(entireProcessTree: true);
            McpStdioProcessHarness.DeleteHome(home);
        }
    }
```

Match the existing test's `finally` block exactly (it may wrap `Kill` in a try/catch and dispose the process); copy it rather than the simplified version above.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~Process_stderr_has_no_trace"`
Expected: FAIL (stderr contains `: Trace (event` lines today).

- [ ] **Step 3: Implement**

In `McpStderrLogger`:

```csharp
            // Noise reduction only: SDK Trace/Debug/Information events carry no text here and
            // only fill client logs. The leak protection is that Log never renders state,
            // exception, or formatter output, at any level.
            public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Warning or LogLevel.Error or LogLevel.Critical;
```

and make `Log` return early when `!IsEnabled(logLevel)` (the SDK usually checks, but the logger must not depend on it):

```csharp
                if (!IsEnabled(logLevel))
                    return;
```

Replace the old "Always enabled: the level gate is not the protection..." comment with the one above.

- [ ] **Step 4: Run the stderr and leak tests**

Run: `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpStderrLeakRegressionTests|FullyQualifiedName~McpStdioProcessTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Mcp/McpHost.cs tests/SqlHarness.Mcp.Tests/McpStderrLeakRegressionTests.cs
git commit -m "fix(mcp): keep only SDK warnings and errors on stderr"
```

---

### Task 7: Real-client acceptance script

**Files:**
- Create: `scripts/mcp-client-acceptance.ps1`
- Create: `scripts/mcp-client-acceptance-tap.py`

**Interfaces:**
- Produces: `pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness ./artifacts/task7-local/sqlharness.exe [-Profile <name>] [-SkipClaude] [-SkipCodex]`; `-Sqlharness` is required and must point to the worktree-local published executable. Exit 0 when every executed check passes, 1 otherwise.

- [ ] **Step 1: Write the tap**

`scripts/mcp-client-acceptance-tap.py` — transparent stdio tap (forwards bytes unchanged, logs each newline-delimited frame with direction):

```python
"""Transparent stdio tap for an MCP server. Forwards bytes unchanged and appends
each newline-delimited JSON-RPC frame to a log with its direction.
Usage: python -I mcp-client-acceptance-tap.py <log-file> <server-command> [args...]
The log contains tool results; keep it local."""
import subprocess
import sys
import threading
import time

log_path, command = sys.argv[1], sys.argv[2:]
log = open(log_path, "a", encoding="utf-8")
lock = threading.Lock()


def record(direction, line):
    with lock:
        log.write(f"{time.strftime('%H:%M:%S')} {direction} {line.decode('utf-8', 'replace').rstrip()}\n")
        log.flush()


server = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def pump(source, sink, direction):
    for line in iter(source.readline, b""):
        record(direction, line)
        sink.write(line)
        sink.flush()
    if direction == "C->S":
        server.stdin.close()


def drain_stderr():
    for line in iter(server.stderr.readline, b""):
        record("ERR", line)


threading.Thread(target=pump, args=(sys.stdin.buffer, server.stdin, "C->S"), daemon=True).start()
threading.Thread(target=drain_stderr, daemon=True).start()
pump(server.stdout, sys.stdout.buffer, "S->C")
sys.exit(server.wait())
```

- [ ] **Step 2: Write the driver**

`scripts/mcp-client-acceptance.ps1`:

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
    Manual MCP acceptance against real Claude Code and Codex clients (not CI).

.DESCRIPTION
    Runs sqlharness mcp serve behind a logging stdio tap through headless
    `claude -p` and `codex exec`, with one-off MCP configuration only: no client
    configuration file is edited. Each client calls sqlharness_capabilities once.
    Checks the negotiated revision (Claude Code: 2026-07-28 via server/discover;
    Codex: 2025-06-18), the revision the capabilities result reports, and that the
    session's clientInfo name was received. Frames contain tool results and stay
    in a temporary directory that is printed at the end.
#>
[CmdletBinding()]
param(
    [string]$Sqlharness = (Get-Command sqlharness -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source,
    [string]$Profile = 'local-playground',
    [switch]$SkipClaude,
    [switch]$SkipCodex
)

$ErrorActionPreference = 'Stop'
$python = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$tap = Join-Path $PSScriptRoot 'mcp-client-acceptance-tap.py'
$work = Join-Path ([IO.Path]::GetTempPath()) ("sqlharness-mcp-acceptance-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$prompt = 'Call the sqlharness_capabilities tool from the acceptance MCP server exactly once, then reply with one word: done.'
$results = [System.Collections.Generic.List[object]]::new()

function Read-Frames([string]$log) {
    Get-Content $log | ForEach-Object {
        $parts = $_ -split ' ', 3
        if ($parts.Count -eq 3 -and $parts[1] -ne 'ERR') {
            try { [pscustomobject]@{ Direction = $parts[1]; Message = ($parts[2] | ConvertFrom-Json -Depth 64) } } catch { }
        }
    }
}

function Test-Client([string]$name, [string]$log, [string]$expected) {
    $frames = @(Read-Frames $log)
    $toServer = $frames | Where-Object Direction -eq 'C->S'
    $init = $toServer | Where-Object { $_.Message.method -eq 'initialize' } | Select-Object -First 1
    $discover = $toServer | Where-Object { $_.Message.method -eq 'server/discover' } | Select-Object -First 1
    $call = $toServer | Where-Object { $_.Message.method -eq 'tools/call' } | Select-Object -First 1
    $negotiated = if ($expected -eq '2026-07-28') {
        $call.Message.params._meta.'io.modelcontextprotocol/protocolVersion'
    } else {
        ($frames | Where-Object { $_.Direction -eq 'S->C' -and $_.Message.result.serverInfo } | Select-Object -First 1).Message.result.protocolVersion
    }
    $clientName = if ($init) { $init.Message.params.clientInfo.name } else { $call.Message.params._meta.'io.modelcontextprotocol/clientInfo'.name }
    $capsText = ($frames | Where-Object { $_.Direction -eq 'S->C' -and $_.Message.id -eq $call.Message.id } | Select-Object -First 1).Message.result.content[0].text
    $reported = if ($capsText) { ($capsText | ConvertFrom-Json -Depth 64).result.protocolVersion } else { $null }
    $results.Add([pscustomobject]@{ Client = $name; Check = 'negotiated'; Expected = $expected; Actual = $negotiated; Pass = $negotiated -eq $expected })
    $results.Add([pscustomobject]@{ Client = $name; Check = 'capabilities.protocolVersion'; Expected = $expected; Actual = $reported; Pass = $reported -eq $expected })
    $results.Add([pscustomobject]@{ Client = $name; Check = 'clientInfo.name present'; Expected = 'non-empty'; Actual = $clientName; Pass = -not [string]::IsNullOrWhiteSpace($clientName) })
    if ($expected -eq '2026-07-28') {
        $results.Add([pscustomobject]@{ Client = $name; Check = 'server/discover used'; Expected = 'yes'; Actual = [bool]$discover; Pass = [bool]$discover })
    }
}

if (-not $SkipClaude) {
    $log = Join-Path $work 'claude-frames.log'
    $config = @{ mcpServers = @{ acceptance = @{ command = $python; args = @('-I', $tap, $log, $Sqlharness, 'mcp', 'serve', $Profile) } } } |
        ConvertTo-Json -Depth 8
    $configPath = Join-Path $work 'claude-mcp.json'
    Set-Content -Path $configPath -Value $config -Encoding utf8NoBOM
    Push-Location $work
    try {
        claude -p $prompt --mcp-config $configPath --strict-mcp-config --allowedTools 'mcp__acceptance__sqlharness_capabilities' --model haiku | Out-Null
    } finally { Pop-Location }
    Test-Client 'Claude Code' $log '2026-07-28'
}

if (-not $SkipCodex) {
    $log = Join-Path $work 'codex-frames.log'
    $argsToml = '[' + ((@('-I', $tap, $log, $Sqlharness, 'mcp', 'serve', $Profile) | ForEach-Object { '"' + ($_ -replace '\\', '/') + '"' }) -join ',') + ']'
    Push-Location $work
    try {
        codex exec --skip-git-repo-check `
            -c ('mcp_servers.acceptance.command="' + ($python -replace '\\', '/') + '"') `
            -c ('mcp_servers.acceptance.args=' + $argsToml) `
            -c 'mcp_servers.acceptance.default_tools_approval_mode="approve"' `
            $prompt | Out-Null
    } finally { Pop-Location }
    Test-Client 'Codex' $log '2025-06-18'
}

$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Frames (local only, contain tool results): $work"
if ($results | Where-Object { -not $_.Pass }) { exit 1 }
exit 0
```

The exact `_meta` key under which a `2026-07-28` client sends its protocol version on `tools/call` is `io.modelcontextprotocol/protocolVersion` per the SDK documentation; if the first run shows it elsewhere (for example only on `server/discover`), read it from the discover request's `params._meta` instead and keep the check.

- [ ] **Step 3: Run it once against a local publish**

Run: `dotnet publish ./src/SqlHarness.Cli -c Release -p:PublishTrimmed=false -o ./artifacts/task7-local`, then `pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness ./artifacts/task7-local/sqlharness.exe`.
Expected: all rows `Pass = True`; exit 0. Paste the table into the PR description. If Claude Code or Codex is not logged in, run with `-SkipClaude` / `-SkipCodex` and say so in the PR.

- [ ] **Step 4: Commit**

```bash
git add scripts/mcp-client-acceptance.ps1 scripts/mcp-client-acceptance-tap.py
git commit -m "test(mcp): add manual real-client acceptance script"
```

---

### Task 8: Documentation and gates

**Files:**
- Modify: `docs/mcp.md` ("Versions", "Stdout and stderr")
- Modify: `AGENTS.md` (MCP server section, last bullet)

- [ ] **Step 1: Edit `docs/mcp.md`**

Replace the "Versions" body with:

```markdown
- MCP SDK: `ModelContextProtocol` 2.2.0 (`ModelContextProtocol.Core` 2.2.0), pinned in `Directory.Packages.props`.
- Offered protocol revisions: `2025-06-18`, `2025-11-25`, `2026-07-28`. The server negotiates: an `initialize` for `2025-06-18` or `2025-11-25` gets that revision; an `initialize` for an older revision gets `2025-11-25`; a `2026-07-28` client uses `server/discover` and per-request `_meta`.
- `sqlharness_capabilities` reports the revision negotiated for the current request and `supportedProtocolVersions`.
- Real-client check before release: `pwsh ./scripts/mcp-client-acceptance.ps1` (manual; needs logged-in `claude` and `codex`).
```

In "Stdout and stderr", add: "Stderr carries only SDK warnings and errors (category, level and numeric event id; never content)."

- [ ] **Step 2: Edit `AGENTS.md`**

In the MCP server section, replace "Tested SDK `ModelContextProtocol` 2.2.0 with protocol revision `2025-11-25` only." with:

```markdown
Tested SDK `ModelContextProtocol` 2.2.0 with protocol revisions `2025-06-18`, `2025-11-25` and `2026-07-28` (negotiated; older `initialize` requests get `2025-11-25`); stderr carries SDK warnings and errors only.
```

- [ ] **Step 3: Run the gates, one after the other**

Run: `pwsh ./scripts/verify.ps1`
Expected: all stages green.

Run: `pwsh ./scripts/verify-linux.ps1`
Expected: all stages green. If it fails to sync from this worktree, stop and ask the user before applying any workaround (known limitation of the Linux gate from a worktree).

- [ ] **Step 4: Commit**

```bash
git add docs/mcp.md AGENTS.md
git commit -m "docs: describe negotiated MCP revisions and quieter stderr"
```
