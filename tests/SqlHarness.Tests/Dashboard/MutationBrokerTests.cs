using SqlHarness.Core;
using SqlHarness.Core.Mutations;
using SqlHarness.Dashboard.Mutations;

namespace SqlHarness.Tests.Dashboard;

public sealed class MutationBrokerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Approving_pending_request_executes_once_and_returns_success()
    {
        var clock = new ManualTimeProvider(Start);
        var calls = 0;
        var broker = Broker(clock, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Success());
        });
        var request = Request();
        var submit = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default);

        Assert.True(broker.TryApprove("1", "click"));
        Assert.Equal(MutationStatus.Success, (await submit).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Denying_pending_request_never_executes()
    {
        var clock = new ManualTimeProvider(Start);
        var calls = 0;
        var broker = Broker(clock, (_, _) => { calls++; return Task.FromResult(Success()); });
        var request = Request();
        var submit = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default);

        Assert.True(broker.TryDeny("1"));
        Assert.Equal(MutationStatus.Denied, (await submit).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Expired_request_cannot_be_approved_even_when_timer_dispatch_is_delayed()
    {
        var clock = new ManualTimeProvider(Start);
        var calls = 0;
        var broker = Broker(clock, (_, _) => { calls++; return Task.FromResult(Success()); });
        var request = Request();
        var submit = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default);

        clock.Advance(Ttl + TimeSpan.FromTicks(1), dispatchTimers: false);

        Assert.False(broker.TryApprove("1", "click"));
        Assert.Equal(MutationStatus.Timeout, (await submit).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Approval_racing_deadline_never_executes_after_timeout()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var clock = new ManualTimeProvider(Start);
            var calls = 0;
            var broker = Broker(clock, (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Success());
            });
            var request = Request(session: $"session-{iteration}");
            var submit = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default);
            clock.Advance(Ttl, dispatchTimers: false);

            var approve = Task.Run(() => broker.TryApprove("1", "click"));
            var expire = Task.Run(() => clock.DispatchTimers());
            await Task.WhenAll(approve, expire);
            var result = await submit;

            Assert.Contains(result.Status, new[] { MutationStatus.Success, MutationStatus.Timeout });
            Assert.Equal(result.Status == MutationStatus.Success ? 1 : 0, calls);
        }
    }

    [Fact]
    public async Task Cancelling_pending_submit_removes_it_and_makes_approval_impossible()
    {
        var clock = new ManualTimeProvider(Start);
        var broker = Broker(clock, (_, _) => Task.FromResult(Success()));
        var request = Request();
        var removed = new TaskCompletionSource<MutationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Changed += change =>
        {
            if (change is MutationRemoved { State: var state }) removed.TrySetResult(state);
        };
        using var cancellation = new CancellationTokenSource();
        var submit = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
        Assert.Equal(MutationState.Cancelled, await removed.Task);
        Assert.False(broker.TryApprove("1", "click"));
    }

    [Fact]
    public async Task Cancelling_joined_waiter_does_not_cancel_owner_request()
    {
        var clock = new ManualTimeProvider(Start);
        var broker = Broker(clock, (_, _) => Task.FromResult(Success()));
        var request = Request();
        using var joinedCancellation = new CancellationTokenSource();
        var owner = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default);
        var joined = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("2", request), joinedCancellation.Token);

        joinedCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
        Assert.Single(broker.Pending);
        Assert.True(broker.TryApprove("1", "click"));
        Assert.Equal(MutationStatus.Success, (await owner).Status);
    }

    [Fact]
    public async Task Duplicate_pending_digest_joins_one_request_and_one_added_event()
    {
        var clock = new ManualTimeProvider(Start);
        var broker = Broker(clock, (_, _) => Task.FromResult(Success()));
        var request = Request();
        var added = 0;
        broker.Changed += change => { if (change is MutationAdded) Interlocked.Increment(ref added); };
        var first = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default);
        var second = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("2", request), default);

        Assert.Single(broker.Pending);
        Assert.Equal(1, added);
        Assert.True(broker.TryApprove("1", "click"));
        Assert.Equal(MutationStatus.Success, (await first).Status);
        Assert.Equal(MutationStatus.Success, (await second).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Added_event_is_published_before_concurrent_removal(bool expire)
    {
        var clock = new ManualTimeProvider(Start);
        var broker = Broker(clock, (_, _) => Task.FromResult(Success()));
        var request = Request();
        var addedHandlerStarted = new ManualResetEventSlim();
        var releaseAddedHandler = new ManualResetEventSlim();
        var published = new List<string>();
        broker.Changed += change =>
        {
            if (change is MutationAdded)
            {
                addedHandlerStarted.Set();
                releaseAddedHandler.Wait();
                lock (published) published.Add("added");
            }
            else if (change is MutationRemoved)
            {
                lock (published) published.Add("removed");
            }
        };
        var submit = Task.Run(async () => await broker.SubmitAsync(
            request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default));

        try
        {
            Assert.True(addedHandlerStarted.Wait(TimeSpan.FromSeconds(5)));
            if (expire)
                clock.Advance(Ttl);
            else
                Assert.True(await Task.Run(() => broker.TryApprove("1", "click")));
        }
        finally
        {
            releaseAddedHandler.Set();
        }

        var result = await submit;

        Assert.Equal(expire ? MutationStatus.Timeout : MutationStatus.Success, result.Status);
        Assert.Equal(new[] { "added", "removed" }, published);
        addedHandlerStarted.Dispose();
        releaseAddedHandler.Dispose();
    }

    [Fact]
    public async Task Different_pending_digest_in_same_session_returns_busy()
    {
        var clock = new ManualTimeProvider(Start);
        var broker = Broker(clock, (_, _) => Task.FromResult(Success()));
        var first = Request(sql: "update t set x = 1");
        var second = Request(sql: "update t set x = 2");
        var pending = broker.SubmitAsync(first, MutationDecisionKind.AskOperator, Ttl, View("1", first), default);

        var busy = await broker.SubmitAsync(second, MutationDecisionKind.AskOperator, Ttl, View("2", second), default);

        Assert.Equal(MutationStatus.Busy, busy.Status);
        Assert.Single(broker.Pending);
        broker.TryDeny("1");
        await pending;
    }

    [Fact]
    public async Task Denial_digest_is_blocked_for_60_seconds_then_can_be_submitted_again()
    {
        var clock = new ManualTimeProvider(Start);
        var broker = Broker(clock, (_, _) => Task.FromResult(Success()));
        var request = Request();
        var added = 0;
        broker.Changed += change => { if (change is MutationAdded) added++; };
        var first = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("1", request), default);
        Assert.True(broker.TryDeny("1"));
        Assert.Equal(MutationStatus.Denied, (await first).Status);

        clock.Advance(TimeSpan.FromSeconds(60));
        var withinWindow = await broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("2", request), default);
        Assert.Equal(MutationStatus.Denied, withinWindow.Status);
        Assert.Equal(1, added);

        clock.Advance(TimeSpan.FromSeconds(1));
        var afterWindow = broker.SubmitAsync(request, MutationDecisionKind.AskOperator, Ttl, View("3", request), default);
        Assert.Equal(2, added);
        Assert.Single(broker.Pending);
        broker.TryDeny("3");
        await afterWindow;
    }

    [Fact]
    public async Task Execute_decision_skips_pending_notification()
    {
        var clock = new ManualTimeProvider(Start);
        var calls = 0;
        var broker = Broker(clock, (_, _) => { calls++; return Task.FromResult(Success()); });
        var request = Request();
        var added = 0;
        broker.Changed += change => { if (change is MutationAdded) added++; };

        var result = await broker.SubmitAsync(request, MutationDecisionKind.Execute, Ttl, View("1", request), default);

        Assert.Equal(MutationStatus.Success, result.Status);
        Assert.Equal(1, calls);
        Assert.Equal(0, added);
        Assert.Empty(broker.Pending);
    }

    [Theory]
    [InlineData(MutationDecisionKind.Execute, "Automatic")]
    [InlineData(MutationDecisionKind.AskOperator, "Operator")]
    public async Task Execution_forwards_origin_and_exact_request_id(
        MutationDecisionKind decision, string expectedOrigin)
    {
        var clock = new ManualTimeProvider(Start);
        MutationExecutionContext? context = null;
        var broker = new MutationBroker(clock, (_, execution, _) =>
        {
            context = execution;
            return Task.FromResult(Success());
        });
        var request = Request();
        var submit = broker.SubmitAsync(request, decision, Ttl, View("request-id-7", request), default);

        if (decision == MutationDecisionKind.AskOperator)
            Assert.True(broker.TryApprove("request-id-7", "click"));
        Assert.Equal(MutationStatus.Success, (await submit).Status);

        Assert.Equal(new MutationExecutionContext("request-id-7", Enum.Parse<MutationExecutionOrigin>(expectedOrigin)), context);
    }

    [Fact]
    public async Task Cancellation_after_execution_starts_does_not_cancel_executor_or_result()
    {
        var clock = new ManualTimeProvider(Start);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = Broker(clock, async (_, ct) =>
        {
            started.TrySetResult(ct);
            await release.Task;
            return Success();
        });
        using var cancellation = new CancellationTokenSource();
        var request = Request();
        var submit = broker.SubmitAsync(request, MutationDecisionKind.Execute, Ttl, View("1", request), cancellation.Token);

        var executorToken = await started.Task;
        cancellation.Cancel();
        release.TrySetResult();

        Assert.False(executorToken.CanBeCanceled);
        Assert.Equal(MutationStatus.Success, (await submit).Status);
    }

    [Fact]
    public async Task Different_profiles_execute_concurrently_but_same_profile_is_busy()
    {
        var clock = new ManualTimeProvider(Start);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = Broker(clock, async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return Success();
        });
        var first = Request(profile: "alpha");
        var firstSubmit = broker.SubmitAsync(first, MutationDecisionKind.Execute, Ttl, View("1", first), default);
        await started.Task;
        var sameProfile = Request(profile: "alpha", session: "other-session");
        var sameResult = await broker.SubmitAsync(sameProfile, MutationDecisionKind.Execute, Ttl, View("2", sameProfile), default);
        Assert.Equal(MutationStatus.Busy, sameResult.Status);

        var second = Request(profile: "beta");
        var secondSubmit = broker.SubmitAsync(second, MutationDecisionKind.Execute, Ttl, View("3", second), default);
        Assert.False(secondSubmit.IsCompleted);
        release.TrySetResult();

        Assert.Equal(MutationStatus.Success, (await firstSubmit).Status);
        Assert.Equal(MutationStatus.Success, (await secondSubmit).Status);
    }

    private static MutationBroker Broker(TimeProvider clock, Func<MutationRequest, CancellationToken, Task<MutationBrokerResult>> execute) => new(clock, execute);

    private static MutationRequest Request(string profile = "local", string session = "session", string sql = "update t set x = 1") =>
        new(profile, new Dictionary<string, string>(), "PostgreSQL", "localhost", "db", sql,
            Array.Empty<SqlHarnessParameterInput>(), 30, 10, "dml", Array.Empty<string>(), session);

    private static PendingMutationView View(string id, MutationRequest request) => new(id, request, "dev", true, Start + Ttl);

    private static MutationBrokerResult Success() => new(MutationStatus.Success, null, null);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount, bool dispatchTimers = true)
        {
            _now += amount;
            if (dispatchTimers) DispatchTimers();
        }

        public void DispatchTimers()
        {
            ManualTimer[] timers;
            lock (_timers) timers = _timers.ToArray();
            foreach (var timer in timers) timer.FireIfDue(_now);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private DateTimeOffset? _due = due == Timeout.InfiniteTimeSpan ? null : owner._now + due;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed) return false;
                _period = newPeriod;
                _due = newDueTime == Timeout.InfiniteTimeSpan ? null : owner._now + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

            public void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || _due is not { } due || now < due) return;
                _due = _period == Timeout.InfiniteTimeSpan ? null : now + _period;
                callback(state);
            }
        }
    }
}
