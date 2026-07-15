namespace SqlHarness.Core;

public static class SqlHarnessPaths
{
    public static string Home =>
        Environment.GetEnvironmentVariable("SQLHARNESS_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlharness");

    public static string TargetsFile => Path.Combine(Home, "targets.json");
    public static string GainFile => Path.Combine(Home, "data", "gain.jsonl");
    public static string CompareDir => Path.Combine(Home, "compare");
}
