using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class ReleaseWorkflowTests
{
    [Fact]
    public void Release_build_is_gated_by_the_reusable_ci_workflow()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        AssertReleaseBuildIsGated(workflow);
    }

    [Fact]
    public void Release_build_contract_rejects_a_gate_job_that_does_not_call_ci()
    {
        const string workflow = """
            jobs:
              gate:
                runs-on: ubuntu-latest
                steps:
                  - run: echo no reusable CI
              build:
                needs: gate
            """;

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertReleaseBuildIsGated(workflow));
    }

    [Fact]
    public void Release_write_permission_is_scoped_to_the_release_job()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        AssertReleaseWritePermissionIsScoped(workflow);
    }

    [Fact]
    public void Release_permission_contract_rejects_a_later_write_enabled_job()
    {
        const string workflow = """
            permissions:
              contents: read
            jobs:
              release:
                permissions:
                  contents: write
                steps: []
              audit:
                permissions:
                  contents: write
                steps: []
            """;

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertReleaseWritePermissionIsScoped(workflow));
    }

    [Fact]
    public void Every_workflow_checkout_disables_persisted_credentials()
    {
        foreach (var path in new[] { ".github/workflows/ci.yml", ".github/workflows/release.yml" })
        {
            var workflow = File.ReadAllText(RepositoryFile.Locate(path.Split('/')));

            AssertEveryCheckoutDisablesPersistedCredentials(workflow);
        }
    }

    [Fact]
    public void Checkout_contract_rejects_an_additional_checkout_without_credential_hardening()
    {
        const string workflow = """
            jobs:
              build:
                steps:
                  - uses: actions/checkout@v4
                    with:
                      persist-credentials: false
                  - name: Fetch another source
                    uses: actions/checkout@v4
            """;

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEveryCheckoutDisablesPersistedCredentials(workflow));
    }

    [Fact]
    public void Release_job_creates_the_release_with_explicit_repository_context_without_checkout()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));
        var releaseJob = Regex.Match(
            workflow,
            @"(?ms)^  release:\r?\n(?<body>.*?)(?=^\S|\z)").Groups["body"].Value;

        Assert.NotEmpty(releaseJob);
        AssertReleaseJobContract(releaseJob);
    }

    [Fact]
    public void Release_job_contract_rejects_named_checkout_step()
    {
        const string releaseJob = """
            steps:
              - name: Checkout repository
                uses: actions/checkout@v4
              - name: Create GitHub Release
                run: gh release create "$GITHUB_REF_NAME" artifacts/* --generate-notes --title "$GITHUB_REF_NAME" --repo "$GITHUB_REPOSITORY"
            """;

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertReleaseJobContract(releaseJob));
    }

    [Fact]
    public void Build_matrix_publishes_every_supported_rid_on_its_native_runner()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        AssertBuildPair(workflow, "windows-latest", "win-x64");
        AssertBuildPair(workflow, "ubuntu-latest", "linux-x64");
        AssertBuildPair(workflow, "macos-14", "osx-arm64");
    }

    [Fact]
    public void Publish_step_is_self_contained_single_file_untrimmed_for_the_cli()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));

        Assert.Contains("src/SqlHarness.Cli", workflow, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=true", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishTrimmed=false", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_step_stamps_the_version_from_the_release_tag()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "release.yml"));
        var publishStep = Regex.Match(
            workflow,
            @"(?ms)^      - name: Publish single-file binary\r?\n(?<body>.*?)(?=^      - (?:name|uses):|\z)").Groups["body"].Value;

        Assert.NotEmpty(publishStep);
        Assert.Contains("shell: bash", publishStep, StringComparison.Ordinal);
        Assert.Contains("-p:Version=${GITHUB_REF_NAME#v}", publishStep, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_builds_the_ui_before_testing_and_publishing()
    {
        var workflow = File.ReadAllText(RepositoryFile.Locate(".github", "workflows", "release.yml"));
        var install = workflow.IndexOf("npm ci --prefix src/SqlHarness.Dashboard/ui", StringComparison.Ordinal);

        Assert.True(install >= 0);
        Assert.True(install < workflow.IndexOf("dotnet test", StringComparison.Ordinal));
    }

    private static void AssertBuildPair(string workflow, string os, string rid)
    {
        var pair = Regex.Match(
            workflow,
            @"(?ms)os:\s*" + Regex.Escape(os) + @"\r?\n\s*rid:\s*" + Regex.Escape(rid) + @"\b");

        Assert.True(pair.Success, $"The build matrix must publish {rid} on its native runner {os}.");
    }

    private static void AssertReleaseBuildIsGated(string workflow)
    {
        var jobs = ExtractJobs(workflow);
        var gateJob = jobs.GetValueOrDefault("gate") ?? string.Empty;
        var buildJob = jobs.GetValueOrDefault("build") ?? string.Empty;

        Assert.NotEmpty(gateJob);
        Assert.Matches(@"(?m)^    uses:\s*\./\.github/workflows/ci\.yml\s*$", gateJob);
        Assert.NotEmpty(buildJob);
        Assert.Matches(@"(?m)^    needs:\s*gate\s*$", buildJob);
    }

    private static void AssertReleaseWritePermissionIsScoped(string workflow)
    {
        var jobs = ExtractJobs(workflow);
        var releaseJob = jobs.GetValueOrDefault("release") ?? string.Empty;
        var topLevelPermissions = Regex.Match(
            workflow,
            @"(?ms)^permissions:\r?\n(?<body>(?:^  .*\r?\n)+)").Groups["body"].Value;

        Assert.Matches(@"(?m)^  contents:\s*read\s*$", topLevelPermissions);
        Assert.NotEmpty(releaseJob);
        Assert.Matches(@"(?m)^    permissions:\r?\n      contents:\s*write\s*$", releaseJob);
        foreach (var (jobName, jobBody) in jobs)
        {
            if (!string.Equals(jobName, "release", StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(@"(?m)^\s*contents:\s*write\s*$", jobBody);
            }
        }
    }

    private static Dictionary<string, string> ExtractJobs(string workflow)
    {
        var jobsBody = Regex.Match(workflow, @"(?ms)^jobs:\r?\n(?<body>.*)\z").Groups["body"].Value;
        return Regex.Matches(jobsBody, @"(?ms)^  (?<name>[\w-]+):\r?\n(?<body>.*?)(?=^  [\w-]+:\r?\n|\z)")
            .ToDictionary(match => match.Groups["name"].Value, match => match.Groups["body"].Value, StringComparer.Ordinal);
    }

    private static void AssertEveryCheckoutDisablesPersistedCredentials(string workflow)
    {
        var steps = Regex.Matches(workflow, @"(?ms)^      - (?<body>.*?)(?=^      - |\z)")
            .Select(match => match.Groups["body"].Value)
            .Where(body => Regex.IsMatch(body, @"(?m)^\s*uses:\s*actions/checkout@"))
            .ToArray();

        Assert.NotEmpty(steps);
        foreach (var step in steps)
        {
            Assert.Matches(
                @"(?ms)^\s+with:\r?\n(?:(?!^      - ).*\r?\n)*?\s+persist-credentials:\s*false\s*$",
                step);
        }
    }

    private static void AssertReleaseJobContract(string releaseJob)
    {
        Assert.DoesNotMatch(@"(?m)^\s*(?:-\s*)?uses:\s*actions/checkout@", releaseJob);

        var command = FindReleaseCreateCommand(releaseJob);

        Assert.Matches(@"\bgh\s+release\s+create\b", command);
        Assert.Matches("\\bgh\\s+release\\s+create\\s+(?:\\\"\\$GITHUB_REF_NAME\\\"|\\$GITHUB_REF_NAME)", command);
        Assert.Matches(@"(?<!\S)artifacts/\*(?!\S)", command);
        Assert.Matches(@"--generate-notes\b", command);
        Assert.Matches("--title\\s+(?:\\\"\\$GITHUB_REF_NAME\\\"|\\$GITHUB_REF_NAME)", command);
        Assert.Matches("--repo\\s+(?:\\\"\\$GITHUB_REPOSITORY\\\"|\\$GITHUB_REPOSITORY)", command);
    }

    private static string FindReleaseCreateCommand(string releaseJob)
    {
        var match = Regex.Match(
            releaseJob,
            @"(?ms)^\s*run:\s*(?<command>(?:(?!^\s*-\s+(?:name|uses):).)*?\bgh\s+release\s+create\b(?:(?!^\s*-\s+(?:name|uses):).)*)(?=^\s*-\s+(?:name|uses):|\z)");

        Assert.True(match.Success, "The release job must run gh release create.");
        return match.Groups["command"].Value;
    }

    private static string FindRepositoryFile(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate the repository release workflow.");
    }
}