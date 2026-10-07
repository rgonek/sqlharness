using Microsoft.Data.SqlClient;

using SqlHarness.Core.Auth;

namespace SqlHarness.Core;

/// <summary>
/// Lifecycle stage of one operation execution. Validation covers bounds,
/// profile resolution and SQL safety before any connection opens; artifact
/// covers local report/artifact writes after SQL completes.
/// </summary>
internal enum OperationPhase
{
    Validation,
    Authentication,
    Sql,
    Artifact,
}

/// <summary>
/// Single owner for exception-to-exit-code mapping across query, watch,
/// snapshot, compare and measure. Mapping is by exception type and lifecycle
/// phase only, never by message text. Auth versus SQL versus local-storage
/// differences are preserved per phase (see tests).
/// </summary>
internal static class OperationFailureMapper
{
    internal static SqlHarnessExitCode Map(Exception exception, OperationPhase phase) => exception switch
    {
        ArtifactStoragePreflightException => SqlHarnessExitCode.LocalStorage,
        SqlTargetMismatchException => SqlHarnessExitCode.TargetMismatch,
        SqlHarnessSafetyException => SqlHarnessExitCode.Safety,
        IOException or UnauthorizedAccessException when phase == OperationPhase.Validation => SqlHarnessExitCode.LocalStorage,
        AzureCliException => SqlHarnessExitCode.Authentication,
        SqlException when phase == OperationPhase.Authentication => SqlHarnessExitCode.Authentication,
        SqlException => SqlHarnessExitCode.SqlExecution,
        Npgsql.NpgsqlException when phase == OperationPhase.Authentication => SqlHarnessExitCode.Authentication,
        Npgsql.NpgsqlException => SqlHarnessExitCode.SqlExecution,
        TimeoutException => SqlHarnessExitCode.SqlExecution,
        OperationCanceledException when phase == OperationPhase.Sql => SqlHarnessExitCode.SqlExecution,
        _ when phase == OperationPhase.Authentication => SqlHarnessExitCode.Authentication,
        _ => SqlHarnessExitCode.SqlExecution,
    };

    /// <summary>
    /// Runs best-effort <paramref name="cleanup"/> without ever masking
    /// <paramref name="primary"/>. When the main path already failed, a
    /// cleanup failure is swallowed so the original cause propagates; when
    /// the main path succeeded (no primary), a cleanup failure propagates
    /// as the real failure.
    /// </summary>
    internal static void CompleteCleanup(Exception? primary, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            cleanup();
        }
        catch when (primary is not null)
        {
            // Preserve the primary failure if cleanup also fails.
        }
    }
}