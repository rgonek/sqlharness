using SqlHarness.Core.Mutations;

namespace SqlHarness.Dashboard.Mutations;

internal enum MutationState { Pending, Approved, Denied, Expired, Cancelled, Executing, Succeeded, Failed, OutcomeUnknown }

internal sealed record PendingMutationView(string RequestId, MutationRequest Request, string Environment, bool WriterSameAsReader,
    DateTimeOffset ExpiresAt);

internal abstract record BrokerEvent;
internal sealed record MutationAdded(PendingMutationView View) : BrokerEvent;
internal sealed record MutationRemoved(string RequestId, MutationState State) : BrokerEvent;

internal sealed class MutationBroker(TimeProvider time, Func<MutationRequest, CancellationToken, Task<MutationBrokerResult>> execute)
{
    private static readonly TimeSpan DenialCooldown = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingRequest> _pendingById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _deniedDigests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _executingProfiles = new(StringComparer.Ordinal);

    internal event Action<BrokerEvent>? Changed;

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
        TimeSpan ttl, PendingMutationView view, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (kind == MutationDecisionKind.Reject)
            return Task.FromResult(Result(MutationStatus.Disabled));

        if (kind == MutationDecisionKind.Execute)
            return StartImmediateExecution(request, ct);

        PendingRequest? created = null;
        PendingRequest? join = null;
        MutationBrokerResult? rejection = null;
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
                    var expiresAt = now + ttl;
                    created = new PendingRequest(request, view with { Request = request, ExpiresAt = expiresAt });
                    _pendingById.Add(view.RequestId, created);
                    created.Timer = time.CreateTimer(_ => Expire(created), null, ttl, Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (rejection is not null) return Task.FromResult(rejection);
        if (join is not null) return AwaitWithCancellation(join.Completion.Task, ct);

        var pending = created!;
        Changed?.Invoke(new MutationAdded(pending.View));
        pending.CancellationRegistration = ct.Register(() => Cancel(pending));
        return pending.Completion.Task;
    }

    /// <summary>False when the request is no longer Pending (expired, cancelled, decided).</summary>
    internal bool TryApprove(string requestId, string verification)
    {
        PendingRequest? approved = null;
        PendingRequest? expired = null;
        bool busy;
        lock (_gate)
        {
            if (!_pendingById.TryGetValue(requestId, out var pending) || pending.State != MutationState.Pending)
                return false;
            if (time.GetUtcNow() > pending.View.ExpiresAt)
            {
                if (Transition(pending, MutationState.Expired)) expired = pending;
                busy = false;
            }
            else if (!string.Equals(verification, "click", StringComparison.Ordinal))
                return false;
            else if (!Transition(pending, MutationState.Approved))
                return false;
            else
            {
                approved = pending;
                busy = !_executingProfiles.Add(pending.Request.Profile);
                if (!busy) pending.State = MutationState.Executing;
            }
        }

        if (expired is not null) CompleteExpired(expired);
        if (approved is not null)
        {
            Changed?.Invoke(new MutationRemoved(approved.View.RequestId, MutationState.Approved));
            DisposePendingSignals(approved);
            if (busy) approved.Completion.TrySetResult(Result(MutationStatus.Busy));
            else _ = RunExecutionAsync(approved);
        }
        return approved is not null && !busy;
    }

    internal bool TryDeny(string requestId)
    {
        PendingRequest? denied = null;
        PendingRequest? expired = null;
        lock (_gate)
        {
            if (!_pendingById.TryGetValue(requestId, out var pending) || pending.State != MutationState.Pending)
                return false;
            if (time.GetUtcNow() > pending.View.ExpiresAt)
            {
                if (Transition(pending, MutationState.Expired)) expired = pending;
            }
            else if (!Transition(pending, MutationState.Denied))
                return false;
            else
            {
                _deniedDigests[pending.Request.Digest] = time.GetUtcNow();
                denied = pending;
            }
        }

        if (expired is not null) CompleteExpired(expired);
        if (denied is not null)
        {
            Changed?.Invoke(new MutationRemoved(denied.View.RequestId, MutationState.Denied));
            DisposePendingSignals(denied);
            denied.Completion.TrySetResult(Result(MutationStatus.Denied));
        }
        return denied is not null;
    }

    private Task<MutationBrokerResult> StartImmediateExecution(MutationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_executingProfiles.Add(request.Profile)) return Task.FromResult(Result(MutationStatus.Busy));
        }
        return RunExecutionAsync(request, null);
    }

    private async Task<MutationBrokerResult> RunExecutionAsync(PendingRequest pending) =>
        await RunExecutionAsync(pending.Request, pending).ConfigureAwait(false);

    private async Task<MutationBrokerResult> RunExecutionAsync(MutationRequest request, PendingRequest? pending)
    {
        MutationBrokerResult result;
        try
        {
            result = await execute(request, CancellationToken.None).ConfigureAwait(false);
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
        lock (_gate)
        {
            if (pending.State != MutationState.Pending || time.GetUtcNow() < pending.View.ExpiresAt) return;
            if (!Transition(pending, MutationState.Expired)) return;
        }
        CompleteExpired(pending);
    }

    private void Cancel(PendingRequest pending)
    {
        lock (_gate)
        {
            if (!Transition(pending, MutationState.Cancelled)) return;
        }
        Changed?.Invoke(new MutationRemoved(pending.View.RequestId, MutationState.Cancelled));
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
        Changed?.Invoke(new MutationRemoved(pending.View.RequestId, MutationState.Expired));
        DisposePendingSignals(pending);
        pending.Completion.TrySetResult(Result(MutationStatus.Timeout));
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
