using Microsoft.Extensions.Logging;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp;

/// <summary>
/// Local stdio host for one profile-scoped MCP server process (spec
/// section 2): stdout carries only protocol frames, all logging goes to
/// stderr without arguments, SQL, parameter values, or secrets. T2 starts
/// the transport with a frozen scope and clean shutdown; the explicit T3
/// tool catalog registers exactly 11 tools on the frozen scope.
/// </summary>
public static class McpHost
{
    public const string ServerName = "sqlharness-mcp";

    /// <summary>Pinned, tested protocol revision (spec bindings, T1).</summary>
    public const string PinnedProtocolVersion = "2025-11-25";

    public static readonly string ServerVersion =
        typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Serves the frozen startup scope over process stdio.
    /// Returns 0 on clean shutdown (EOF/cancellation), 2 on startup
    /// validation failure, 1 on an unexpected transport failure.
    /// Startup errors go to stderr and never echo values or secrets.
    /// </summary>
    public static Task<int> RunAsync(McpServerOptions options, CancellationToken ct = default) =>
        RunAsync(
            options,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.Error,
            () => ProfileStore.Load(),
            ct);

    /// <summary>
    /// Hosting seam for embedding and tests: the same startup path with
    /// explicit streams, log writer, and profile loader. <paramref name="started"/>
    /// runs once, only after startup validation succeeded and before the
    /// transport starts, with the loaded config; it must not write to stdout,
    /// and an exception from it is swallowed so it never fails the server.
    /// </summary>
    public static async Task<int> RunAsync(
        McpServerOptions options,
        Stream input,
        Stream output,
        TextWriter log,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
        CancellationToken ct = default,
        Action<SqlHarnessConfig>? started = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(loadProfiles);

        // SDK events can arrive from several transport and handler tasks. The
        // caller may supply a StringWriter, whose StringBuilder is not safe for
        // concurrent writes.
        log = TextWriter.Synchronized(log);

        McpProcessContext process;
        try
        {
            process = McpProcessContext.Create(options, loadProfiles);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Pre-handshake failure: stderr only, generic text, exit 2.
            // The inner chain may hold var values or paths; never print it.
            log.WriteLine("sqlharness-mcp: invalid MCP startup configuration.");
            return (int)SqlHarnessExitCode.Safety;
        }

        // Composition reads only the frozen profile snapshot and opens no
        // database connection. The process context owns one gate for every
        // request scope.

        var loggerFactory = new McpStderrLoggerFactory(log);
        var serverOptions = new ModelContextProtocol.Server.McpServerOptions
        {
            ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = ServerName,
                Version = ServerVersion,
            },
            ProtocolVersion = PinnedProtocolVersion,
        };
        // Explicit EOF binding (T5 fix R1): the SDK does not propagate stdin
        // EOF to in-flight handler tokens, so the host watches the
        // transport's own reads and folds EOF into the shutdown token every
        // handler already observes. No thread, no polling.
        using var eofShutdown = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, eofShutdown.Token);
        using var eofInput = new EofShutdownInput(input, eofShutdown);
        using var guardedInput = process.RequestScope ? new McpDuplicateJsonFieldGuardInput(eofInput) : null;
        // Activity journal: content-free stderr diagnostics only; stdout stays protocol-only.
        var config = SqlHarnessConfigLoader.Load();
        if (config.Warning is not null)
            log.WriteLine(config.Warning);
        var journal = new Lazy<IActivityJournal>(
            () => ActivityJournal.Open(config.Config.Journal, log),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var sessionKey = "mcp:" + Guid.NewGuid().ToString("N");
        var mcpMode = process.RequestScope ? "request" : "fixed";
        ModelContextProtocol.Server.McpServer? running = null;
        // One identity per serve process: resolved lazily on the first journaled call
        // (after initialize, so ClientInfo is set) and shared by every per-call decorator.
        var identity = new Lazy<SessionIdentity>(
            () => SessionIdentities.Mcp(ProcessInfo.Current, sessionKey, running?.ClientInfo?.Name, running?.ClientInfo?.Version, mcpMode),
            LazyThreadSafetyMode.ExecutionAndPublication);
        process.DecorateModules(module => new JournalingModule(
            module,
            () => journal.Value,
            () => identity.Value));
        Tools.McpToolCatalog.Wire(serverOptions, process, hostShutdown: lifetime.Token);
        try
        {
            started?.Invoke(config.Config);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Startup side effects (dashboard autostart, retention) are best-effort.
        }

        try
        {
            // Tools come only from the explicit catalog wired above.
            await using var server = ModelContextProtocol.Server.McpServer.Create(
                new ModelContextProtocol.Server.StreamServerTransport((Stream?)guardedInput ?? eofInput, output, ServerName, loggerFactory),
                serverOptions,
                loggerFactory,
                serviceProvider: null);
            running = server;
            await server.RunAsync(lifetime.Token);
            return (int)SqlHarnessExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return (int)SqlHarnessExitCode.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.WriteLine($"sqlharness-mcp: the MCP server stopped unexpectedly ({exception.GetType().Name}).");
            return 1;
        }
    }

    private sealed class McpStderrLoggerFactory(TextWriter writer) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName) => new McpStderrLogger(writer, categoryName);

        public void Dispose() { }

        private sealed class McpStderrLogger(TextWriter writer, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            // Always enabled: the level gate is not the protection. Safety
            // comes from Log never rendering state, exception, or formatter
            // output, so any enabled event is content-free by construction.
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // SDK-side diagnostics only. This logger emits
                // only safe primitives, so no SQL, parameters, or connection details
                // can reach stderr through it: category, level, numeric event id. Never call
                // formatter(state, exception) and never ToString state or
                // exception: the SDK renders tool-call arguments, file paths,
                // and binder exception content into log state, which leaked
                // request content to stderr (001/T1 oracle).
                writer.WriteLine($"sqlharness-mcp[{category}]: {logLevel} (event {eventId.Id})");
            }
        }
    }
}