namespace SqlHarness.Tests.Integration;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING";

    public PostgresIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not configured.";
    }
}