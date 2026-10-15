using System.Text.RegularExpressions;

namespace SqlHarness.Dashboard.Transcripts;

/// <summary>Where Claude Code and Codex keep their transcripts; only read when the setting is on.</summary>
internal sealed partial record AgentHomes(string ClaudeHome, string CodexHome)
{
    internal static AgentHomes FromEnvironment(Func<string, string?> env, string userHome) => new(
        NonEmpty(env("CLAUDE_CONFIG_DIR")) ?? Path.Combine(userHome, ".claude"),
        NonEmpty(env("CODEX_HOME")) ?? Path.Combine(userHome, ".codex"));

    internal static AgentHomes Current() =>
        FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Claude Code names a project directory after the cwd with every non-alphanumeric character replaced by '-'.</summary>
    internal string ClaudeProjectDirectory(string cwd) => Path.Combine(ClaudeHome, "projects", NonAlphanumeric().Replace(cwd, "-"));

    internal string CodexSessionIndex => Path.Combine(CodexHome, "session_index.jsonl");

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}