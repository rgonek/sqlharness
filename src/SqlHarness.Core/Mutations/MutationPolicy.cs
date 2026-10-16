namespace SqlHarness.Core.Mutations;

public enum MutationMode { Never, Operator, Yolo }
public enum MutationVerification { Click }

public static class MutationStatus
{
    public const string Success = "success";
    public const string Disabled = "mutations_disabled";
    public const string Unavailable = "approval_unavailable";
    public const string Timeout = "approval_timeout";
    public const string Denied = "denied_by_operator";
    public const string Busy = "busy";
    public const string NotAMutation = "not_a_mutation";
    public const string DatabaseMismatch = "database_mismatch";
    public const string PrincipalProtected = "principal_protected";
    public const string Failed = "failed";
    public const string OutcomeUnknown = "outcome_unknown";
}

public enum MutationDecisionKind { Reject, Execute, AskOperator }

internal static class MutationPolicy
{
    internal static (MutationDecisionKind Kind, string? Status) Decide(MutationMode mode, SqlRiskTier tier) => mode switch
    {
        MutationMode.Never => (MutationDecisionKind.Reject, MutationStatus.Disabled),
        MutationMode.Yolo when tier == SqlRiskTier.Dml => (MutationDecisionKind.Execute, null),
        _ => (MutationDecisionKind.AskOperator, null),
    };
}
