using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlHarnessPathsTests
{
    private static readonly object EnvironmentLock = new();

    [Fact]
    public void Paths_default_to_dot_sqlharness_under_user_profile()
    {
        lock (EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
            try
            {
                Environment.SetEnvironmentVariable("SQLHARNESS_HOME", null);
                var expectedHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".sqlharness");

                Assert.Equal(expectedHome, SqlHarnessPaths.Home);
                Assert.Equal(Path.Combine(expectedHome, "targets.json"), SqlHarnessPaths.TargetsFile);
                Assert.Equal(Path.Combine(expectedHome, "data", "gain.jsonl"), SqlHarnessPaths.GainFile);
                Assert.Equal(Path.Combine(expectedHome, "compare"), SqlHarnessPaths.CompareDir);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SQLHARNESS_HOME", original);
            }
        }
    }

    [Fact]
    public void Sqlharness_home_override_is_used_for_every_path()
    {
        lock (EnvironmentLock)
        {
            var original = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
            var home = Path.Combine(Path.GetTempPath(), $"sqlharness-paths-{Guid.NewGuid():N}");
            try
            {
                Environment.SetEnvironmentVariable("SQLHARNESS_HOME", home);

                Assert.Equal(home, SqlHarnessPaths.Home);
                Assert.Equal(Path.Combine(home, "targets.json"), SqlHarnessPaths.TargetsFile);
                Assert.Equal(Path.Combine(home, "data", "gain.jsonl"), SqlHarnessPaths.GainFile);
                Assert.Equal(Path.Combine(home, "compare"), SqlHarnessPaths.CompareDir);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SQLHARNESS_HOME", original);
            }
        }
    }
}
