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
    /// explicit streams, log writer, and profile loader.
    /// </summary>
    public static async Task<int> RunAsync(
        McpServerOptions options,
        Stream input,
        Stream output,
        TextWriter log,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(loadProfiles);

        McpScope scope;
        try
        {
            scope = McpScope.Create(options, loadProfiles);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Pre-handshake failure: stderr only, generic text, exit 2.
            // The inner chain may hold var values or paths; never print it.
            log.WriteLine("sqlharness-mcp: invalid MCP startup configuration.");
            return (int)SqlHarnessExitCode.Safety;
        }

        // Eager shared composition over the frozen provider. This opens no
        // database connection and performs no auth; the explicit T3 tool
        // catalog executes on it. The T5 execution gate is process-wide: one
        // active database operation, shared by every tool call in process.
        var module = scope.CreateModule();
        var gate = new McpExecutionGate();

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
        Tools.McpToolCatalog.Wire(serverOptions, scope, module, gate, hostShutdown: lifetime.Token);

        try
        {
            // Tools come only from the explicit catalog wired above.
            await using var server = ModelContextProtocol.Server.McpServer.Create(
                new ModelContextProtocol.Server.StreamServerTransport(eofInput, output, ServerName, loggerFactory),
                serverOptions,
                loggerFactory,
                serviceProvider: null);
            await server.RunAsync(lifetime.Token);
            return (int)SqlHarnessExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return (int)SqlHarnessExitCode.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.WriteLine("sqlharness-mcp: the MCP server stopped unexpectedly.");
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

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // SDK-side diagnostics only. Tool results never flow through logging, so
                // no SQL, parameters, or connection details can reach this
                // logger; only the rendered message template is written.
                writer.WriteLine($"sqlharness-mcp[{category}]: {formatter(state, exception)}");
            }
        }
    }
}
