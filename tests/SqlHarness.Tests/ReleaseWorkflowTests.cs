using System.Text.RegularExpressions;

namespace SqlHarness.Tests;

public sealed class ReleaseWorkflowTests
{
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