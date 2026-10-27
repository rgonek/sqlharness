using System.Diagnostics;
using System.Security.Cryptography;

using SqlHarness.Core.Mutations;

using SqlHarness.Core.Journal;

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
    // Resolve per operation so MCP client info can fill in after an early call omitted it.
    private readonly Func<SessionIdentity?> _session;
    private readonly Func<CancellationToken, string?>? _cancelReason;
    private readonly Func<string?>? _rootsJson;
    private readonly Func<CancellationToken, AgentCallMetadata?>? _callMetadata;
    private readonly DiagnosticsConfig _diagnostics;

    public JournalingModule(
        ISqlHarnessModule inner,
        Func<IActivityJournal> journal,
        Func<SessionIdentity> session,
        Func<CancellationToken, string?>? cancelReason = null,
        Func<string?>? rootsJson = null,
        Func<CancellationToken, AgentCallMetadata?>? callMetadata = null,
        DiagnosticsConfig? diagnostics = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(session);
        _journal = new Lazy<IActivityJournal?>(() => Try(journal), LazyThreadSafetyMode.ExecutionAndPublication);
        _session = () => Try(session);
        _cancelReason = cancelReason;
        _rootsJson = rootsJson;
        _callMetadata = callMetadata;
        _diagnostics = diagnostics ?? new DiagnosticsConfig();
    }

    public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteAsync(effective, ct), ct);

    public Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation operation,
        TextWriter writer,
        CancellationToken ct = default) =>
        RunAsync(operation, effective => _inner.ExecuteWatchNdjsonAsync((SqlHarnessWatchOperation)effective, writer, ct), ct);

    private async Task<SqlHarnessOutcome> RunAsync(SqlHarnessOperation operation, Func<SqlHarnessOperation, Task<SqlHarnessOutcome>> run, CancellationToken ct)
    {
        operation = SqlHarnessOperationReferences.Ensure(operation);
        var journal = _journal.Value;
        var handle = Begin(journal, operation, ct);
        ConditionsModel? conditionsModel = null;
        IDisposable? phaseRecording = null;
        OperationPhaseRecorder? phaseRecorder = null;
        if (journal is not null && handle is not null)
        {
            try
            {
                phaseRecording = OperationPhaseRecorder.Begin(TimeProvider.System, _diagnostics.Enabled);
                phaseRecorder = OperationPhaseRecorder.Current;
            }
            catch
            {
                // Timing must never turn an operation failure into a journaling failure.
            }
        }

        try
        {
            var effective = journal is not null && handle is not null
                ? operation with
                {
                    RecordConditions = model =>
                    {
                        conditionsModel = model;
                        RecordConditions(journal, handle, model);
                    },
                }
                : operation;
            if (effective is SqlHarnessWatchOperation watch && journal is not null && handle is not null)
                effective = watch with { Progress = ProgressRecorder(journal, handle) };
            var stopwatch = Stopwatch.StartNew();
            SqlHarnessOutcome outcome;
            try
            {
                outcome = await run(effective);
            }
            catch (OperationCanceledException)
            {
                Complete(journal, handle, phaseRecorder, () => OperationJournalDescriber.Cancelled(stopwatch.ElapsedMilliseconds, Reason(ct)));
                throw;
            }
            catch (Exception)
            {
                Complete(journal, handle, phaseRecorder, () => OperationJournalDescriber.Crashed(stopwatch.ElapsedMilliseconds));
                throw;
            }

            // Core maps a cancellation during SQL to an ordinary failed outcome after disposing
            // its session; MCP reports such a call as cancelled, so the journal does too.
            if (ct.IsCancellationRequested && IsCancellableFailure(outcome.ExitCode))
            {
                Complete(journal, handle, phaseRecorder, () => OperationJournalDescriber.Cancelled(stopwatch.ElapsedMilliseconds, Reason(ct), outcome.SessionWarnings));
                return outcome with { OperationRef = operation.OperationRef, Journaled = handle is not null, OperationRefEligible = EligibleRef(operation) };
            }

            if (journal is not null && handle is not null && operation is SqlHarnessMutationOperation { DryRun: false, Approval: null }
                && outcome.ExitCode == SqlHarnessExitCode.Success && outcome.Report is SqlHarnessMutationPrepared prepared)
            {
                try
                {
                    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    outcome = outcome with { Report = prepared with { Request = prepared.Request with { PreparationToken = token } } };
                }
                catch
                {
                    // Correlation is best effort and cannot affect mutation preparation.
                }
            }

            var completed = Complete(journal, handle, phaseRecorder,
                () => OperationJournalDescriber.DescribeEnd(outcome, stopwatch.ElapsedMilliseconds, conditionsModel));
            if (completed && journal is not null && handle is not null && outcome.BenchmarkRuns is { Count: > 0 } runs)
                RecordBenchmark(journal, handle, runs, conditionsModel);

            // A failed completion (busy or broken journal) skips the emission write, so one
            // operation never waits on a locked journal a second time.
            if (!completed || journal is null || handle is null || outcome.EmissionReceipt is not { } inner)
                return outcome with { OperationRef = operation.OperationRef, Journaled = handle is not null, OperationRefEligible = EligibleRef(operation) };

            var wrapped = new SqlHarnessEmissionReceipt(async (emitted, ct) =>
            {
                var exitCode = await inner.CompleteAsync(emitted, ct);
                try
                {
                    journal.RecordEmission(handle, inner.RawFootprint, emitted);
                }
                catch (Exception)
                {
                    // IActivityJournal implementations do not throw; this guards third-party implementations.
                }

                return exitCode;
            })
            {
                RawFootprint = inner.RawFootprint,
            };
            return outcome with { EmissionReceipt = wrapped, OperationRef = operation.OperationRef, Journaled = handle is not null, OperationRefEligible = EligibleRef(operation) };
        }
        finally
        {
            phaseRecording?.Dispose();
        }
    }

    private static bool EligibleRef(SqlHarnessOperation operation) => operation.OperationAnnotationsEnabled && operation switch
    {
        SqlHarnessQueryOperation query => !query.DryRun,
        SqlHarnessMeasureOperation measure => !measure.DryRun,
        SqlHarnessCompareOperation compare => !compare.DryRun,
        SqlHarnessCompareMatrixOperation matrix => !matrix.DryRun,
        SqlHarnessWatchOperation or SqlHarnessSnapshotOperation => true,
        _ => false,
    };

    private JournalHandle? Begin(IActivityJournal? journal, SqlHarnessOperation operation, CancellationToken ct)
    {
        // A disabled journal records nothing, so the process tree is not walked at all.
        if (journal is null or NullActivityJournal || _session() is not { } session)
            return null;
        try
        {
            var start = OperationJournalDescriber.DescribeStart(operation);
            if (CallMetadata(ct) is { } agent)
                start = start with { Agent = agent };
            var current = _rootsJson is null ? session : session with { RootsJson = Roots() };
            return journal.Begin(current, start);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsCancellableFailure(SqlHarnessExitCode exitCode) =>
        exitCode is not (SqlHarnessExitCode.Success or SqlHarnessExitCode.WatchMaxDuration or SqlHarnessExitCode.SnapshotDifferences);

    private string? Reason(CancellationToken ct)
    {
        if (_cancelReason is null)
            return null;
        try
        {
            return _cancelReason(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private AgentCallMetadata? CallMetadata(CancellationToken ct)
    {
        if (_callMetadata is null)
            return null;
        try
        {
            return _callMetadata(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? Roots()
    {
        if (_rootsJson is null)
            return null;
        try
        {
            return _rootsJson();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Complete(
        IActivityJournal? journal,
        JournalHandle? handle,
        OperationPhaseRecorder? phases,
        Func<OperationEnd> end)
    {
        if (journal is null || handle is null)
            return false;
        try
        {
            // The end row is described inside the guard: a describer failure must neither turn
            // a success into an exception nor replace the operation's own exception.
            var operationEnd = end();
            try
            {
                journal.RecordPhases(handle, phases?.Aggregates() ?? Array.Empty<PhaseAggregate>(),
                    phases?.Events() ?? Array.Empty<PhaseEvent>(), phases?.DroppedEvents ?? 0);
            }
            catch (Exception)
            {
                // Phase detail is best-effort and cannot replace the operation result.
            }

            return journal.Complete(handle, operationEnd);
        }
        catch (Exception)
        {
            // IActivityJournal implementations do not throw; this guards third-party implementations.
            return false;
        }
    }

    private static void RecordBenchmark(
        IActivityJournal journal, JournalHandle handle, IReadOnlyList<CompareRunArtifact> runs, ConditionsModel? conditions)
    {
        try
        {
            // Plan parsing and aggregation run inside the guard: a malformed run never
            // turns a successful benchmark into an exception.
            journal.RecordBenchmark(handle, JournalBenchmarkBuilder.Build(runs, conditions));
        }
        catch (Exception)
        {
            // Best-effort: the journal row stays completed without benchmark detail.
        }
    }

    private static void RecordConditions(IActivityJournal journal, JournalHandle handle, ConditionsModel model)
    {
        try
        {
            journal.RecordConditions(handle, model);
        }
        catch (Exception)
        {
            // Conditions journaling is best-effort and cannot change SQL execution.
        }
    }

    private static Action<WatchProgress> ProgressRecorder(IActivityJournal journal, JournalHandle handle)
    {
        var enabled = true;
        return progress =>
        {
            if (!enabled)
                return;
            try
            {
                // One failed write (busy or broken journal) stops progress for this watch,
                // so a locked journal cannot add its timeout to every poll.
                enabled = journal.RecordWatchProgress(handle, progress);
            }
            catch (Exception)
            {
                enabled = false;
            }
        };
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