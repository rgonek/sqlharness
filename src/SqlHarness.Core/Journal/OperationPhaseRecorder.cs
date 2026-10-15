namespace SqlHarness.Core.Journal;

public enum OperationPhase
{
    Connect,
    Setup,
    Execute,
    Artifact,
}

public sealed record PhaseAggregate(
    OperationPhase Phase,
    int Count,
    long TotalMilliseconds,
    long MaxMilliseconds);

public sealed record PhaseEvent(
    int Sequence,
    long OffsetMilliseconds,
    OperationPhase Phase,
    long DurationMilliseconds,
    string Outcome,
    int? ErrorNumber,
    string? DetailJson);

public sealed class OperationPhaseRecorder
{
    public const int MaximumEvents = 256;

    private static readonly AsyncLocal<OperationPhaseRecorder?> CurrentRecorder = new();
    private static readonly IReadOnlyList<PhaseEvent> NoEvents = Array.Empty<PhaseEvent>();

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private readonly bool _captureEvents;
    private readonly Dictionary<OperationPhase, MutableAggregate> _aggregates = [];
    private readonly List<PhaseEvent>? _events;
    private int _droppedEvents;

    private OperationPhaseRecorder(TimeProvider time, bool captureEvents)
    {
        _time = time;
        _startedAt = time.GetTimestamp();
        _captureEvents = captureEvents;
        if (captureEvents)
            _events = new List<PhaseEvent>(MaximumEvents);
    }

    public static OperationPhaseRecorder? Current => CurrentRecorder.Value;

    internal bool CapturesEvents => _captureEvents;

    public static IDisposable Begin(TimeProvider time, bool captureEvents)
    {
        ArgumentNullException.ThrowIfNull(time);
        var previous = CurrentRecorder.Value;
        CurrentRecorder.Value = new OperationPhaseRecorder(time, captureEvents);
        return new RestoreCurrent(previous);
    }

    public PhaseScope Start(OperationPhase phase)
    {
        try
        {
            return new PhaseScope(this, phase, _time.GetTimestamp());
        }
        catch
        {
            return default;
        }
    }

    public IReadOnlyList<PhaseAggregate> Aggregates()
    {
        lock (_sync)
        {
            return _aggregates
                .OrderBy(pair => pair.Key)
                .Select(pair => new PhaseAggregate(
                    pair.Key,
                    pair.Value.Count,
                    pair.Value.TotalMilliseconds,
                    pair.Value.MaxMilliseconds))
                .ToArray();
        }
    }

    public IReadOnlyList<PhaseEvent> Events()
    {
        if (_events is null)
            return NoEvents;
        lock (_sync)
            return _events.ToArray();
    }

    public int DroppedEvents
    {
        get
        {
            lock (_sync)
                return _droppedEvents;
        }
    }

    internal static long ElapsedMilliseconds(TimeProvider time, long start, long end) =>
        Math.Max(0, (long)time.GetElapsedTime(start, end).TotalMilliseconds);

    internal void Record(
        OperationPhase phase,
        long startedAt,
        string outcome,
        int? errorNumber,
        string? detailJson)
    {
        var endedAt = _time.GetTimestamp();
        var duration = ElapsedMilliseconds(_time, startedAt, endedAt);
        var offset = ElapsedMilliseconds(_time, _startedAt, endedAt);
        lock (_sync)
        {
            if (!_aggregates.TryGetValue(phase, out var aggregate))
            {
                aggregate = new MutableAggregate();
                _aggregates.Add(phase, aggregate);
            }

            aggregate.Count++;
            aggregate.TotalMilliseconds += duration;
            aggregate.MaxMilliseconds = Math.Max(aggregate.MaxMilliseconds, duration);

            if (_events is null || phase is not (OperationPhase.Connect or OperationPhase.Artifact))
                return;
            if (_events.Count >= MaximumEvents)
            {
                _droppedEvents++;
                return;
            }

            _events.Add(new PhaseEvent(
                _events.Count + 1,
                offset,
                phase,
                duration,
                outcome,
                errorNumber,
                detailJson));
        }
    }

    private sealed class MutableAggregate
    {
        internal int Count;
        internal long TotalMilliseconds;
        internal long MaxMilliseconds;
    }

    private sealed class RestoreCurrent(OperationPhaseRecorder? previous) : IDisposable
    {
        private OperationPhaseRecorder? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            CurrentRecorder.Value = _previous;
            _previous = null;
            _disposed = true;
        }
    }
}

public struct PhaseScope : IDisposable
{
    private readonly OperationPhaseRecorder? _recorder;
    private readonly OperationPhase _phase;
    private readonly long _startedAt;
    private string? _detailJson;
    private int? _errorNumber;
    private bool _failed;
    private bool _disposed;

    internal PhaseScope(OperationPhaseRecorder recorder, OperationPhase phase, long startedAt)
    {
        _recorder = recorder;
        _phase = phase;
        _startedAt = startedAt;
        _detailJson = null;
        _errorNumber = null;
        _failed = false;
        _disposed = false;
    }

    public void Fail(int? errorNumber)
    {
        _failed = true;
        _errorNumber = errorNumber;
    }

    public void Detail(string detailJson)
    {
        ArgumentNullException.ThrowIfNull(detailJson);
        _detailJson = detailJson.Length <= 512 ? detailJson : detailJson[..512];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _recorder?.Record(
                _phase,
                _startedAt,
                _failed ? "failed" : "succeeded",
                _errorNumber,
                _detailJson);
        }
        catch
        {
            // Timing must never change the operation result.
        }
    }
}