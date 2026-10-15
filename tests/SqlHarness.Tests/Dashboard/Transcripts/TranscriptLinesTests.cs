using System.Text;
using System.Text.Json;

using SqlHarness.Dashboard.Transcripts;

namespace SqlHarness.Tests.Dashboard.Transcripts;

public sealed class TranscriptLinesTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Assistant_line_yields_only_sqlharness_tool_uses_with_model_and_effort()
    {
        var line = ClaudeFixtures.Assistant("conv-1", "claude-opus-5-5", "medium",
            ("toolu_1", "mcp__bsi-sqlharness-uat__sqlharness_query"), ("toolu_2", "Bash"), ("toolu_3", "mcp__sqlharness__sqlharness_compare"));

        var parsed = TranscriptLines.ParseClaude(B(line))!;

        Assert.Equal(("conv-1", "claude-opus-5-5", "medium"), (parsed.ConversationId, parsed.Model, parsed.Effort));
        Assert.Equal(["toolu_1", "toolu_3"], parsed.SqlHarnessToolUseIds);
        Assert.DoesNotContain("secret prompt", parsed.ToString());
    }

    [Fact]
    public void Assistant_line_without_effort_still_yields_model_and_tool_use()
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = "conv-2",
            message = new { model = "claude-sonnet", role = "assistant", content = new[] { new { type = "tool_use", id = "toolu_4", name = "mcp__sqlharness__sqlharness_query" } } },
        });

        var parsed = TranscriptLines.ParseClaude(B(line)!);

        Assert.Equal(("conv-2", "claude-sonnet", null), (parsed!.ConversationId, parsed.Model, parsed.Effort));
        Assert.Equal(["toolu_4"], parsed.SqlHarnessToolUseIds);
    }

    [Fact]
    public void Ai_title_line_yields_the_title()
    {
        var parsed = TranscriptLines.ParseClaude(B("""{"type":"ai-title","aiTitle":"Optimize orders query","sessionId":"conv-1"}"""))!;
        Assert.Equal(("conv-1", "Optimize orders query"), (parsed.ConversationId, parsed.AiTitle));
    }

    [Theory]
    [InlineData("""{"type":"user","sessionId":"c","message":{"content":"secret prompt"}}""")]
    [InlineData("""{"type":"assistant","sessionId":"c","message":{"model":"m","content":[{"type":"text","text":"hi"}]}}""")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"mcp__s__sqlharness_query"}]}}""")]
    [InlineData("""{"type":"ai-title","aiTitle":42,"sessionId":"c"}""")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Irrelevant_or_malformed_lines_yield_null(string line) => Assert.Null(TranscriptLines.ParseClaude(B(line)));

    [Fact]
    public void Values_are_capped()
    {
        var parsed = TranscriptLines.ParseClaude(B($$"""{"type":"ai-title","aiTitle":"{{new string('a', 1000)}}","sessionId":"c"}"""))!;
        Assert.Equal(256, parsed.AiTitle!.Length);
    }

    [Fact]
    public void Codex_index_entry()
    {
        Assert.Equal(new CodexIndexEntry("019a", "Fix orders"), TranscriptLines.ParseCodexIndex(B("""{"id":"019a","thread_name":"Fix orders","updated_at":"2026-10-14T10:00:00Z"}""")));
        Assert.Null(TranscriptLines.ParseCodexIndex(B("""{"thread_name":"x"}""")));
    }
}

internal static class ClaudeFixtures
{
    internal static string Assistant(string conversation, string model, string? effort, params (string Id, string Name)[] tools)
    {
        var blocks = new List<object> { new { type = "text", text = "secret prompt" } };
        blocks.AddRange(tools.Select(t => (object)new { type = "tool_use", id = t.Id, name = t.Name, input = new { sql = "SELECT secret" } }));
        return JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = conversation,
            effort,
            cwd = "/work",
            message = new { model, role = "assistant", content = blocks },
        });
    }

    internal static string Title(string conversation, string title) =>
        JsonSerializer.Serialize(new { type = "ai-title", aiTitle = title, sessionId = conversation });
}