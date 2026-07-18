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
        Assert.DoesNotMatch(@"(?m)^\s*- uses: actions/checkout@", releaseJob);
        Assert.Matches(
            "gh release create \\\"\\$GITHUB_REF_NAME\\\" artifacts/\\* --generate-notes --title \\\"\\$GITHUB_REF_NAME\\\" --repo \\\"\\$GITHUB_REPOSITORY\\\"",
            releaseJob);
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