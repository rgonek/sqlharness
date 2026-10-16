using SqlHarness.Core;
using SqlHarness.Core.Mutations;

namespace SqlHarness.Tests.Mutations;

public sealed class MutationPolicyTests
{
    [Theory]
    [InlineData(MutationMode.Never, "Dml", MutationDecisionKind.Reject, MutationStatus.Disabled)]
    [InlineData(MutationMode.Never, "Schema", MutationDecisionKind.Reject, MutationStatus.Disabled)]
    [InlineData(MutationMode.Never, "Privilege", MutationDecisionKind.Reject, MutationStatus.Disabled)]
    [InlineData(MutationMode.Never, "Opaque", MutationDecisionKind.Reject, MutationStatus.Disabled)]
    [InlineData(MutationMode.Operator, "Dml", MutationDecisionKind.AskOperator, null)]
    [InlineData(MutationMode.Operator, "Schema", MutationDecisionKind.AskOperator, null)]
    [InlineData(MutationMode.Operator, "Privilege", MutationDecisionKind.AskOperator, null)]
    [InlineData(MutationMode.Operator, "Opaque", MutationDecisionKind.AskOperator, null)]
    [InlineData(MutationMode.Yolo, "Dml", MutationDecisionKind.Execute, null)]
    [InlineData(MutationMode.Yolo, "Schema", MutationDecisionKind.AskOperator, null)]
    [InlineData(MutationMode.Yolo, "Privilege", MutationDecisionKind.AskOperator, null)]
    [InlineData(MutationMode.Yolo, "Opaque", MutationDecisionKind.AskOperator, null)]
    public void Decide_applies_mode_and_risk_policy(
        MutationMode mode, string tierName, MutationDecisionKind kind, string? status)
    {
        var tier = Enum.Parse<SqlRiskTier>(tierName);
        Assert.Equal((kind, status), MutationPolicy.Decide(mode, tier));
    }
}
