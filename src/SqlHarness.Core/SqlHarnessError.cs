namespace SqlHarness.Core;

/// <summary>A stable, safe description of a failed SQLHarness operation.</summary>
public sealed record SqlHarnessError(
    string Code,
    string Phase,
    string Message,
    string? Hint = null,
    SqlHarnessErrorLocation? Location = null)
{
    public static SqlHarnessError From(SqlHarnessExitCode exitCode, string? message, string? phase = null) =>
        new(
            exitCode switch
            {
                SqlHarnessExitCode.Safety => "safety_rejected",
                SqlHarnessExitCode.Authentication => "authentication_failed",
                SqlHarnessExitCode.TargetMismatch => "target_mismatch",
                SqlHarnessExitCode.SqlExecution => "sql_execution_failed",
                SqlHarnessExitCode.LocalStorage => "local_storage_failed",
                SqlHarnessExitCode.WatchMaxDuration => "watch_max_duration",
                SqlHarnessExitCode.SnapshotDifferences => "snapshot_differences",
                _ => "operation_failed",
            },
            phase ?? exitCode switch
            {
                SqlHarnessExitCode.Safety or SqlHarnessExitCode.TargetMismatch => "validation",
                SqlHarnessExitCode.Authentication => "authentication",
                SqlHarnessExitCode.SqlExecution => "sql",
                SqlHarnessExitCode.LocalStorage => "artifact",
                _ => "execution",
            },
            string.IsNullOrWhiteSpace(message) ? "The operation failed." : message);
}

public sealed record SqlHarnessErrorLocation(int? Line = null, int? Column = null, string? Path = null);

public sealed record SqlHarnessAgentEnvelope(
    int SchemaVersion,
    string Command,
    string Status,
    int ExitCode,
    object? Result,
    SqlHarnessError? Error,
    object? Truncation = null);
