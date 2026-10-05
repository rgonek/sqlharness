using System.Text.Json;

namespace SqlHarness.Core;

/// <summary>
/// One NDJSON record of the optional <c>watch --output ndjson</c> stream.
/// A run emits <c>started</c> once a session exists, zero or more <c>changed</c>
/// records (only for changed results), then exactly one terminal record:
/// <c>completed</c> or <c>failed</c>, never both. Sequence numbers start at 1
/// for <c>started</c> and increase strictly by one per record.
/// </summary>
public sealed record WatchNdjsonEvent(
    int SchemaVersion,
    string Event,
    long Sequence,
    long ElapsedMilliseconds,
    object? Data);

/// <summary>
/// Serializes <see cref="WatchNdjsonEvent"/> records as single NDJSON lines
/// and flushes the transport after every record. Serialization happens before
/// any byte is written, so a serialization failure leaves the stream
/// untouched and consumes no sequence number. After a terminal record
/// (<c>completed</c> or <c>failed</c>) the writer is closed and rejects any
/// further record, which enforces the single-terminal invariant.
/// </summary>
internal sealed class WatchNdjsonWriter(TextWriter output)
{
    public const int SchemaVersion = 1;

    public const string StartedEvent = "started";
    public const string ChangedEvent = "changed";
    public const string CompletedEvent = "completed";
    public const string FailedEvent = "failed";

    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private long _nextSequence = 1;

    /// <summary>True once a terminal record was emitted.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Sequence number the next record will carry.</summary>
    public long NextSequence => _nextSequence;

    public void WriteStarted(object? data, long elapsedMilliseconds) =>
        Write(StartedEvent, data, elapsedMilliseconds, final: false);

    public void WriteChanged(object? data, long elapsedMilliseconds) =>
        Write(ChangedEvent, data, elapsedMilliseconds, final: false);

    public void WriteCompleted(object? data, long elapsedMilliseconds) =>
        Write(CompletedEvent, data, elapsedMilliseconds, final: true);

    public void WriteFailed(object? data, long elapsedMilliseconds) =>
        Write(FailedEvent, data, elapsedMilliseconds, final: true);

    internal static string FormatExitReason(WatchExitReason reason) => reason switch
    {
        WatchExitReason.ConditionMet => "condition-met",
        WatchExitReason.Unchanged => "unchanged",
        WatchExitReason.MaxDuration => "max-duration",
        _ => reason.ToString().ToLowerInvariant(),
    };

    private void Write(string kind, object? data, long elapsedMilliseconds, bool final)
    {
        if (IsClosed)
            throw new InvalidOperationException("The NDJSON watch stream already has a terminal record.");
        // Serialize first: a serialization failure writes nothing, flushes
        // nothing, and consumes no sequence number.
        var line = JsonSerializer.Serialize(
            new WatchNdjsonEvent(SchemaVersion, kind, _nextSequence, elapsedMilliseconds, data),
            CompactJson);
        _output.WriteLine(line);
        // Flush after every record: consumers observe each event immediately
        // and no history is accumulated before emission.
        _output.Flush();
        _nextSequence++;
        if (final)
            IsClosed = true;
    }
}