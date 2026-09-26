using System.Security.Cryptography.X509Certificates;
using System.Text;

using SqlHarness.Core.Targets;

namespace SqlHarness.Core.Postgres;

internal static class PostgresTransportPolicy
{
    internal const string Legacy = "legacy";
    internal const string Explicit = "explicit";
    internal const string InvalidSslMode = "Postgres sslMode is invalid.";
    internal const string ContradictsTrust = "Postgres sslMode contradicts trustServerCertificate.";
    internal const string RootRequiresVerifying = "Postgres rootCertificate requires a verifying sslMode.";
    internal const string RootUnreadable = "Postgres rootCertificate is missing or unreadable.";
    internal const string SqlServerFields = "SQL Server targets cannot set Postgres transport fields.";

    internal static PostgresTransport? Resolve(
        SqlEngine engine,
        bool trustServerCertificate,
        string? sslMode,
        string? rootCertificate)
    {
        if (engine != SqlEngine.Postgres)
        {
            if (sslMode is not null || rootCertificate is not null)
                throw new SqlHarnessSafetyException(SqlServerFields);
            return null;
        }

        if (sslMode is null && rootCertificate is null)
            return null;

        if (sslMode is null)
            throw new SqlHarnessSafetyException(RootRequiresVerifying);

        if (!TryParseMode(sslMode, out var mode))
            throw new SqlHarnessSafetyException(InvalidSslMode);

        if (Contradicts(trustServerCertificate, mode))
            throw new SqlHarnessSafetyException(ContradictsTrust);

        if (rootCertificate is null)
            return new PostgresTransport(mode, null);

        if (mode is not (PostgresSslMode.VerifyFull or PostgresSslMode.VerifyCa))
            throw new SqlHarnessSafetyException(RootRequiresVerifying);

        EnsureReadable(rootCertificate);
        return new PostgresTransport(mode, rootCertificate);
    }

    internal static PostgresSslMode EffectiveMode(ResolvedTarget target) =>
        target.Transport?.Mode
        ?? (target.Auth.TrustServerCertificate ? PostgresSslMode.Disable : PostgresSslMode.Require);

    internal static string Label(ResolvedTarget target) =>
        target.Transport is null ? Legacy : Explicit;

    internal static void EnsureAccepted(ResolvedTarget target)
    {
        if (target.Transport is not { } transport)
            return;

        if (Contradicts(target.Auth.TrustServerCertificate, transport.Mode))
            throw new SqlHarnessSafetyException(ContradictsTrust);

        if (transport.RootCertificate is null)
            return;

        if (transport.Mode is not (PostgresSslMode.VerifyFull or PostgresSslMode.VerifyCa))
            throw new SqlHarnessSafetyException(RootRequiresVerifying);

        EnsureReadable(transport.RootCertificate);
    }

    private static bool TryParseMode(string value, out PostgresSslMode mode)
    {
        switch (value)
        {
            case "verify-full":
                mode = PostgresSslMode.VerifyFull;
                return true;
            case "verify-ca":
                mode = PostgresSslMode.VerifyCa;
                return true;
            case "require":
                mode = PostgresSslMode.Require;
                return true;
            case "disable":
                mode = PostgresSslMode.Disable;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static bool Contradicts(bool trustServerCertificate, PostgresSslMode mode) =>
        trustServerCertificate
            ? mode != PostgresSslMode.Disable
            : mode == PostgresSslMode.Disable;

    private static void EnsureReadable(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new SqlHarnessSafetyException(RootUnreadable);

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
                throw new SqlHarnessSafetyException(RootUnreadable);

            using var certificate = LoadCertificate(bytes);
            if (certificate.RawData.Length == 0)
                throw new SqlHarnessSafetyException(RootUnreadable);
        }
        catch (SqlHarnessSafetyException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SqlHarnessSafetyException(RootUnreadable);
        }
    }

    private static X509Certificate2 LoadCertificate(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            return X509Certificate2.CreateFromPem(text);

        return new X509Certificate2(bytes);
    }
}
