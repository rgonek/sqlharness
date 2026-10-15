using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Dashboard.Transcripts;

internal sealed record ClaudeLine(string ConversationId, string? AiTitle, string? Model, string? Effort, IReadOnlyList<string> SqlHarnessToolUseIds);

internal sealed record CodexIndexEntry(string Id, string? Title);

/// <summary>
/// Allowlist parsers for one transcript line. Only ids, titles, model and effort leave this class;
/// prompts, responses and tool inputs/results are never copied.
/// </summary>
internal static class TranscriptLines
{
    internal const int MaximumLength = 256;
    private static readonly byte[] AiTitleMarker = "\"ai-title\""u8.ToArray();
    private static readonly byte[] SqlHarnessToolMarker = "__sqlharness_"u8.ToArray();

    internal static ClaudeLine? ParseClaude(ReadOnlySpan<byte> line)
    {
        var isTitle = line.IndexOf(AiTitleMarker) >= 0;
        if (!isTitle && line.IndexOf(SqlHarnessToolMarker) < 0)
            return null;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "sessionId") is not { } conversation)
                return null;
            var type = Text(root, "type");
            if (type == "ai-title")
                return Text(root, "aiTitle") is { } title ? new ClaudeLine(conversation, title, null, null, []) : null;
            if (type != "assistant" || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return null;

            var ids = new List<string>();
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object && Text(block, "type") == "tool_use"
                        && Text(block, "name") is { } name && IsSqlHarnessTool(name) && Text(block, "id") is { } id)
                        ids.Add(id);
                }
            }
            return ids.Count == 0 ? null : new ClaudeLine(conversation, null, Text(message, "model"), Text(root, "effort"), ids);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static CodexIndexEntry? ParseCodexIndex(ReadOnlySpan<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "id") is { } id ? new CodexIndexEntry(id, Text(root, "thread_name")) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>MCP tools reach Claude as mcp__&lt;server&gt;__&lt;tool&gt;; SQLHarness tools are named sqlharness_*.</summary>
    private static bool IsSqlHarnessTool(string name) =>
        name.StartsWith("mcp__", StringComparison.Ordinal) && name.Contains("__sqlharness_", StringComparison.Ordinal);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? SafeTruncation.Cap(text, MaximumLength)
            : null;
}