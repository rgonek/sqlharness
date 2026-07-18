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