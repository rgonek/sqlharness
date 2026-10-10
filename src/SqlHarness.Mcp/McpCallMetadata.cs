using System.Text.Json.Nodes;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

/// <summary>
/// Allowlist over tools/call _meta. Claude Code sends claudecode/toolUseId; Codex sends
/// callId and an x-codex-turn-metadata object. Only string values are read, each capped;
/// every other key (sandbox mode, feature flags, window and item ids) is ignored.
/// </summary>
internal static class McpCallMetadata
{
    internal const int MaximumLength = 256;

    private static readonly AgentCallMetadata Empty = new(null, null, null, null, null, null, null);

    internal static AgentCallMetadata? Read(JsonObject? meta)
    {
        if (meta is null)
            return null;
        try
        {
            var turn = meta.TryGetPropertyValue("x-codex-turn-metadata", out var node) ? node as JsonObject : null;
            var result = new AgentCallMetadata(
                Text(meta, "claudecode/toolUseId") ?? Text(meta, "callId"),
                Text(turn, "model"),
                Text(turn, "reasoning_effort"),
                Text(turn, "session_id") ?? Text(meta, "ai.opencode/sessionID"),
                Text(turn, "turn_id"),
                Text(turn, "turn_trigger"),
                Text(turn, "thread_source"));
            return result == Empty ? null : result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Text(JsonObject? source, string key)
    {
        if (source is null || !source.TryGetPropertyValue(key, out var node) || node is not JsonValue value
            || !value.TryGetValue<string>(out var text) || text.Length == 0)
            return null;
        return text.Length <= MaximumLength ? text : text[..MaximumLength];
    }
}