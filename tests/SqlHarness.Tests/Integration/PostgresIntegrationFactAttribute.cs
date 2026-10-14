namespace SqlHarness.Tests.Integration;

/// <summary>
/// The one opt-in gate for the PostgreSQL integration attributes: the reason the
/// run must be skipped, or <c>null</c> when the operator has opted in. Both
/// attributes delegate here, so a changed variable or message cannot reach one
/// of them and miss the other.
/// </summary>
internal static class PostgresIntegrationGate
{
    internal static string? SkipReason(bool requireDisposableTargetConfirmation)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.Variable)))
            return $"{PostgresIntegrationFactAttribute.Variable} is not configured.";

        if (requireDisposableTargetConfirmation &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.DisposableTargetConfirmationVariable)))
            return $"{PostgresIntegrationFactAttribute.DisposableTargetConfirmationVariable} is not configured.";

        return null;
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    internal const string Variable = "SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING";
    internal const string DisposableTargetConfirmationVariable = "SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE";

    public PostgresIntegrationFactAttribute(bool requireDisposableTargetConfirmation = false)
    {
        if (PostgresIntegrationGate.SkipReason(requireDisposableTargetConfirmation) is { } skipReason)
            Skip = skipReason;
    }
}

/// <summary>The <see cref="PostgresIntegrationGate"/> over an <see cref="TheoryAttribute"/>, so each row reports separately.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PostgresIntegrationTheoryAttribute : TheoryAttribute
{
    public PostgresIntegrationTheoryAttribute(bool requireDisposableTargetConfirmation = false)
    {
        if (PostgresIntegrationGate.SkipReason(requireDisposableTargetConfirmation) is { } skipReason)
            Skip = skipReason;
    }
}