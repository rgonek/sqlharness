using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlHarness.Core;

/// <summary>
/// Implicit session identity: MCP clientInfo plus a per-process key, or for the
/// CLI the nearest known agent ancestor. Agents send nothing and are told nothing.
/// </summary>
public static class SessionIdentities
{
    internal const int MaximumDepth = 16;

    public static SessionIdentity Cli(IProcessInfo processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var walk = Walk(processes);
        string key;
        if (walk.Agent is { } agent)
            key = Key("cli", walk.AgentKind!, agent.Pid, agent.StartedAt);
        else if (walk.Parent is { } parent)
            key = Key("cli", "unknown", parent.Pid, parent.StartedAt);
        else
            key = Key("cli", "unknown", processes.CurrentPid, walk.Self?.StartedAt);

        return new SessionIdentity(
            key,
            walk.AgentKind ?? "unknown",
            walk.AgentKind is null ? "unknown" : "process-tree",
            JournalTransport.Cli,
            null,
            null,
            null,
            walk.Agent?.Pid,
            walk.Agent?.StartedAt,
            processes.CurrentPid,
            walk.Self?.StartedAt,
            CurrentDirectory());
    }

    public static SessionIdentity Mcp(IProcessInfo processes, string sessionKey, string? clientName, string? clientVersion, string mcpMode)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var walk = Walk(processes);
        var fromClient = AgentKindFromClientName(clientName);
        var useClient = fromClient != "unknown";
        return new SessionIdentity(
            sessionKey,
            useClient ? fromClient : walk.AgentKind ?? "unknown",
            useClient ? "mcp-clientinfo" : walk.AgentKind is null ? "unknown" : "process-tree",
            JournalTransport.Mcp,
            string.IsNullOrWhiteSpace(clientName) ? null : clientName,
            string.IsNullOrWhiteSpace(clientVersion) ? null : clientVersion,
            mcpMode,
            walk.Agent?.Pid,
            walk.Agent?.StartedAt,
            processes.CurrentPid,
            walk.Self?.StartedAt,
            CurrentDirectory());
    }

    public static string AgentKindFromClientName(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
            return "unknown";
        if (clientName.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return "claude";
        if (clientName.Contains("codex", StringComparison.OrdinalIgnoreCase))
            return "codex";
        return "other";
    }

    internal static string? Classify(ProcessSnapshot process)
    {
        var name = Path.GetFileNameWithoutExtension(process.Name).ToLowerInvariant();
        switch (name)
        {
            case "claude":
                return "claude";
            case "codex":
                return "codex";
            case "node" or "bun" when process.CommandLine is { } commandLine:
                var normalized = commandLine.Replace('\\', '/');
                if (normalized.Contains("@anthropic-ai/claude-code", StringComparison.OrdinalIgnoreCase))
                    return "claude";
                if (normalized.Contains("@openai/codex", StringComparison.OrdinalIgnoreCase))
                    return "codex";
                return null;
            default:
                return null;
        }
    }

    private static Walked Walk(IProcessInfo processes)
    {
        ProcessSnapshot? self = null;
        ProcessSnapshot? parent = null;
        try
        {
            self = processes.Get(processes.CurrentPid);
            var visited = new HashSet<int> { processes.CurrentPid };
            var next = self?.ParentPid;
            for (var depth = 0; depth < MaximumDepth && next is { } pid && visited.Add(pid); depth++)
            {
                var current = processes.Get(pid);
                if (current is null)
                    break;
                parent ??= current;
                if (Classify(current) is { } kind)
                    return new Walked(self, parent, current, kind);
                next = current.ParentPid;
            }
        }
        catch (Exception)
        {
            // Unreadable process tree: identity degrades to unknown, never fails the operation.
        }

        return new Walked(self, parent, null, null);
    }

    private static string Key(string prefix, string kind, int pid, DateTimeOffset? started)
    {
        var material = string.Create(CultureInfo.InvariantCulture, $"{kind}|{pid}|{started?.ToUnixTimeMilliseconds()}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return prefix + ":" + hash[..32];
    }

    private static string? CurrentDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Walked(ProcessSnapshot? Self, ProcessSnapshot? Parent, ProcessSnapshot? Agent, string? AgentKind);
}