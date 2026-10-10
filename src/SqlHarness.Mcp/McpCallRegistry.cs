using System.Collections.Concurrent;

namespace SqlHarness.Mcp;

/// <summary>
/// Maps the token an MCP run site hands to the module back to why it was cancelled.
/// Keyed by token, not by "the current call": target-free tools take no gate and
/// can run beside a database call.
/// </summary>
internal static class McpCallRegistry
{
    internal const string Shutdown = "shutdown";
    internal const string Client = "client";
    internal const string Deadline = "deadline";

    private static readonly ConcurrentDictionary<CancellationToken, (CancellationToken Request, CancellationToken Shutdown)> Calls = new();

    internal static IDisposable Track(CancellationTokenSource linked, CancellationToken request, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(linked);
        var token = linked.Token;
        Calls[token] = (request, shutdown);
        return new Registration(token);
    }

    internal static string? Classify(CancellationToken moduleToken)
    {
        if (!moduleToken.IsCancellationRequested || !Calls.TryGetValue(moduleToken, out var call))
            return null;
        if (call.Shutdown.IsCancellationRequested)
            return Shutdown;
        return call.Request.IsCancellationRequested ? Client : Deadline;
    }

    private sealed class Registration(CancellationToken token) : IDisposable
    {
        public void Dispose() => Calls.TryRemove(token, out _);
    }
}
