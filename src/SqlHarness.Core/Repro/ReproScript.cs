namespace SqlHarness.Core;

public enum ReproScriptForm
{
    AsExecuted,
    LocalVariables,
}

public enum ReproCompleteness
{
    Complete,
    ValuesMissing,
    Unavailable,
}

public sealed record ReproScript(string? Text, ReproCompleteness Completeness, string Label);

public static class ReproScripts
{
    public static ReproScript Create(
        ConditionsModel conditions,
        string? sideSql,
        string side,
        string caseId,
        ReproScriptForm form)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(caseId);

        if (!string.Equals(conditions.Engine, "sqlserver", StringComparison.OrdinalIgnoreCase))
            return new ReproScript(null, ReproCompleteness.Unavailable, "Script not available for this engine");
        if (string.IsNullOrWhiteSpace(sideSql) || conditions.Setup is { Text: null })
            return new ReproScript(null, ReproCompleteness.Unavailable, "Script not available — enable storeSensitive");

        return SqlServerReproScriptWriter.Write(conditions, sideSql, side, caseId, form);
    }
}
