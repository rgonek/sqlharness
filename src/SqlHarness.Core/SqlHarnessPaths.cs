namespace SqlHarness.Core;

public static class SqlHarnessPaths
{
    public static string Home =>
        Environment.GetEnvironmentVariable("SQLHARNESS_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlharness");

    public static string TargetsFile => Path.Combine(Home, "targets.json");
    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string ActivityDatabase => Path.Combine(Home, "data", "activity.db");
    public static string CompareDir => Path.Combine(Home, "compare");
    public static string SnapshotsDir => Path.Combine(Home, "snapshots");
    public static string QueryStoreDir => Path.Combine(Home, "query-store");
    public static string IndexAnalysisDir => Path.Combine(Home, "index-analysis");
}