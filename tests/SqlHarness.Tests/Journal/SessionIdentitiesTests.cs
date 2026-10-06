using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class SessionIdentitiesTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeProcesses(int current, params ProcessSnapshot[] processes) : IProcessInfo
    {
        private readonly Dictionary<int, ProcessSnapshot> _byPid = processes.ToDictionary(p => p.Pid);
        public int CurrentPid => current;
        public ProcessSnapshot? Get(int pid) => _byPid.GetValueOrDefault(pid);
    }

    private static ProcessSnapshot P(int pid, int? parent, string name, string? cmd = null) =>
        new(pid, parent, name, T.AddMinutes(pid), cmd);

    [Fact]
    public void Cli_finds_claude_ancestor_through_shells()
    {
        var processes = new FakeProcesses(30,
            P(30, 20, "sqlharness.exe"), P(20, 10, "pwsh.exe"), P(10, 1, "claude.exe"), P(1, null, "explorer.exe"));

        var identity = SessionIdentities.Cli(processes);

        Assert.Equal("claude", identity.AgentKind);
        Assert.Equal("process-tree", identity.Source);
        Assert.Equal(JournalTransport.Cli, identity.Transport);
        Assert.Equal(10, identity.AgentPid);
        Assert.Equal(30, identity.HostPid);
        Assert.StartsWith("cli:", identity.SessionKey);
    }

    [Fact]
    public void Two_cli_processes_under_one_agent_share_a_session_key()
    {
        var first = SessionIdentities.Cli(new FakeProcesses(30, P(30, 20, "sqlharness"), P(20, 10, "bash"), P(10, 1, "codex")));
        var second = SessionIdentities.Cli(new FakeProcesses(31, P(31, 21, "sqlharness"), P(21, 10, "bash"), P(10, 1, "codex")));

        Assert.Equal("codex", first.AgentKind);
        Assert.Equal(first.SessionKey, second.SessionKey);
    }

    [Fact]
    public void Same_agent_pid_with_different_start_time_is_a_different_session()
    {
        var first = SessionIdentities.Cli(new FakeProcesses(30, P(30, 10, "sqlharness"), P(10, null, "claude")));
        var reused = SessionIdentities.Cli(new FakeProcesses(30, P(30, 10, "sqlharness"),
            new ProcessSnapshot(10, null, "claude", T.AddDays(1), null)));

        Assert.NotEqual(first.SessionKey, reused.SessionKey);
    }

    [Theory]
    [InlineData("node", "/usr/lib/node_modules/@anthropic-ai/claude-code/cli.js", "claude")]
    [InlineData("node.exe", "node C:\\npm\\node_modules\\@openai\\codex\\bin\\codex.js", "codex")]
    [InlineData("node", "/srv/app/server.js", null)]
    [InlineData("Claude.exe", null, "claude")]
    [InlineData("codex", null, "codex")]
    [InlineData("pwsh", null, null)]
    public void Classify_recognizes_native_and_node_hosted_agents(string name, string? cmd, string? expected) =>
        Assert.Equal(expected, SessionIdentities.Classify(new ProcessSnapshot(1, null, name, T, cmd)));

    [Fact]
    public void No_agent_yields_unknown_keyed_by_parent()
    {
        var a = SessionIdentities.Cli(new FakeProcesses(30, P(30, 20, "sqlharness"), P(20, null, "bash")));
        var b = SessionIdentities.Cli(new FakeProcesses(31, P(31, 20, "sqlharness"), P(20, null, "bash")));

        Assert.Equal("unknown", a.AgentKind);
        Assert.Equal("unknown", a.Source);
        Assert.Null(a.AgentPid);
        Assert.Equal(a.SessionKey, b.SessionKey);
    }

    [Fact]
    public void Missing_parent_yields_unknown_keyed_by_self()
    {
        var identity = SessionIdentities.Cli(new FakeProcesses(30, P(30, 999, "sqlharness")));

        Assert.Equal("unknown", identity.AgentKind);
        Assert.StartsWith("cli:", identity.SessionKey);
    }

    [Fact]
    public void Cycle_in_parent_chain_terminates_as_unknown()
    {
        var identity = SessionIdentities.Cli(new FakeProcesses(30, P(30, 20, "sqlharness"), P(20, 21, "a"), P(21, 20, "b")));

        Assert.Equal("unknown", identity.AgentKind);
    }

    [Fact]
    public void Throwing_process_info_yields_unknown()
    {
        var identity = SessionIdentities.Cli(new ThrowingProcesses());

        Assert.Equal("unknown", identity.AgentKind);
        Assert.Equal(Environment.ProcessId, identity.HostPid);
    }

    private sealed class ThrowingProcesses : IProcessInfo
    {
        public int CurrentPid => Environment.ProcessId;
        public ProcessSnapshot? Get(int pid) => throw new UnauthorizedAccessException();
    }

    [Theory]
    [InlineData("claude-code", "claude")]
    [InlineData("Claude Desktop", "claude")]
    [InlineData("codex-mcp-client", "codex")]
    [InlineData("sqlharness-mcp-tests", "other")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void Client_names_map_to_agent_kinds(string? clientName, string expected) =>
        Assert.Equal(expected, SessionIdentities.AgentKindFromClientName(clientName));

    [Fact]
    public void Mcp_identity_uses_client_info_and_records_agent_ancestor()
    {
        var processes = new FakeProcesses(30, P(30, 10, "sqlharness"), P(10, null, "claude"));

        var identity = SessionIdentities.Mcp(processes, "mcp:abc", "claude-code", "2.1.0", "fixed");

        Assert.Equal("mcp:abc", identity.SessionKey);
        Assert.Equal("claude", identity.AgentKind);
        Assert.Equal("mcp-clientinfo", identity.Source);
        Assert.Equal(JournalTransport.Mcp, identity.Transport);
        Assert.Equal("claude-code", identity.ClientName);
        Assert.Equal("fixed", identity.McpMode);
        Assert.Equal(10, identity.AgentPid);
    }

    [Fact]
    public void Mcp_identity_without_client_info_falls_back_to_process_tree_kind()
    {
        var processes = new FakeProcesses(30, P(30, 10, "sqlharness"), P(10, null, "codex"));

        var identity = SessionIdentities.Mcp(processes, "mcp:abc", null, null, "request");

        Assert.Equal("codex", identity.AgentKind);
        Assert.Equal("process-tree", identity.Source);
    }
}