using System.Text.Json.Nodes;

using SqlHarness.Core;

namespace SqlHarness.Mcp.Tests;

public sealed class McpCallMetadataTests
{
    [Fact]
    public void Claude_code_meta_yields_the_tool_use_id_only()
    {
        var meta = JsonNode.Parse("""{"claudecode/toolUseId":"toolu_0145","progressToken":2}""")!.AsObject();

        Assert.Equal(new AgentCallMetadata("toolu_0145", null, null, null, null, null, null), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Codex_meta_yields_the_allowlisted_turn_fields()
    {
        var meta = JsonNode.Parse("""
            {"callId":"exec-1","threadId":"t","sessionId":"s","windowId":"s:0","itemId":"ctc_1","progressToken":1,
             "x-codex-turn-metadata":{"session_id":"s","thread_id":"t","reasoning_effort":"low","turn_id":"turn-1",
               "model":"gpt-6-sol","thread_source":"user","turn_trigger":"exec","sandbox":"none",
               "sandbox_mode":"danger-full-access","auto_review_enabled":false,"turn_started_at_unix_ms":1791617819497,
               "codex_version":"0.160.0"}}
            """)!.AsObject();

        Assert.Equal(new AgentCallMetadata("exec-1", "gpt-6-sol", "low", "s", "turn-1", "exec", "user"), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Wrong_types_empty_strings_and_missing_keys_are_null()
    {
        var meta = JsonNode.Parse("""
            {"callId":"","x-codex-turn-metadata":{"model":42,"reasoning_effort":null,"turn_id":["x"],"session_id":"s"}}
            """)!.AsObject();

        Assert.Equal(new AgentCallMetadata(null, null, null, "s", null, null, null), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Turn_metadata_of_the_wrong_shape_keeps_the_call_id()
    {
        var meta = JsonNode.Parse("""{"callId":"exec-1","x-codex-turn-metadata":"not an object"}""")!.AsObject();

        Assert.Equal(new AgentCallMetadata("exec-1", null, null, null, null, null, null), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Opencode_meta_yields_its_session_id()
    {
        var meta = JsonNode.Parse("""{"ai.opencode/sessionID":"ses_123","progressToken":1}""")!.AsObject();

        Assert.Equal(new AgentCallMetadata(null, null, null, "ses_123", null, null, null), McpCallMetadata.Read(meta));
    }

    [Fact]
    public void Nothing_allowlisted_or_no_meta_is_null()
    {
        Assert.Null(McpCallMetadata.Read(null));
        Assert.Null(McpCallMetadata.Read(JsonNode.Parse("""{"progressToken":3,"other":"x"}""")!.AsObject()));
    }

    [Fact]
    public void Long_values_are_cut()
    {
        var meta = new JsonObject { ["claudecode/toolUseId"] = new string('a', 1000) };

        Assert.Equal(McpCallMetadata.MaximumLength, McpCallMetadata.Read(meta)!.CallId!.Length);
    }

    [Fact]
    public void Registry_returns_metadata_while_the_call_is_tracked()
    {
        var metadata = new AgentCallMetadata("exec-1", "m", null, null, null, null, null);
        using var linked = new CancellationTokenSource();
        using (McpCallRegistry.Track(linked, CancellationToken.None, CancellationToken.None, metadata))
            Assert.Same(metadata, McpCallRegistry.Metadata(linked.Token));
        Assert.Null(McpCallRegistry.Metadata(linked.Token));
    }
}