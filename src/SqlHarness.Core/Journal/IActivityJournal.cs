namespace SqlHarness.Core;

/// <summary>
/// Best-effort activity journal. Implementations never throw: a failed write
/// returns a null handle or is skipped, so journaling cannot change an outcome.
/// </summary>
public interface IActivityJournal
{
    JournalHandle? Begin(SessionIdentity session, OperationStart start);

    void Complete(JournalHandle? handle, OperationEnd end);

    void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted);
}

public sealed class NullActivityJournal : IActivityJournal
{
    public static NullActivityJournal Instance { get; } = new();

    private NullActivityJournal() { }

    public JournalHandle? Begin(SessionIdentity session, OperationStart start) => null;

    public void Complete(JournalHandle? handle, OperationEnd end) { }

    public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) { }
}