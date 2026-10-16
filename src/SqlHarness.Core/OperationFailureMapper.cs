using Microsoft.Data.SqlClient;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;

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
    internal static bool IsMutationOutcomeUnknown(Exception exception)
    {
        if (exception is TimeoutException or OperationCanceledException)
            return true;
        if (exception is Microsoft.Data.SqlClient.SqlException sqlException)
            return sqlException.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().Any(error => error.Number is
                -2 or 0 or 64 or 233 or 10053 or 10054 or 10060 or 10928 or 10929 or 40197 or 40501 or 40613);
        return exception is Npgsql.NpgsqlException and not Npgsql.PostgresException;
    }

    internal static SqlHarnessExitCode Map(Exception exception, OperationPhase phase)
    {
        if (PostgresReadOnlyFailure.ContainsReadOnlyViolation(exception))
            return SqlHarnessExitCode.Safety;

        return exception switch
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
    }

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
