using Microsoft.Data.SqlClient;

namespace SqlHarness.Core.Auth;

public enum AuthStrategy
{
    AzureCli,
    AdDefault,
    Integrated,
    Sql,
}

public sealed record AuthSpec(
    AuthStrategy Strategy,
    string? SqlUser = null,
    string? PasswordEnvVar = null,
    bool TrustServerCertificate = false)
{
    public static AuthSpec Parse(
        string name,
        string? sqlUser,
        string? passwordEnvVar,
        bool trustServerCertificate)
    {
        var strategy = name switch
        {
            "azure-cli" => AuthStrategy.AzureCli,
            "ad-default" => AuthStrategy.AdDefault,
            "integrated" => AuthStrategy.Integrated,
            "sql" => AuthStrategy.Sql,
            _ => throw new SqlHarnessSafetyException($"Unknown authentication strategy '{name}'."),
        };

        if (strategy == AuthStrategy.Sql &&
            (string.IsNullOrWhiteSpace(sqlUser) || string.IsNullOrWhiteSpace(passwordEnvVar)))
        {
            throw new SqlHarnessSafetyException(
                "SQL authentication requires both a user and a password environment variable.");
        }

        return new AuthSpec(strategy, sqlUser, passwordEnvVar, trustServerCertificate);
    }

    internal string BuildConnectionString(string server, string database, int connectTimeoutSeconds)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = true,
            ConnectTimeout = connectTimeoutSeconds,
        };

        if (TrustServerCertificate)
            builder.TrustServerCertificate = true;

        switch (Strategy)
        {
            case AuthStrategy.AzureCli:
                break;
            case AuthStrategy.AdDefault:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
            case AuthStrategy.Integrated:
                builder.IntegratedSecurity = true;
                break;
            case AuthStrategy.Sql:
                var password = Environment.GetEnvironmentVariable(PasswordEnvVar!);
                if (string.IsNullOrEmpty(password))
                {
                    throw new SqlHarnessSafetyException(
                        $"SQL password environment variable '{PasswordEnvVar}' is missing or empty.");
                }

                builder.UserID = SqlUser;
                builder.Password = password;
                break;
            default:
                throw new SqlHarnessSafetyException($"Unsupported authentication strategy '{Strategy}'.");
        }

        return builder.ConnectionString;
    }

    internal bool RequiresAccessToken => Strategy == AuthStrategy.AzureCli;
}
