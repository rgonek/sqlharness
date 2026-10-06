using System.Diagnostics;

namespace SqlHarness.Core;

/// <summary>
/// Records every operation in the activity journal around an inner module.
/// The inner outcome is returned unchanged except that its emission receipt is
/// wrapped to also record token footprints; journal or identity failures are
/// swallowed so they can never change output or exit codes.
/// </summary>
public sealed class JournalingModule : ISqlHarnessModule
{
    private readonly ISqlHarnessModule _inner;
    private readonly Lazy<IActivityJournal?> _journal;
    private readonly Lazy<SessionIdentity?> _session;

    public JournalingModule(ISqlHarnessModule inner, Func<IActivityJournal> journal, Func<SessionIdentity> session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(session);
        _journal = new Lazy<IActivityJournal?>(() => Try(journal), LazyThreadSafetyMode.ExecutionAndPublication);
        _session = new Lazy<SessionIdentity?>(() => Try(session), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
        RunAsync(operation, () => _inner.ExecuteAsync(operation, ct));

    public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        RunAsync(operation, () => _inner.ExecuteWatchNdjsonAsync(operation, writer, ct));

    private async Task<SqlHarnessOutcome> RunAsync(SqlHarnessOperation operation, Func<Task<SqlHarnessOutcome>> run)
    {
        var journal = _journal.Value;
        var handle = Begin(journal, operation);
        var stopwatch = Stopwatch.StartNew();
        SqlHarnessOutcome outcome;
        try
        {
            outcome = await run();
        }
        catch (OperationCanceledException)
        {
            Complete(journal, handle, OperationJournalDescriber.Cancelled(stopwatch.ElapsedMilliseconds));
            throw;
        }
        catch (Exception)
        {
            Complete(journal, handle, OperationJournalDescriber.Crashed(stopwatch.ElapsedMilliseconds));
            throw;
        }

        Complete(journal, handle, OperationJournalDescriber.DescribeEnd(outcome, stopwatch.ElapsedMilliseconds));
        if (journal is null || handle is null || outcome.EmissionReceipt is not { } inner)
            return outcome;

        var wrapped = new SqlHarnessEmissionReceipt(async (emitted, ct) =>
        {
            var exitCode = await inner.CompleteAsync(emitted, ct);
            journal.RecordEmission(handle, inner.RawFootprint, emitted);
            return exitCode;
        })
        {
            RawFootprint = inner.RawFootprint,
        };
        return outcome with { EmissionReceipt = wrapped };
    }

    private JournalHandle? Begin(IActivityJournal? journal, SqlHarnessOperation operation)
    {
        if (journal is null || _session.Value is not { } session)
            return null;
        try
        {
            return journal.Begin(session, OperationJournalDescriber.DescribeStart(operation));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Complete(IActivityJournal? journal, JournalHandle? handle, OperationEnd end)
    {
        try
        {
            journal?.Complete(handle, end);
        }
        catch (Exception)
        {
            // IActivityJournal implementations do not throw; this guards third-party implementations.
        }
    }

    private static T? Try<T>(Func<T> factory) where T : class
    {
        try
        {
            return factory();
        }
        catch (Exception)
        {
            return null;
        }
    }
}