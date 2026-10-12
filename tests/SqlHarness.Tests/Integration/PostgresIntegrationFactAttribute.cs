namespace SqlHarness.Tests.Integration;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING";
    internal const string DisposableTargetConfirmationVariable = "SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE";

    public PostgresIntegrationFactAttribute(bool requireDisposableTargetConfirmation = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"{Variable} is not configured.";
            return;
        }

        if (requireDisposableTargetConfirmation &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DisposableTargetConfirmationVariable)))
            Skip = $"{DisposableTargetConfirmationVariable} is not configured.";
    }
}