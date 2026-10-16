using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class GateParityTests
{
    // `dotnet build` restores implicitly, so there is no separate restore verb to keep
    // in step between the gates. `VerbGroup` below still lists it so the extractor can
    // find a restore command if one is ever added back and the expectations catch it.
    private static readonly string[] ExpectedVerbs = ["build", "test", "format"];

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_runs_the_same_three_dotnet_stages_in_the_same_order(string path)
    {
        var verbs = StageVerbs(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))));

        Assert.Equal(ExpectedVerbs, verbs);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_builds_and_formats_identically(string path)
    {
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.Contains("-warnaserror", StageCommand(content, "build"), StringComparison.Ordinal);
        Assert.Contains("--verify-no-changes", StageCommand(content, "format"), StringComparison.Ordinal);
        Assert.Contains("SqlHarness.sln", StageCommand(content, "format"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_runs_build_and_format_exactly_once(string path)
    {
        // StageVerbs is distinct, because a project list legitimately yields `test` twice.
        // That dedupe would otherwise let a second `dotnet build` or `dotnet format` slip in
        // unnoticed, and the two-stage comparison above reads only the first one.
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.Single(StageCommands(content, "build"));
        Assert.Single(StageCommands(content, "format"));
    }

    // CI is in this theory, not only the two local gates. The UI-before-build anchor moved
    // from `dotnet restore` to `dotnet build`, but that changed the anchor, not the scope:
    // when this check only covered the local scripts, adding `--no-restore` to CI's build
    // step or putting a standalone `dotnet restore` back into ci.yml left the suite green.
    // CI's `dotnet build` runs from the repository root and names no solution, which is why
    // this is separate from the format assertions above.
    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_builds_with_warnaserror_and_lets_the_build_restore(string path)
    {
        // `dotnet build` restores implicitly. A gate that passes --no-restore would fail on
        // an unbuilt tree instead of restoring, so none of the three may pass it.
        var command = StageCommand(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))), "build");

        Assert.Contains("-warnaserror", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-restore", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_runs_the_same_non_integration_test_selection(string path)
    {
        // Every `dotnet test` command, not the first one: a gate that runs one command per
        // project can carry the right filter on one project and the wrong one on the other.
        // CI is in this theory so its test steps cannot drift from the local gates' on the
        // selection they run.
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.All(CommandsFor(path, content, "test"), command =>
        {
            Assert.Contains("--no-build", command, StringComparison.Ordinal);
            Assert.Contains("--filter", command, StringComparison.Ordinal);
            Assert.Contains("FullyQualifiedName!~Integration", command, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Every_gate_names_the_same_test_filter_value()
    {
        // One value across all three surfaces and every command in them. A second --filter,
        // or a different selection in one surface, is exactly the drift the per-project
        // comparison cannot see because it compares flags rather than the filter.
        var values = new[] { "scripts/verify.ps1", "scripts/verify-linux.ps1", ".github/workflows/ci.yml" }
            .SelectMany(path => CommandsFor(path, ReadRepositoryFile(path.Split('/')), "test")
                .Select(FilterValueOf))
            .Distinct()
            .ToArray();

        Assert.Equal(["FullyQualifiedName!~Integration"], values);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_runs_integration_tests_filtered(string path)
    {
        // Exactly one --filter, and its whole value is the exclusion. An unquoted value, a
        // value with extra conditions glued on, or a second --filter that overrides the first
        // are all shapes that stop excluding the integration tests, and none of them can be
        // told apart by looking for a quoted substring.
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.All(CommandsFor(path, content, "test"), command =>
        {
            var arguments = ArgumentsOf(command);
            var filters = Enumerable.Range(0, arguments.Length).Where(index => arguments[index] == "--filter").ToArray();

            Assert.Single(filters);
            Assert.True(
                filters[0] + 1 < arguments.Length,
                $"'{command}' passes --filter with no value after it");
            Assert.Equal("FullyQualifiedName!~Integration", arguments[filters[0] + 1]);
        });
    }

    [Fact]
    public void The_two_gates_have_identical_build_commands()
    {
        var windows = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")), "build");
        var linux = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1")), "build");

        Assert.Equal(windows, linux);
    }

    [Fact]
    public void No_gate_runs_a_standalone_restore_stage()
    {
        // dotnet build restores implicitly and performs the same restore, so a separate
        // restore stage is duplicated work. This replaces the deleted identical-restore
        // comparison with a stronger claim than it made, and it covers ci.yml too: the
        // local gates were guarded here while CI's `dotnet restore` step could come back
        // unnoticed.
        foreach (var path in new[] { "scripts/verify.ps1", "scripts/verify-linux.ps1", ".github/workflows/ci.yml" })
        {
            var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

            Assert.DoesNotContain("dotnet restore", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_two_gates_have_identical_format_commands()
    {
        var windows = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1")), "format");
        var linux = StageCommand(File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1")), "format");

        Assert.Equal(FormatShape(linux), FormatShape(windows));
    }

    // CI is in this theory, not only the two local gates. When the two gates are compared to
    // each other and a third surface is left out, that surface drifts silently - which is
    // how CI's build step lost its restore guard once already. Every surface runs the
    // Windows arguments plus its own logger, and nothing else.
    [Theory]
    [InlineData("scripts/verify-linux.ps1", "--logger console;verbosity=normal")]
    [InlineData(".github/workflows/ci.yml", "--logger trx --results-directory TestResults")]
    public void Every_gate_runs_the_windows_test_commands_plus_its_own_logger(string path, string logger)
    {
        // Arguments, not command text: the three surfaces quote the same argument three ways
        // because their shells differ, and comparing the text would compare the shells
        // instead of what they run. Compared as sets, because which project a surface runs
        // first is not a parity property - WHICH projects, and with which arguments, is.
        var expected = TestCommands("scripts/verify.ps1", ReadRepositoryFile("scripts", "verify.ps1"))
            .SelectMany(command => ArgumentsOf(command).Concat(logger.Split(' ')))
            .OrderBy(argument => argument, StringComparer.Ordinal)
            .ToArray();
        var actual = TestCommands(path, ReadRepositoryFile(path.Split('/')))
            .SelectMany(ArgumentsOf)
            .OrderBy(argument => argument, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void The_windows_gate_runs_no_test_logger()
    {
        // The Windows gate already writes each project's output to its own log file, so a
        // console logger on top of it would duplicate the stage's only report. CI's trx
        // logger is asserted by the theory above rather than here: CI has no per-project
        // log to write into, and its logger is what produces the uploaded artifact.
        Assert.All(
            TestCommands("scripts/verify.ps1", ReadRepositoryFile("scripts", "verify.ps1")),
            command => Assert.DoesNotContain("--logger", command, StringComparison.Ordinal));
    }

    [Fact]
    public void The_only_logger_occurrences_are_the_ones_that_surface_asks_for()
    {
        // One logger per test command where a surface asks for one, and none anywhere else:
        // a --logger that appears on build or format, or twice on a test command, is drift
        // the single-value comparisons above cannot see. Every command of every verb is
        // checked, so an extra command cannot carry one either.
        foreach (var verb in ExpectedVerbs)
        {
            foreach (var path in new[] { "scripts/verify.ps1", "scripts/verify-linux.ps1", ".github/workflows/ci.yml" })
            {
                var commands = CommandsFor(path, ReadRepositoryFile(path.Split('/')), verb);
                var wants = verb == "test" && path != "scripts/verify.ps1";

                Assert.All(commands, command =>
                {
                    if (wants) { Assert.Single(Regex.Matches(command, @"--logger")); }
                    else { Assert.DoesNotContain("--logger", command, StringComparison.Ordinal); }
                });
            }
        }
    }

    // Every surface names one `dotnet test` command per test project, and only the projects
    // the solution holds. Naming them explicitly is what makes the stage able to start them
    // all at once, and it is also the failure mode: a third test project added to the
    // solution would simply not run in any gate.
    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_names_every_test_project_in_the_solution(string path)
    {
        var commands = TestCommands(path, ReadRepositoryFile(path.Split('/')));

        Assert.NotEmpty(commands);
        Assert.Equal(
            SolutionTestProjects(),
            commands.Select(TestProjectOf).OrderBy(project => project, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_windows_gate_runs_its_test_projects_through_the_shared_parallel_stage()
    {
        // One Add-Stage over a project list, not one Add-Stage per project: two sequential
        // stages are two sequential runs and report only the first failure. The helper comes
        // from gate-output.ps1, so the parallel stage keeps the same log-per-stage contract as
        // every other stage rather than growing a second implementation in the gate.
        var content = ReadRepositoryFile("scripts", "verify.ps1");

        Assert.Single(Regex.Matches(content, @"Add-Stage -Name 'test'"));
        Assert.Contains("Invoke-ParallelGateStage", content, StringComparison.Ordinal);
        Assert.Contains("Invoke-ParallelGateStage", ReadRepositoryFile("scripts", "gate-output.ps1"), StringComparison.Ordinal);
        Assert.DoesNotMatch(@"dotnet\s+test\s+SqlHarness\.sln", content);
    }

    [Fact]
    public void The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both()
    {
        // A `&` on both commands and a wait on both pids: joining the first before starting
        // the second is the sequential solution wearing this stage's name. Both live in one
        // bash block because two concurrent WSL launches would race each other over the
        // clone, the same reason the UI check is coupled into the build stage.
        var content = ReadRepositoryFile("scripts", "verify-linux.ps1");
        var stage = content.IndexOf("Invoke-LinuxStage -Name 'test'", StringComparison.Ordinal);
        Assert.True(stage > 0, "the Linux gate must run a test stage");

        // Cut at the next stage so the counts cannot be satisfied by another stage's bash.
        var block = content.Substring(stage);
        var next = block.IndexOf("Invoke-LinuxStage -Name", 1);
        if (next > 0) { block = block.Substring(0, next); }

        Assert.Equal(2, Regex.Matches(block, @"'dotnet test [^']+ &',").Count);
        Assert.Equal(2, Regex.Matches(block, @"\w+_pid=\$!").Count);
        Assert.Equal(2, Regex.Matches(block, @"'wait ""\$\w+_pid""").Count);
    }

    [Fact]
    public void Ci_runs_one_dotnet_test_step_per_test_project()
    {
        // Two steps, not one shell block and not one step over the solution: the job summary
        // names the project that failed, which is the reason CI is not made parallel here.
        // Every_gate_names_every_test_project_in_the_solution cannot see this shape - a single
        // block running both projects would satisfy it.
        var content = ReadRepositoryFile(".github", "workflows", "ci.yml");
        var steps = Regex.Matches(content, @"(?m)^\s*-\s+run:\s*(?<command>dotnet test\b.*)$")
            .Select(match => NormalizeCommand(match.Groups["command"].Value))
            .ToArray();

        Assert.Equal(SolutionTestProjects().Length, steps.Length);
        Assert.Equal(
            SolutionTestProjects(),
            steps.Select(TestProjectOf).OrderBy(project => project, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void StageCommand_ignores_comment_shadow_above_wrapped_command()
    {
        var content = """
            # dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror }
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", command);
    }

    [Fact]
    public void StageCommand_comment_shadow_does_not_hide_real_command_drift()
    {
        var baseline = """
            # dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror }
            """;
        var candidate = """
            # dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary }
            """;

        var baselineCommand = StageCommand(baseline, "build");
        var candidateCommand = StageCommand(candidate, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", baselineCommand);
        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror -clp:NoSummary", candidateCommand);
        Assert.NotEqual(baselineCommand, candidateCommand);
    }

    [Fact]
    public void StageCommand_ignores_trailing_comment_outside_windows_wrapper()
    {
        var content = """
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --no-restore -warnaserror } # it's fine
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", command);
        Assert.DoesNotContain("it's fine", command, StringComparison.Ordinal);
    }

    [Fact]
    public void StageCommand_ignores_trailing_comment_outside_linux_wrapper()
    {
        var content = """
            Invoke-GateStage -Name 'build' -Arguments @($gateClone) -Lines @(
                'dotnet build SqlHarness.sln --no-restore -warnaserror'
            ) # it's fine
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --no-restore -warnaserror", command);
        Assert.DoesNotContain("it's fine", command, StringComparison.Ordinal);
    }

    [Fact]
    public void StageCommand_extracts_full_command_when_quoted_argument_contains_closing_brace()
    {
        var content = """
            Invoke-Stage -Name 'build' -Action { dotnet build SqlHarness.sln --property:Foo="a}b" }
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln --property:Foo=\"a}b\"", command);
    }

    [Fact]
    public void StageCommand_preserves_windows_single_quoted_filter()
    {
        var content = """
            Invoke-Stage -Name 'test' -Action { dotnet test --filter 'FullyQualifiedName!~Integration' }
            """;

        var command = StageCommand(content, "test");

        Assert.Equal("dotnet test --filter \"FullyQualifiedName!~Integration\"", command);
    }

    [Fact]
    public void StageCommand_drops_trailing_unquoted_comment_from_yaml_run()
    {
        var content = """
            steps:
              - run: dotnet build SqlHarness.sln # note
            """;

        var command = StageCommand(content, "build");

        Assert.Equal("dotnet build SqlHarness.sln", command);
        Assert.DoesNotContain("# note", command, StringComparison.Ordinal);
    }

    [Fact]
    public void The_extractor_returns_the_expected_commands_for_the_real_gate_files()
    {
        var windows = ReadRepositoryFile("scripts", "verify.ps1");
        var linux = ReadRepositoryFile("scripts", "verify-linux.ps1");
        var ci = ReadRepositoryFile(".github", "workflows", "ci.yml");

        Assert.Equal("dotnet build SqlHarness.sln -warnaserror", StageCommand(windows, "build"));
        Assert.Equal("dotnet build SqlHarness.sln -warnaserror", StageCommand(linux, "build"));
        Assert.Equal("dotnet build -warnaserror", StageCommand(ci, "build"));
        // The Windows command is composed from the parallel stage's argument list, so its
        // filter value carries no quotes: PowerShell hands the argument to dotnet without
        // them, exactly as bash strips them from the Linux command. ArgumentsOf is what
        // makes the two spellings compare equal, and it is asserted on all three above.
        Assert.Equal(
            [
                "dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter FullyQualifiedName!~Integration",
                "dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-build --filter FullyQualifiedName!~Integration",
            ],
            TestCommands("scripts/verify.ps1", windows));
        Assert.Equal(
            [
                "dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter \"FullyQualifiedName!~Integration\" --logger \"console;verbosity=normal\"",
                "dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-build --filter \"FullyQualifiedName!~Integration\" --logger \"console;verbosity=normal\"",
            ],
            TestCommands("scripts/verify-linux.ps1", linux));
        Assert.Equal(
            [
                "dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-build --filter \"FullyQualifiedName!~Integration\" --logger trx --results-directory TestResults",
                "dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-build --filter \"FullyQualifiedName!~Integration\" --logger trx --results-directory TestResults",
            ],
            TestCommands(".github/workflows/ci.yml", ci));
        Assert.Equal("dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes", FormatShape(StageCommand(windows, "format")));
        Assert.Equal("dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes", FormatShape(StageCommand(linux, "format")));
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_scopes_the_format_check_to_changed_files(string path)
    {
        var command = StageCommand(File.ReadAllText(RepositoryFile.Locate(path.Split('/'))), "format");

        Assert.Contains("--include", command, StringComparison.Ordinal);
        Assert.Contains("--verify-no-changes", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    public void Every_local_gate_routes_stage_output_through_the_shared_gate_output(string path)
    {
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

        Assert.Contains("gate-output.ps1", content, StringComparison.Ordinal);
        Assert.Contains("Invoke-GateStage", content, StringComparison.Ordinal);
        Assert.Contains("Get-GateSummaryLine", content, StringComparison.Ordinal);
        Assert.Contains("Get-StageFailureText", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_gate_checks_the_files_the_working_tree_changed()
    {
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1"));

        Assert.Contains("format-changed.ps1", content, StringComparison.Ordinal);
        Assert.Contains("-ListOnly", content, StringComparison.Ordinal);
        Assert.DoesNotContain("-Mode branch", content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_linux_gate_checks_the_files_the_branch_changed_because_the_clone_only_holds_commits()
    {
        // The gate clone is reset to the branch tip, so a working-tree diff there is
        // always empty. Scope the check to what the branch changed over origin/main.
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Contains("format-changed.ps1", content, StringComparison.Ordinal);
        Assert.Contains("-Mode branch", content, StringComparison.Ordinal);
        Assert.Contains("-ListOnly", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_still_checks_formatting_over_the_whole_solution()
    {
        var content = File.ReadAllText(RepositoryFile.Locate(".github", "workflows", "ci.yml"));

        Assert.Equal("dotnet format --no-restore --verify-no-changes", StageCommand(content, "format"));
    }

    [Fact]
    public void The_justfile_runs_the_gates_and_can_rewrite_formatting()
    {
        var justfile = File.ReadAllText(RepositoryFile.Locate("justfile"));

        Assert.Matches(@"(?m)^verify\b", justfile);
        Assert.Matches(@"(?m)^verify-linux\b", justfile);
        Assert.Matches(@"(?m)^fix\b", justfile);
        Assert.Contains("scripts/verify.ps1", justfile, StringComparison.Ordinal);
        Assert.Contains("scripts/verify-linux.ps1", justfile, StringComparison.Ordinal);
        Assert.Contains("scripts/format-changed.ps1 -Fix", justfile, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verification_contract_uses_the_justfile_rather_than_the_raw_scripts()
    {
        var verification = ReadRepositoryFile("docs", "agents", "verification.md");

        Assert.Contains("just verify", verification, StringComparison.Ordinal);
        Assert.Contains("just verify-linux", verification, StringComparison.Ordinal);
        Assert.Contains("just fix", verification, StringComparison.Ordinal);
    }

    [Fact]
    public void The_linux_gate_is_optional_until_ci_moves_to_github_actions()
    {
        // The decision: while CI runs on the local machine, one gate is enough. The
        // command stays, and the contract says when it becomes required again.
        var verification = ReadRepositoryFile("docs", "agents", "verification.md");

        Assert.Contains("just verify`;", verification, StringComparison.Ordinal);
        Assert.DoesNotContain("Run `just verify`, then `just verify-linux`", verification, StringComparison.Ordinal);
        Assert.Contains("GitHub Actions", verification, StringComparison.Ordinal);
    }

    private const string UiCheckCommand = "npm run check --prefix src/SqlHarness.Dashboard/ui";

    // The Windows gate starts the UI check as a background process, so the command
    // arrives as a FilePath plus an argument vector instead of one literal line. Both
    // spellings name the same check against the same directory, so the assertion is
    // equally strong either way; only the surface form differs. Returns the position
    // of the anchor so the caller can still pin the order, or -1 when the gate never
    // names the check at all.
    private static int UiCheckIndex(string path, string content)
    {
        if (path != "scripts/verify.ps1")
        {
            return content.IndexOf(UiCheckCommand, StringComparison.Ordinal);
        }

        var filePath = content.IndexOf("-FilePath 'npm'", StringComparison.Ordinal);
        if (filePath < 0) { return -1; }
        var argumentList = content.IndexOf("@('run', 'check', '--prefix', 'src/SqlHarness.Dashboard/ui')", StringComparison.Ordinal);
        return argumentList < 0 ? -1 : Math.Min(filePath, argumentList);
    }

    // The Windows gate installs through scripts/ui-install.ps1, the Linux gate with
    // its own stamp logic in bash, CI with a plain npm ci.
    private static string UiInstallMarker(string path) => path switch
    {
        "scripts/verify.ps1" => "ui-install.ps1",
        "scripts/verify-linux.ps1" => "npm ci",
        _ => "npm ci --prefix src/SqlHarness.Dashboard/ui",
    };

    [Theory]
    [InlineData("scripts/verify.ps1")]
    [InlineData("scripts/verify-linux.ps1")]
    [InlineData(".github/workflows/ci.yml")]
    public void Every_gate_installs_and_checks_the_ui_before_the_dotnet_build(string path)
    {
        // dotnet build is the first .NET stage in every gate, so "before the build" is
        // the earliest .NET work a gate can do; the UI must be installed and checked by
        // then or the gate pays for the UI twice.
        var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));
        var build = content.IndexOf("dotnet build", StringComparison.Ordinal);
        var uiCheck = UiCheckIndex(path, content);

        Assert.True(build > 0, "the gate must build");
        var install = UiInstallMarker(path);
        var installIndex = content.IndexOf(install, StringComparison.Ordinal);

        Assert.True(installIndex >= 0, $"{path} must install the UI with '{install}'");
        Assert.True(installIndex < build, $"{path} must install the UI before dotnet build");
        Assert.True(uiCheck >= 0, $"{path} must run '{UiCheckCommand}'");
        Assert.True(uiCheck < build, $"{path} must check the UI before dotnet build");
    }

    [Fact]
    public void Both_local_gates_skip_the_ui_install_when_the_lockfile_is_unchanged()
    {
        // npm ci deleted and reinstalled node_modules on every run: 64s on Windows,
        // 54s in WSL. The stamp contract lives in scripts/ui-install.ps1 and is
        // mirrored in bash for the gate clone, where PowerShell cannot run.
        var installer = File.ReadAllText(RepositoryFile.Locate("scripts", "ui-install.ps1"));
        var windows = File.ReadAllText(RepositoryFile.Locate("scripts", "verify.ps1"));
        var linux = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Contains(".sqlharness-install-stamp", installer, StringComparison.Ordinal);
        Assert.Contains("package-lock.json", installer, StringComparison.Ordinal);
        Assert.Contains("Get-UiInstallState", windows, StringComparison.Ordinal);
        Assert.Contains(".sqlharness-install-stamp", linux, StringComparison.Ordinal);
        Assert.Contains("sha256sum", linux, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_and_release_take_node_from_nvmrc()
    {
        foreach (var path in new[] { ".github/workflows/ci.yml", ".github/workflows/release.yml" })
        {
            var content = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));
            Assert.Contains("actions/setup-node@", content, StringComparison.Ordinal);
            Assert.Contains("node-version-file: .nvmrc", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Linux_gate_puts_the_provisioned_node_first_and_checks_its_version()
    {
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Contains("$HOME/.node/current/bin", content, StringComparison.Ordinal);
        Assert.Contains("cat .nvmrc", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_gate_rejects_a_windows_interop_node_with_a_setup_hint()
    {
        // Without a provisioned Linux node, WSL interop resolves node.exe from the Windows PATH
        // (/mnt/...). It prints its version with CRLF, so a plain comparison would report a
        // misleading "differs from .nvmrc" for the same version.
        var content = File.ReadAllText(RepositoryFile.Locate("scripts", "verify-linux.ps1"));

        Assert.Contains("command -v node", content, StringComparison.Ordinal);
        Assert.Contains("/mnt/*", content, StringComparison.Ordinal);
        Assert.Contains("No Linux node on PATH", content, StringComparison.Ordinal);
    }

    private const string VerbGroup = @"(?<verb>restore|build|test|format)";
    private const string Body = @"(?:[^""'\}\r\n]|""[^""]*""|'[^']*')*";

    // The bash array expansion "${changedFiles[@]}" is a double-quoted token, which
    // the Linux body already accepts; single-quoted bash lines are literal here.
    private const string LinuxBody = @"(?:[^""'\r\n]|""[^""]*"")*";
    private const string YamlBody = @"(?:[^""'\r\n]|""[^""]*""|'[^']*')*";

    // One `dotnet <verb>` command in any of the three surfaces: a PowerShell scriptblock
    // stage, a single-quoted bash line, or a yaml `run:` step. The test commands of a gate
    // that runs one project per line are found with the same pattern, which is why the
    // plural form below exists.
    private static string StagePattern(string verbGroup) =>
        $@"(?:\{{\s*(?<command>dotnet {verbGroup}\b{Body})\s*\}})|(?:'(?<command>dotnet {verbGroup}\b{LinuxBody})')|(?:(?m)^\s*(?:-\s+)?run:\s*(?<command>dotnet {verbGroup}\b{YamlBody}))";

    // Add-Stage -Name 'test' -Project @('a', 'b') -Arguments @('--no-build', ...)
    private static readonly Regex ParallelTestStage = new(
        @"Add-Stage\s+-Name\s+'test'\s+-Project\s*@\((?<projects>.*?)\)\s*-Arguments\s*@\((?<arguments>.*?)\)",
        RegexOptions.Singleline);

    private static readonly Regex TestProjectArgument = new(@"^dotnet test\s+(?<project>\S+\.csproj)");

    internal static string FormatShape(string command) =>
        Regex.Replace(
            command,
            "--include\\s+(?:\"\\$\\{changedFiles\\[@\\]\\}\"|\\$changedFiles)",
            "--include $changedFiles");

    private static string ReadRepositoryFile(params string[] parts) =>
        File.ReadAllText(RepositoryFile.Locate(parts));

    [Fact]
    public void FormatShape_hides_only_the_expansion_syntax_of_the_include_list()
    {
        var windows = "dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes";
        var linux = "dotnet format SqlHarness.sln --no-restore --include \"${changedFiles[@]}\" --verify-no-changes";

        Assert.Equal(windows, FormatShape(linux));
    }

    [Fact]
    public void FormatShape_keeps_drift_in_the_format_flags_visible()
    {
        var windows = "dotnet format SqlHarness.sln --no-restore --include $changedFiles --verify-no-changes";
        var linux = "dotnet format SqlHarness.sln --no-restore --include \"${changedFiles[@]}\"";

        Assert.NotEqual(windows, FormatShape(linux));
    }

    internal static string[] StageVerbs(string content)
    {
        var verbs = new List<(int Index, string Verb)>();
        foreach (Match match in Regex.Matches(content, StagePattern(VerbGroup)))
        {
            verbs.Add((match.Index, match.Groups["verb"].Value));
        }

        // A project list is one `dotnet test` command per project, and the Windows gate hands
        // that list to the shared parallel stage helper rather than spelling the commands
        // out, so each project contributes a `test` at the position of the stage running it.
        var parallel = ParallelTestStage.Match(content);
        if (parallel.Success)
        {
            foreach (var _ in ParseStringArray(parallel.Groups["projects"].Value))
            {
                verbs.Add((parallel.Index, "test"));
            }
        }

        // Distinct, in first-occurrence order. The ORDER of the stages is the property under
        // test; the count is not, because two `test` verbs now means two test projects running
        // at once. The stages that must appear exactly once are asserted separately.
        return verbs
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.Verb)
            .Distinct()
            .ToArray();
    }

    internal static string[] StageCommands(string content, string verb)
    {
        // A trailing `&` is how the Linux gate schedules a project in the background, not an
        // argument of dotnet, and a trailing `# ...` is a shell comment. Both are stripped
        // with the command so what is compared is the command and not how the shell runs
        // it; the backgrounding itself is pinned by
        // The_linux_gate_starts_both_test_projects_in_the_background_and_waits_for_both.
        return Regex.Matches(content, StagePattern(verb))
            .Select(match => NormalizeCommand(
                Regex.Replace(match.Groups["command"].Value, @"(?:\s+#.*$|\s+&\s*$)", "")))
            .ToArray();
    }

    internal static string StageCommand(string content, string verb)
    {
        var commands = StageCommands(content, verb);

        if (commands.Length == 0)
        {
            Assert.Fail($"No 'dotnet {verb}' command was found.");
        }

        return commands[0];
    }

    // The `dotnet test` commands one surface really runs, one per test project. The Windows
    // gate names its projects in the parallel stage helper rather than spelling a command
    // line per project, so its list is composed from that project list and the shared
    // argument list - which is the command the stage actually runs, and so a stronger thing
    // to compare between the gates than a literal line would be. The other two surfaces
    // spell their commands out and are read as they are written.
    internal static string[] TestCommands(string path, string content)
    {
        if (path != "scripts/verify.ps1")
        {
            var commands = StageCommands(content, "test");
            if (commands.Length == 0)
            {
                Assert.Fail($"No 'dotnet test' command was found in {path}.");
            }

            return commands;
        }

        var stage = ParallelTestStage.Match(content);
        if (!stage.Success)
        {
            Assert.Fail("scripts/verify.ps1 must run its test stage through the shared parallel stage helper.");
            return [];
        }

        var arguments = ParseStringArray(stage.Groups["arguments"].Value);
        return ParseStringArray(stage.Groups["projects"].Value)
            .Select(project => NormalizeCommand($"dotnet test {project} {string.Join(" ", arguments)}".Trim()))
            .ToArray();
    }

    // The commands one surface runs for a verb. `test` always goes through TestCommands,
    // because "the test command" of a gate with two test projects is a list, not a line.
    internal static string[] CommandsFor(string path, string content, string verb) =>
        verb == "test" ? TestCommands(path, content) : StageCommands(content, verb);

    // The project a test command names, spelled the way the solution spells it.
    internal static string TestProjectOf(string command)
    {
        var match = TestProjectArgument.Match(command);

        Assert.True(match.Success, $"'{command}' names no test project.");
        return match.Groups["project"].Value.Replace('\\', '/');
    }

    // The test projects the solution itself holds, read out of SqlHarness.sln rather than
    // hard-coded. That is what makes Every_gate_names_every_test_project_in_the_solution
    // fail on the day a third test project is added instead of on the day someone notices.
    internal static string[] SolutionTestProjects() =>
        Regex.Matches(ReadRepositoryFile("SqlHarness.sln"), @"""(?<project>tests\\[^""]+\.csproj)""", RegexOptions.IgnoreCase)
            .Select(match => match.Groups["project"].Value.Replace('\\', '/'))
            .OrderBy(project => project, StringComparer.Ordinal)
            .ToArray();

    private static string[] ParseStringArray(string literal) =>
        Regex.Matches(literal, @"'(?<value>[^']*)'|""(?<value>[^""]*)""")
            .Select(match => match.Groups["value"].Value)
            .ToArray();

    // The arguments of a command as the process receives them: shell quoting is stripped, so
    // a bare FullyQualifiedName!~Integration and a "FullyQualifiedName!~Integration" are the
    // same argument. This is what the parity comparison compares, because the three surfaces
    // spell the same argument differently and their text is not the thing that must match.
    internal static string[] ArgumentsOf(string command) =>
        Regex.Matches(command, @"""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>\S+)")
            .Select(match => match.Groups["value"].Value)
            .ToArray();

    // The filter value of ONE command. It ends at the first space or quote after --filter,
    // and it may be quoted or bare, because the three surfaces spell it three ways.
    internal static string FilterValueOf(string command) =>
        Regex.Match(command, @"--filter\s+['""]?(?<value>[^'""\s]+)")
            .Groups["value"]
            .Value;

    private static string NormalizeCommand(string command) =>
        command.Trim().Replace('\'', '"');
}