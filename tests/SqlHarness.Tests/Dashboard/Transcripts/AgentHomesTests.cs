using SqlHarness.Dashboard.Transcripts;

namespace SqlHarness.Tests.Dashboard.Transcripts;

public sealed class AgentHomesTests
{
    [Theory]
    [InlineData(@"C:\BSI\Tools\sqlharness", "C--BSI-Tools-sqlharness")]
    [InlineData(@"C:\Users\robert.gonek\.notes", "C--Users-robert-gonek--notes")]
    [InlineData("/home/dev/my_repo", "-home-dev-my-repo")]
    public void Claude_project_directory_replaces_every_non_alphanumeric(string cwd, string expected)
    {
        var homes = new AgentHomes("/claude", "/codex");
        Assert.Equal(Path.Combine("/claude", "projects", expected), homes.ClaudeProjectDirectory(cwd));
    }

    [Fact]
    public void Homes_honour_environment_overrides()
    {
        var homes = AgentHomes.FromEnvironment(name => name switch { "CLAUDE_CONFIG_DIR" => "/c", "CODEX_HOME" => "/x", _ => null }, "/u");
        Assert.Equal(new AgentHomes("/c", "/x"), homes);
        Assert.Equal(new AgentHomes(Path.Combine("/u", ".claude"), Path.Combine("/u", ".codex")), AgentHomes.FromEnvironment(_ => null, "/u"));
    }
}
