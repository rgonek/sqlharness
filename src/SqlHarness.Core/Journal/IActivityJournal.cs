using SqlHarness.Core.Journal;

namespace SqlHarness.Core;

/// <summary>
/// Best-effort activity journal. Implementations never throw: a failed write
/// returns a null handle or is skipped, so journaling cannot change an outcome.
/// </summary>
public interface IActivityJournal
{
    JournalHandle? Begin(SessionIdentity session, OperationStart start);

    /// <summary>Returns true only when the completion row was written; callers skip later writes for that handle otherwise.</summary>
    bool Complete(JournalHandle? handle, OperationEnd end);

    void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted);

    /// <summary>Per-variant benchmark metrics; full plan documents only when the journal stores sensitive content.</summary>
    void RecordBenchmark(JournalHandle? handle, BenchmarkJournalRecord record) { }

    /// <summary>Stores canonical, bounded conditions for a resolved measure or compare operation.</summary>
    void RecordConditions(JournalHandle handle, ConditionsModel model) { }

    /// <summary>Phase aggregates and bounded diagnostic events for one operation.</summary>
    void RecordPhases(JournalHandle? handle, IReadOnlyList<PhaseAggregate> phases, IReadOnlyList<PhaseEvent> events, int droppedEvents) { }

    /// <summary>Latest progress of a running watch. Returns false when not written; callers stop reporting then.</summary>
    bool RecordWatchProgress(JournalHandle? handle, WatchProgress progress) => false;

    /// <summary>Records a mutation approval request; SQL and values are retained only when storeSensitive is enabled.</summary>
    void RecordMutation(MutationJournalRecord record) { }

    /// <summary>Links an accepted request to its unique journaled operation, when eligible.</summary>
    void LinkMutation(string requestId, string? operationRef, string? digest, string? preparationToken) { }

    /// <summary>Records a confirmed provider total for the exactly linked request.</summary>
    void RecordMutationResult(string requestId, int recordsAffected) { }

    /// <summary>Updates the latest state and lifecycle timestamps for a mutation request.</summary>
    void UpdateMutation(string requestId, string state, string? status, string? verification, DateTimeOffset at) { }
}

public sealed class NullActivityJournal : IActivityJournal
{
    public static NullActivityJournal Instance { get; } = new();

    private NullActivityJournal() { }

    public JournalHandle? Begin(SessionIdentity session, OperationStart start) => null;

    public bool Complete(JournalHandle? handle, OperationEnd end) => false;

    public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
}