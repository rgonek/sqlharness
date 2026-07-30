using Microsoft.Data.SqlClient;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal sealed class SnapshotRunner(ISqlSessionFactory sessions, ISnapshotStore store, Func<DateTimeOffset>? utcNow = null)
{
    private const string OmittedRowsMessage =
        "Snapshot result exceeded --max-rows; raise the explicit bound or narrow the query.";

    private readonly ISqlSessionFactory _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly ISnapshotStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    internal async Task<(SqlHarnessOutcome Outcome, OutputFootprint RawFootprint)> ExecuteAsync(
        SqlHarnessSnapshotOperation operation,
        ResolvedTarget target,
        IReadOnlyList<SqlHarnessParameter> parameters,
        IReadOnlyCollection<string> knownSecrets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(knownSecrets);

        // Load baseline before authentication/connect so missing or corrupt files never open SQL.
        SnapshotDocument? baseline = null;
        if (operation.Diff)
        {
            try
            {
                baseline = _store.Load(operation.Name);
            }
            catch (Exception exception) when (IsLocalStorage(exception))
            {
                var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
                return (
                    new SqlHarnessOutcome(
                        SqlHarnessExitCode.LocalStorage,
                        null,
                        SecretRedactor.Redact(exception, secrets)),
                    new OutputFootprint(0, 0));
            }
        }

        var phase = SnapshotPhase.Authentication;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;

        try
        {
            await using var session = await _sessions.ConnectAsync(target, ct);
            phase = SnapshotPhase.Sql;

            var execution = new SqlExecutionCommand(operation.Sql, parameters, operation.TimeoutSeconds);
            var collected = await QueryResultCollector.CollectAsync(
                session,
                execution,
                operation.MaxRows,
                knownSecrets,
                () =>
                {
                    raw = new CanonicalResultAccumulator();
                    return raw;
                },
                ct);

            rawFootprint = raw!.Complete().Footprint;

            if (collected.ResultSets.Any(set => set.OmittedRowCount > 0))
                throw new SqlHarnessSafetyException(OmittedRowsMessage);

            var document = SnapshotDocument.Create(
                _utcNow(),
                collected.ResultSets,
                collected.Canonical.Hash);

            if (operation.Diff)
            {
                var diff = SnapshotDiffer.Compare(baseline!, document);
                var identical = diff.DifferenceCount == 0;
                var report = new SqlHarnessSnapshotReport(
                    session.Identity,
                    operation.Name,
                    identical ? SnapshotVerdict.Identical : SnapshotVerdict.Different,
                    diff.DifferenceCount,
                    diff.Differences);
                var exitCode = identical
                    ? SqlHarnessExitCode.Success
                    : SqlHarnessExitCode.SnapshotDifferences;
                return (new SqlHarnessOutcome(exitCode, report, null), rawFootprint);
            }

            try
            {
                _store.Save(operation.Name, document, operation.Force);
            }
            catch (Exception exception) when (IsLocalStorage(exception))
            {
                var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
                return (
                    new SqlHarnessOutcome(
                        SqlHarnessExitCode.LocalStorage,
                        null,
                        SecretRedactor.Redact(exception, secrets)),
                    rawFootprint);
            }

            var stored = new SqlHarnessSnapshotReport(
                session.Identity,
                operation.Name,
                SnapshotVerdict.Stored,
                DifferenceCount: 0,
                Differences: Array.Empty<SqlHarnessSnapshotDifference>());
            return (new SqlHarnessOutcome(SqlHarnessExitCode.Success, stored, null), rawFootprint);
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
            return (
                new SqlHarnessOutcome(
                    MapException(exception, phase),
                    null,
                    SecretRedactor.Redact(exception, secrets)),
                rawFootprint);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private static bool IsLocalStorage(Exception exception) =>
        exception is FileNotFoundException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException;

    private static SqlHarnessExitCode MapException(Exception exception, SnapshotPhase phase) => exception switch
    {
        SqlTargetMismatchException => SqlHarnessExitCode.TargetMismatch,
        SqlHarnessSafetyException => SqlHarnessExitCode.Safety,
        FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException =>
            SqlHarnessExitCode.LocalStorage,
        AzureCliException => SqlHarnessExitCode.Authentication,
        SqlException when phase == SnapshotPhase.Authentication => SqlHarnessExitCode.Authentication,
        SqlException => SqlHarnessExitCode.SqlExecution,
        TimeoutException => SqlHarnessExitCode.SqlExecution,
        OperationCanceledException when phase == SnapshotPhase.Sql => SqlHarnessExitCode.SqlExecution,
        _ when phase == SnapshotPhase.Authentication => SqlHarnessExitCode.Authentication,
        _ => SqlHarnessExitCode.SqlExecution,
    };

    private enum SnapshotPhase
    {
        Authentication,
        Sql,
    }
}