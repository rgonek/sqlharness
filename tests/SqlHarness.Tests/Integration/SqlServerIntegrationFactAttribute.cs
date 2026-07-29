namespace SqlHarness.Tests.Integration;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class SqlServerIntegrationFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_INTEGRATION_CONNECTION_STRING";

    public SqlServerIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"{Variable} is not configured.";
    }
}
