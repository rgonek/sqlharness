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

    /// <summary>
    /// Ensures the SQLHarness home exists before a writer creates anything under it. On Unix a home
    /// SQLHarness creates is 0700; an existing home keeps its mode. No-op for a path outside the home.
    /// </summary>
    public static void EnsureHomeFor(string path) => HomeDirectory.EnsureFor(path);

    /// <summary>The home's Unix mode, or null off Unix and when the home does not exist yet.</summary>
    public static UnixFileMode? HomeMode => HomeDirectory.Mode();
}