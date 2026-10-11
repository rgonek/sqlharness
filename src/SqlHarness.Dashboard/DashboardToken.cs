namespace SqlHarness.Dashboard;

/// <summary>
/// The dashboard token persists in the SQLHarness home (owner-only), so a cookie from an
/// earlier start keeps working after restarts and autostarts. Deleting the file rotates it.
/// It protects the journal from other local OS users; Host/Origin/header checks protect
/// against web pages.
/// </summary>
internal static class DashboardToken
{
    internal const string FileName = "dashboard-token";

    internal static string LoadOrCreate(string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        var path = Path.Combine(home, FileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 40)
                return existing;
        }

        var token = DashboardSecurity.NewToken();
        DashboardLock.WriteOwnerOnly(path, token);
        return token;
    }
}
