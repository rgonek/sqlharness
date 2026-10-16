using SqlHarness.Core.Auth;

namespace SqlHarness.Core.Mutations;

/// <summary>Proof that the broker consumed approval for exactly this digest.</summary>
public sealed class ApprovedMutation
{
    internal ApprovedMutation(string digest, AuthSpec writer)
    {
        Digest = digest;
        Writer = writer;
    }

    public string Digest { get; }
    internal AuthSpec Writer { get; }
}
