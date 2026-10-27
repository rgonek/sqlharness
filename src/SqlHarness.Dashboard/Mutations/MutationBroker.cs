using System.Threading.Channels;

using SqlHarness.Core.Mutations;

namespace SqlHarness.Dashboard.Mutations;

internal enum MutationState { Pending, Approved, Denied, Expired, Cancelled, Executing, Succeeded, Failed, OutcomeUnknown }
internal enum MutationApprovalResult { Accepted, Busy, NotPending }

internal sealed record PendingMutationView(string RequestId, MutationRequest Request, string Environment, bool WriterSameAsReader,
    DateTimeOffset ExpiresAt);

internal abstract record BrokerEvent;
internal sealed record MutationAdded(PendingMutationView View) : BrokerEvent;
internal sealed record MutationRemoved(string RequestId, MutationState State) : BrokerEvent;
internal sealed record MutationEventEnvelope(long Sequence, BrokerEvent Event);

internal sealed class MutationBrokerSubscription(MutationBroker broker, ChannelReader<MutationEventEnvelope> events) : IDisposable
{
    public ChannelReader<MutationEventEnvelope> Events { get; } = events;
    public IReadOnlyList<PendingMutationView> Snapshot { get; internal init; } = [];
    public long Sequence { get; internal init; }
    public void Dispose() => broker.Unsubscribe(this);
}

internal sealed class MutationBroker
{
    private static readonly TimeSpan DenialCooldown = TimeSpan.FromSeconds(60);
    private readonly TimeProvider time;
    private readonly Func<MutationRequest, MutationExecutionContext, CancellationToken, Task<MutationBrokerResult>> execute;

    internal MutationBroker(TimeProvider time, Func<MutationRequest, CancellationToken, Task<MutationBrokerResult>> execute)
        : this(time, (request, _, ct) => execute(request, ct)) { }

    internal MutationBroker(TimeProvider time, Func<MutationRequest, MutationExecutionContext, CancellationToken, Task<MutationBrokerResult>> execute)
    {
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingRequest> _pendingById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _deniedDigests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _executingProfiles = new(StringComparer.Ordinal);
    private readonly Queue<MutationEventEnvelope> _events = new();
    private readonly Dictionary<MutationBrokerSubscription, Channel<MutationEventEnvelope>> _subscriptions = [];
    private long _eventSequence;
    private bool _dispatchingEvents;

    internal event Action<BrokerEvent>? Changed;

    internal MutationBrokerSubscription Subscribe()
    {
        lock (_gate)
        {
            var channel = Channel.CreateUnbounded<MutationEventEnvelope>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var subscription = new MutationBrokerSubscription(this, channel.Reader)
            {
                Snapshot = _pendingById.Values.Where(pending => pending.State == MutationState.Pending)
                    .Select(pending => pending.View).ToArray(),
                Sequence = _eventSequence,
            };
            _subscriptions.Add(subscription, channel);
            return subscription;
        }
    }

    internal void Unsubscribe(MutationBrokerSubscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.Remove(subscription, out var channel)) return;
            channel.Writer.TryComplete();
        }
    }

    internal IReadOnlyList<PendingMutationView> Pending
    {
        get
        {
            lock (_gate) return _pendingById.Values
                .Where(pending => pending.State == MutationState.Pending)
                .Select(pending => pending.View)
                .ToArray();
        }
    }

    /// <summary>Completes with the terminal result. Cancelling ct before execution starts cancels the request.</summary>
    internal Task<MutationBrokerResult> SubmitAsync(MutationRequest request, MutationDecisionKind kind,
        TimeSpan ttl, PendingMutationView view, CancellationToken ct, Action? onAccepted = null)
    {
        ct.ThrowIfCancellationRequested();
        if (kind == MutationDecisionKind.Reject)
        {
            NotifyAccepted(onAccepted);
            return Task.FromResult(Result(MutationStatus.Disabled));
        }

        if (kind == MutationDecisionKind.Execute)
        {
            NotifyAccepted(onAccepted);
            return StartImmediateExecution(request, view.RequestId, ct);
        }

        PendingRequest? created = null;
        PendingRequest? join = null;
        MutationBrokerResult? rejection = null;
        var dispatchEvents = false;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();
            RemoveExpiredDenials(now);

            var sessionPending = _pendingById.Values.FirstOrDefault(pending =>
                pending.State == MutationState.Pending &&
                string.Equals(pending.Request.SessionKey, request.SessionKey, StringComparison.Ordinal));
            if (sessionPending is not null)
            {
                if (string.Equals(sessionPending.Request.Digest, request.Digest, StringComparison.Ordinal))
                    join = sessionPending;
                else
                    rejection = Result(MutationStatus.Busy);
            }
            else
            {
                join = _pendingById.Values.FirstOrDefault(pending => pending.State == MutationState.Pending &&
                    string.Equals(pending.Request.Digest, request.Digest, StringComparison.Ordinal));
                if (join is null && _deniedDigests.TryGetValue(request.Digest, out var deniedAt) && now - deniedAt <= DenialCooldown)
                    rejection = Result(MutationStatus.Denied);
                else if (join is null)
                {
                    NotifyAccepted(onAccepted);
                    var expiresAt = now + ttl;
                    created = new PendingRequest(request, view with { Request = request, ExpiresAt = expiresAt });
                    _pendingById.Add(view.RequestId, created);
                    dispatchEvents = QueueEventUnderLock(new MutationAdded(created.View));
                    created.Timer = time.CreateTimer(_ => Expire(created), null, ttl, Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (rejection is not null)
        {
            NotifyAccepted(onAccepted);
            return Task.FromResult(rejection);
        }
        if (join is not null) return AwaitWithCancellation(join.Completion.Task, ct);

        var pending = created!;
        if (dispatchEvents) DispatchEvents();
        pending.CancellationRegistration = ct.Register(() => Cancel(pending));
        return pending.Completion.Task;
    }

    /// <summary>False when the request is no longer Pending (expired, cancelled, decided).</summary>
    internal bool TryApprove(string requestId, string verification)
        => TryApproveWithResult(requestId, verification) == MutationApprovalResult.Accepted;

    internal MutationApprovalResult TryApproveWithResult(string requestId, string verification)
    {
        PendingRequest? approved = null;
        PendingRequest? expired = null;
        var dispatchEvents = false;
        bool busy;
        lock (_gate)
        {
            if (!_pendingById.TryGetValue(requestId, out var pending) || pending.State != MutationState.Pending)
                return MutationApprovalResult.NotPending;
            if (time.GetUtcNow() > pending.View.ExpiresAt)
            {
                if (Transition(pending, MutationState.Expired))
                {
                    expired = pending;
                    dispatchEvents = QueueEventUnderLock(new MutationRemoved(pending.View.RequestId, MutationState.Expired));
                }
                busy = false;
            }
            else if (!string.Equals(verification, "click", StringComparison.Ordinal))
                return MutationApprovalResult.NotPending;
            else if (!Transition(pending, MutationState.Approved))
                return MutationApprovalResult.NotPending;
            else
            {
                approved = pending;
                busy = !_executingProfiles.Add(pending.Request.Profile);
                if (!busy) pending.State = MutationState.Executing;
                dispatchEvents = QueueEventUnderLock(new MutationRemoved(pending.View.RequestId, MutationState.Approved));
            }
        }

        if (dispatchEvents) DispatchEvents();
        if (expired is not null) CompleteExpired(expired);
        if (approved is not null)
        {
            DisposePendingSignals(approved);
            if (busy) approved.Completion.TrySetResult(Result(MutationStatus.Busy));
            else _ = RunExecutionAsync(approved.Request, approved.View.RequestId, MutationExecutionOrigin.Operator, approved);
        }
        return approved is null ? MutationApprovalResult.NotPending : busy ? MutationApprovalResult.Busy : MutationApprovalResult.Accepted;
    }

    internal bool TryDeny(string requestId)
    {
        PendingRequest? denied = null;
        PendingRequest? expired = null;
        var dispatchEvents = false;
        lock (_gate)
        {
            if (!_pendingById.TryGetValue(requestId, out var pending) || pending.State != MutationState.Pending)
                return false;
            if (time.GetUtcNow() > pending.View.ExpiresAt)
            {
                if (Transition(pending, MutationState.Expired))
                {
                    expired = pending;
                    dispatchEvents = QueueEventUnderLock(new MutationRemoved(pending.View.RequestId, MutationState.Expired));
                }
            }
            else if (!Transition(pending, MutationState.Denied))
                return false;
            else
            {
                _deniedDigests[pending.Request.Digest] = time.GetUtcNow();
                denied = pending;
                dispatchEvents = QueueEventUnderLock(new MutationRemoved(pending.View.RequestId, MutationState.Denied));
            }
        }

        if (dispatchEvents) DispatchEvents();
        if (expired is not null) CompleteExpired(expired);
        if (denied is not null)
        {
            DisposePendingSignals(denied);
            denied.Completion.TrySetResult(Result(MutationStatus.Denied));
        }
        return denied is not null;
    }

    private Task<MutationBrokerResult> StartImmediateExecution(MutationRequest request, string requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_executingProfiles.Add(request.Profile)) return Task.FromResult(Result(MutationStatus.Busy));
        }
        return RunExecutionAsync(request, requestId, MutationExecutionOrigin.Automatic, null);
    }

    private async Task<MutationBrokerResult> RunExecutionAsync(
        MutationRequest request,
        string requestId,
        MutationExecutionOrigin origin,
        PendingRequest? pending)
    {
        MutationBrokerResult result;
        try
        {
            result = await execute(request, new MutationExecutionContext(requestId, origin), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = new MutationBrokerResult(MutationStatus.Failed, exception.Message, null);
        }
        finally
        {
            lock (_gate) _executingProfiles.Remove(request.Profile);
        }

        if (pending is not null)
        {
            lock (_gate) pending.State = StateFor(result.Status);
            pending.Completion.TrySetResult(result);
        }
        return result;
    }

    private void Expire(PendingRequest pending)
    {
        var dispatchEvents = false;
        lock (_gate)
        {
            if (pending.State != MutationState.Pending || time.GetUtcNow() < pending.View.ExpiresAt) return;
            if (!Transition(pending, MutationState.Expired)) return;
            dispatchEvents = QueueEventUnderLock(new MutationRemoved(pending.View.RequestId, MutationState.Expired));
        }
        if (dispatchEvents) DispatchEvents();
        CompleteExpired(pending);
    }

    private void Cancel(PendingRequest pending)
    {
        var dispatchEvents = false;
        lock (_gate)
        {
            if (!Transition(pending, MutationState.Cancelled)) return;
            dispatchEvents = QueueEventUnderLock(new MutationRemoved(pending.View.RequestId, MutationState.Cancelled));
        }
        if (dispatchEvents) DispatchEvents();
        DisposePendingSignals(pending);
        pending.Completion.TrySetCanceled();
    }

    private bool Transition(PendingRequest pending, MutationState state)
    {
        if (pending.State != MutationState.Pending) return false;
        pending.State = state;
        _pendingById.Remove(pending.View.RequestId);
        return true;
    }

    private void CompleteExpired(PendingRequest pending)
    {
        DisposePendingSignals(pending);
        pending.Completion.TrySetResult(Result(MutationStatus.Timeout));
    }

    private bool QueueEventUnderLock(BrokerEvent change)
    {
        _events.Enqueue(new MutationEventEnvelope(++_eventSequence, change));
        if (_dispatchingEvents) return false;
        _dispatchingEvents = true;
        return true;
    }

    private void DispatchEvents()
    {
        while (true)
        {
            MutationEventEnvelope envelope;
            Delegate[]? handlers;
            lock (_gate)
            {
                if (_events.Count == 0)
                {
                    _dispatchingEvents = false;
                    return;
                }
                envelope = _events.Dequeue();
                handlers = Changed?.GetInvocationList();
                foreach (var (subscription, channel) in _subscriptions)
                    if (envelope.Sequence > subscription.Sequence)
                        channel.Writer.TryWrite(envelope);
            }

            if (handlers is null) continue;
            foreach (var handler in handlers)
            {
                try { ((Action<BrokerEvent>)handler)(envelope.Event); }
                catch { }
            }
        }
    }

    private static void DisposePendingSignals(PendingRequest pending)
    {
        pending.Timer?.Dispose();
        pending.CancellationRegistration.Unregister();
    }

    private void RemoveExpiredDenials(DateTimeOffset now)
    {
        foreach (var digest in _deniedDigests.Where(pair => now - pair.Value > DenialCooldown).Select(pair => pair.Key).ToArray())
            _deniedDigests.Remove(digest);
    }

    private static Task<MutationBrokerResult> AwaitWithCancellation(Task<MutationBrokerResult> task, CancellationToken ct) =>
        ct.CanBeCanceled ? task.WaitAsync(ct) : task;

    private static void NotifyAccepted(Action? callback)
    {
        try { callback?.Invoke(); }
        catch { /* Journal failure must not affect mutation decisions. */ }
    }

    private static MutationBrokerResult Result(string status) => new(status, null, null);

    private static MutationState StateFor(string status) => status switch
    {
        MutationStatus.Success => MutationState.Succeeded,
        MutationStatus.OutcomeUnknown => MutationState.OutcomeUnknown,
        _ => MutationState.Failed,
    };

    private sealed class PendingRequest(MutationRequest request, PendingMutationView view)
    {
        internal MutationRequest Request { get; } = request;
        internal PendingMutationView View { get; } = view;
        internal MutationState State { get; set; } = MutationState.Pending;
        internal TaskCompletionSource<MutationBrokerResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ITimer? Timer { get; set; }
        internal CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}