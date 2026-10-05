using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

internal sealed class SnapshotRunner(ISqlSessionFactory sessions, ISnapshotStore store, Func<DateTimeOffset>? utcNow = null)
{
    private const string OmittedRowsMessage =
        "Snapshot result exceeded --max-rows; raise the explicit bound or narrow the query.";

    private const string ScopeRefusalMessage =
        "The snapshot is not available in the current scope.";

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

        var phase = OperationPhase.Authentication;
        // Scoped (MCP) calls carry the frozen scope owner: a foreign or
        // ownerless baseline is refused before any connection or SQL runs,
        // with a constant message that carries no cell values. Unscoped
        // (CLI) calls pass no owner and keep working by name.
        if (operation.Diff && operation.Owner is not null && !operation.Owner.Matches(baseline!.Owner))
        {
            return (
                new SqlHarnessOutcome(
                    SqlHarnessExitCode.Safety,
                    null,
                    ScopeRefusalMessage),
                new OutputFootprint(0, 0));
        }

        if (!operation.Diff && operation.Owner is not null)
        {
            var scopeGate = RequireCaptureScope(operation.Name, operation.Owner, knownSecrets);
            if (scopeGate is not null)
                return (scopeGate, new OutputFootprint(0, 0));
        }

        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;

        try
        {
            await using var session = await _sessions.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;

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

            if (operation.Owner is not null)
                document = document with { Owner = operation.Owner };

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

    // Scoped capture never overwrites and never self-grants: an existing
    // name owned by another scope (or with no owner at all) is refused
    // before any connection or SQL runs. A missing name returns null so
    // capture proceeds; other storage failures surface as local storage,
    // mirroring the diff baseline path.
    private SqlHarnessOutcome? RequireCaptureScope(
        string name,
        ArtifactOwner owner,
        IReadOnlyCollection<string> knownSecrets)
    {
        SnapshotDocument existing;
        try
        {
            existing = _store.Load(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (IsLocalStorage(exception))
        {
            var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
            return new SqlHarnessOutcome(
                SqlHarnessExitCode.LocalStorage,
                null,
                SecretRedactor.Redact(exception, secrets));
        }

        if (!owner.Matches(existing.Owner))
        {
            return new SqlHarnessOutcome(
                SqlHarnessExitCode.Safety,
                null,
                ScopeRefusalMessage);
        }

        return null;
    }

    private static bool IsLocalStorage(Exception exception) =>
        exception is FileNotFoundException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException;

    private static SqlHarnessExitCode MapException(Exception exception, OperationPhase phase) =>
        IsLocalStorage(exception)
            ? SqlHarnessExitCode.LocalStorage
            : OperationFailureMapper.Map(exception, phase);
}