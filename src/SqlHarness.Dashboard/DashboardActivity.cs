namespace SqlHarness.Dashboard;

/// <summary>Request and live-stream activity, used by the background idle shutdown.</summary>
internal sealed class DashboardActivity(TimeProvider time)
{
    private int _openStreams;
    private long _lastRequestTicks = time.GetUtcNow().UtcTicks;

    internal int OpenStreams => Volatile.Read(ref _openStreams);

    internal DateTimeOffset LastRequest => new(Interlocked.Read(ref _lastRequestTicks), TimeSpan.Zero);

    internal void Touch() => Interlocked.Exchange(ref _lastRequestTicks, time.GetUtcNow().UtcTicks);

    internal void StreamOpened()
    {
        Interlocked.Increment(ref _openStreams);
        Touch();
    }

    internal void StreamClosed()
    {
        Interlocked.Decrement(ref _openStreams);
        Touch();
    }
}