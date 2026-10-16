namespace SqlHarness.Core.Mutations;

public interface IMutationBrokerClient
{
    /// <summary>Submits and waits for the terminal result, reporting brief progress periodically.</summary>
    Task<MutationBrokerResult> SubmitAsync(MutationRequest request, IProgress<string>? progress, CancellationToken ct);
}

public sealed record MutationBrokerResult(string Status, string? Message, SqlHarnessQueryReport? Result);

/// <summary>Module result for an unapproved, non-dry-run mutation.</summary>
public sealed record SqlHarnessMutationPrepared(MutationRequest Request);
