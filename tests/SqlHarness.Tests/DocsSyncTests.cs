// Docs sync guard (plan 029).
//
// Example marker convention: an "executable example" in README.md or skills/sqlharness/SKILL.md is
// a fenced code block whose nearest preceding non-blank line is the HTML comment
//     <!-- docs-sync:example -->
// Every logical line of such a block that starts with `sqlharness ` (PowerShell backtick and POSIX
// backslash continuations are joined) is parsed offline through SqlHarnessCli with a stub module and
// an empty temporary SQLHARNESS_HOME. A line passes when strict parsing accepts the command and
// options: either the stub module was reached (dispatch proves the parse succeeded; the stub never
// connects and its refusal is not drift), or the command ended without exit -1 or
// "Invalid command line arguments." before dispatch (for example on a missing input file).
// `<placeholder>` tokens are replaced by a fixture value. `mcp` and `dashboard` lines are not run
// (they would start servers); their options are checked against that command's real --help text.
// Untagged fences and prose are never checked.
//
// Command coverage: every command registered with Spectre must appear in capabilities, and in the
// README "## Commands" table; the skill must mention each command in backticks or link to that
// table (README.md#commands).

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed partial class DocsSyncTests
{
    private const string Marker = "<!-- docs-sync:example -->";
    private static readonly string[] ServerCommands = ["mcp", "dashboard"];
    // Operator-only commands are documented in README, not advertised to agents: capabilities omits them.
    private static readonly string[] OperatorOnlyCommands = ["dashboard"];
    private static readonly Dictionary<string, (int, string)> HelpCache = new(StringComparer.Ordinal);

    [Fact]
    public async Task Capabilities_lists_every_registered_command()
    {
        var registered = await RegisteredCommandsAsync();
        var capabilities = SqlHarnessCapabilitiesProvider.Get().Commands.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray();

        // Operator-only commands are documented in README, not advertised to agents.
        var advertised = registered.Where(c => !OperatorOnlyCommands.Contains(c, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(advertised, capabilities);
    }

    [Fact]
    public async Task Readme_commands_table_lists_every_registered_command()
    {
        var registered = await RegisteredCommandsAsync();
        var table = ReadmeCommandTable(ReadRepositoryFile("README.md"));

        var missing = registered.Where(c => !table.Contains(c)).ToArray();
        Assert.True(missing.Length == 0, "README '## Commands' table is missing: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task Skill_lists_every_registered_command_or_links_the_readme_table()
    {
        var registered = await RegisteredCommandsAsync();
        var skill = ReadRepositoryFile("skills", "sqlharness", "SKILL.md");

        if (skill.Contains("README.md#commands", StringComparison.Ordinal))
        {
            return;
        }

        var missing = registered.Where(c => !Regex.IsMatch(skill, $@"`{Regex.Escape(c)}[` ]")).ToArray();
        Assert.True(missing.Length == 0,
            "SKILL.md neither links README.md#commands nor mentions in backticks: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("skills/sqlharness/SKILL.md")]
    public async Task Documented_examples_match_command_contract(string file)
    {
        var examples = ExtractExamples(ReadRepositoryFile(file.Split('/')));
        Assert.True(examples.Count > 0, $"{file} has no examples tagged with {Marker}; tag the runnable sqlharness examples.");

        var failures = await CheckExamplesAsync(examples);
        Assert.True(failures.Count == 0, $"{file} examples drifted from the CLI:{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Drift_detector_flags_a_real_command_with_an_invalid_option()
    {
        var bad = $"{Marker}\n```powershell\nsqlharness query prod-eu --file .\\q.sql --no-such-option 1 --json\n```\n";
        var failures = await CheckExamplesAsync(ExtractExamples(bad));
        Assert.Single(failures);

        var badDispatching = $"{Marker}\n```powershell\nsqlharness ping prod-eu --no-such-option --json\n```\n";
        Assert.Single(await CheckExamplesAsync(ExtractExamples(badDispatching)));

        var badServer = $"{Marker}\n```\nsqlharness mcp serve --no-such-option\n```\n";
        Assert.Single(await CheckExamplesAsync(ExtractExamples(badServer)));

        var good = $"{Marker}\n```powershell\nsqlharness capabilities --json\nsqlharness query <profile> --var a=b `\n  --file .\\q.sql --param id:int=1 --json\nsqlharness ping prod-eu --var a=b --json\n```\n";
        Assert.Empty(await CheckExamplesAsync(ExtractExamples(good)));

        var untagged = "```\nsqlharness query --no-such-option\n```\n";
        Assert.Empty(ExtractExamples(untagged));
    }

    [Fact]
    public async Task Unsupported_output_combinations_reject_without_dispatch()
    {
        var caps = SqlHarnessCapabilitiesProvider.Get();
        using var home = new TempHome();
        var failures = new List<string>();

        // Keys without "text" need one of their flags: a bare invocation must reject (exit 2).
        foreach (var (command, modeList) in caps.OutputModesByCommand)
        {
            var key = string.Join('|', modeList);
            if (modeList.Contains("text", StringComparer.Ordinal))
            {
                continue;
            }

            {
                var (exit, _, module) = await RunAsync(command == "artifact" ? ["artifact", "some-id", "--section", "summary"] : [command]);
                if (exit != 2 || module.Calls != 0)
                {
                    failures.Add($"{command} (modes {key}) bare run: exit {exit}, dispatches {module.Calls}");
                }
            }
        }

        // No command accepts two of --json, --json-summary and --output together.
        foreach (var command in caps.Commands.Select(c => c.Name).Except(ServerCommands))
        {
            foreach (var extra in new[] { new[] { "--json-summary" }, ["--output", "agent"] })
            {
                var (exit, _, module) = await RunAsync([command, "--json", .. extra]);
                if (exit == 0 || module.Calls != 0)
                {
                    failures.Add($"{command} --json {string.Join(' ', extra)}: exit {exit}, dispatches {module.Calls}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---- example extraction and checking ----

    private static List<string> ExtractExamples(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var commands = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != Marker)
            {
                continue;
            }

            var j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length == 0)
            {
                j++;
            }

            if (j >= lines.Length || !lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var logical = new StringBuilder();
            for (j++; j < lines.Length && !lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal); j++)
            {
                var line = lines[j].TrimEnd();
                var continued = line.EndsWith('`') || line.EndsWith('\\');
                logical.Append(continued ? line[..^1] : line).Append(' ');
                if (!continued)
                {
                    Flush(logical, commands);
                }
            }

            Flush(logical, commands);
            i = j;
        }

        return commands;

        static void Flush(StringBuilder logical, List<string> commands)
        {
            var text = logical.ToString().Trim();
            logical.Clear();
            if (text.StartsWith("sqlharness ", StringComparison.Ordinal))
            {
                commands.Add(text);
            }
        }
    }

    private static async Task<List<string>> CheckExamplesAsync(IReadOnlyList<string> examples)
    {
        using var home = new TempHome();
        var failures = new List<string>();
        foreach (var example in examples)
        {
            var args = Tokenize(example).Skip(1).ToArray();
            if (args.Length == 0)
            {
                failures.Add($"'{example}': no command");
                continue;
            }

            if (ServerCommands.Contains(args[0], StringComparer.Ordinal))
            {
                var helpArgs = args[0] == "mcp" && args.Length > 1 ? new[] { "mcp", args[1], "--help" } : [args[0], "--help"];
                var (_, help) = await HelpAsync(helpArgs);
                var unknown = args.Where(a => a.StartsWith("--", StringComparison.Ordinal))
                    .Select(a => a.Split('=')[0])
                    .Where(a => !help.Contains(a, StringComparison.Ordinal)).ToArray();
                if (unknown.Length > 0 || (args[0] == "mcp" && (args.Length < 2 || args[1] != "serve")))
                {
                    failures.Add($"'{example}': unknown subcommand or option(s) {string.Join(", ", unknown)}");
                }

                continue;
            }

            // A dispatch to the stub proves Spectre accepted the line; the stub's refusal then surfaces
            // through the CLI exception handler as "Invalid command line arguments", which is not drift.
            var (exit, output, module) = await RunAsync(args);
            if (module.Calls == 0 && (exit == -1 || output.Contains("Invalid command line arguments", StringComparison.Ordinal)))
            {
                failures.Add($"'{example}': rejected by the CLI parser (exit {exit}); a command, option or argument no longer exists");
            }
        }

        return failures;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        foreach (Match m in TokenPattern().Matches(line))
        {
            var token = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Value;
            tokens.Add(PlaceholderPattern().Replace(token, "placeholder"));
        }

        return tokens;
    }

    [GeneratedRegex("\"([^\"]*)\"|'([^']*)'|\\S+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("<[A-Za-z][A-Za-z0-9_.-]*>")]
    private static partial Regex PlaceholderPattern();

    private static async Task<(int Exit, string Output, StubModule Module)> RunAsync(params string[] args)
    {
        var module = new StubModule();
        var output = new StringWriter();
        var exit = await SqlHarnessCli.Create(module, output, new StringReader(""), stdinRedirected: true, planStdin: new MemoryStream(), mcpError: new StringWriter())
            .RunAsync(args);
        return (exit, output.ToString(), module);
    }

    // ---- registered commands and files ----

    private static async Task<string[]> RegisteredCommandsAsync()
    {
        var (exit, help) = await HelpAsync("--help");
        Assert.Equal(0, exit);
        var section = help[(help.IndexOf("COMMANDS:", StringComparison.Ordinal) + "COMMANDS:".Length)..];
        // Command rows are indented 4 spaces; wrapped descriptions are indented much deeper.
        var names = RegisteredRowPattern().Matches(section).Select(m => m.Groups[1].Value).ToArray();
        Assert.True(names.Length >= 10, "Could not parse the COMMANDS section of 'sqlharness --help':" + Environment.NewLine + help);
        return names;
    }

    [GeneratedRegex(@"^ {4}([a-z][a-z-]*)(?= {2,}|\s*$)", RegexOptions.Multiline)]
    private static partial Regex RegisteredRowPattern();

    private static HashSet<string> ReadmeCommandTable(string readme)
    {
        var normalized = readme.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = normalized.IndexOf("\n## Commands", StringComparison.Ordinal);
        Assert.True(start >= 0, "README.md has no '## Commands' section.");
        var rest = normalized[(start + 1)..];
        var end = rest.IndexOf("\n## ", 3, StringComparison.Ordinal);
        var section = end < 0 ? rest : rest[..end];
        return TableRowPattern().Matches(section).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\|\s*`([a-z][a-z-]*)[` ]", RegexOptions.Multiline)]
    private static partial Regex TableRowPattern();

    private static async Task<(int Exit, string Output)> HelpAsync(params string[] args)
    {
        var key = string.Join(' ', args);
        lock (HelpCache)
        {
            if (HelpCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "sqlharness.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet", $"\"{cliAssembly}\" {key}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = (process.ExitCode, await stdout + await stderr);
        lock (HelpCache)
        {
            HelpCache[key] = result;
        }

        return result;
    }

    private static string ReadRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate repository file: {string.Join('/', path)}");
    }

    private sealed class StubModule : ISqlHarnessModule
    {
        public int Calls { get; private set; }

        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("DocsSync stub: no database access.");
        }
    }

    private sealed class TempHome : IDisposable
    {
        private readonly string? _original = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
        private readonly string _path = Path.Combine(Path.GetTempPath(), "sqlharness-docssync-" + Guid.NewGuid().ToString("N"));

        public TempHome()
        {
            Directory.CreateDirectory(_path);
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _original);
            try
            {
                Directory.Delete(_path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
