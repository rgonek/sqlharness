using SqlHarness.Core.Journal;

namespace SqlHarness.Tests.Journal;

public sealed class OperationPhaseRecorderTests
{
    [Fact]
    public void Aggregates_count_total_and_max_per_phase()
    {
        var time = new ManualTime();
        using (OperationPhaseRecorder.Begin(time, captureEvents: false))
        {
            var recorder = OperationPhaseRecorder.Current!;
            using (recorder.Start(OperationPhase.Connect)) time.Advance(30);
            using (recorder.Start(OperationPhase.Connect)) time.Advance(10);
            using (recorder.Start(OperationPhase.Execute)) time.Advance(1);

            Assert.Equal(
                new PhaseAggregate(OperationPhase.Connect, 2, 40, 30),
                recorder.Aggregates().Single(aggregate => aggregate.Phase == OperationPhase.Connect));
            Assert.Empty(recorder.Events());
        }

        Assert.Null(OperationPhaseRecorder.Current);
    }

    [Fact]
    public async Task Concurrent_flows_do_not_share_a_recorder()
    {
        var time = new ManualTime();

        async Task<int> Flow()
        {
            using (OperationPhaseRecorder.Begin(time, captureEvents: false))
            {
                using (OperationPhaseRecorder.Current!.Start(OperationPhase.Connect))
                    await Task.Yield();
                return OperationPhaseRecorder.Current!.Aggregates().Single().Count;
            }
        }

        var counts = await Task.WhenAll(Flow(), Flow());
        Assert.Equal([1, 1], counts);
    }

    [Fact]
    public void Events_are_capped_and_counted()
    {
        var time = new ManualTime();
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            var recorder = OperationPhaseRecorder.Current!;
            for (var index = 0; index < OperationPhaseRecorder.MaximumEvents + 5; index++)
                using (recorder.Start(OperationPhase.Connect)) { }

            Assert.Equal(OperationPhaseRecorder.MaximumEvents, recorder.Events().Count);
            Assert.Equal(5, recorder.DroppedEvents);
        }
    }

    [Fact]
    public void Diagnostic_only_event_does_not_change_phase_aggregates()
    {
        var time = new ManualTime();
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            var recorder = OperationPhaseRecorder.Current!;
            using (var phase = recorder.StartDiagnosticEvent(OperationPhase.Connect))
            {
                time.Advance(7);
                phase.Detail("{\"attempt\":1}");
            }

            Assert.Empty(recorder.Aggregates());
            Assert.Single(recorder.Events());
        }
    }

    [Fact]
    public void Event_detail_is_clipped_to_512_characters()
    {
        var time = new ManualTime();
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            var recorder = OperationPhaseRecorder.Current!;
            using (var scope = recorder.Start(OperationPhase.Connect))
                scope.Detail(new string('x', 600));

            Assert.Equal(512, Assert.Single(recorder.Events()).DetailJson!.Length);
        }
    }

    [Fact]
    public void Setup_and_execute_aggregate_without_consuming_event_cap()
    {
        var time = new ManualTime();
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            var recorder = OperationPhaseRecorder.Current!;
            for (var index = 0; index < OperationPhaseRecorder.MaximumEvents + 5; index++)
            {
                using (recorder.Start(OperationPhase.Setup)) { }
                using (recorder.Start(OperationPhase.Execute)) { }
            }

            using (recorder.Start(OperationPhase.Connect)) { }
            using (recorder.Start(OperationPhase.Artifact)) { }

            Assert.Equal(
                OperationPhaseRecorder.MaximumEvents + 5,
                recorder.Aggregates().Single(aggregate => aggregate.Phase == OperationPhase.Setup).Count);
            Assert.Equal(
                OperationPhaseRecorder.MaximumEvents + 5,
                recorder.Aggregates().Single(aggregate => aggregate.Phase == OperationPhase.Execute).Count);
            Assert.Equal(
                [OperationPhase.Connect, OperationPhase.Artifact],
                recorder.Events().Select(phaseEvent => phaseEvent.Phase));
            Assert.Equal(0, recorder.DroppedEvents);
        }
    }
}

internal sealed class ManualTime : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;

    internal void Advance(int milliseconds) => _timestamp += TimeSpan.FromMilliseconds(milliseconds).Ticks;
}