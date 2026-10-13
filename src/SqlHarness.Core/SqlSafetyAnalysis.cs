namespace SqlHarness.Core;

/// <summary>
/// Shared static-analysis boundary for every offline safety verdict. The
/// classifier only sees effects visible in the SQL text
/// (<c>static-visible-effects</c>); object existence and permissions never
/// resolve offline (unknown), and hidden effects beyond the AST proof
/// (functions, views, operators) are not verified. Additive and versioned:
/// clients detect meaning changes via <see cref="ContractVersion"/>.
/// Classification labels keep their existing meaning.
/// </summary>
public static class SqlSafetyAnalysis
{
    public const string AnalysisKind = "static-visible-effects";
    public const int ContractVersion = 1;
    public const bool HiddenEffectsVerified = false;
    public const string ObjectAndPermissionStatus = "unknown";
}

/// <summary>
/// Additive, versioned safety-analysis disclosure shared by the
/// capabilities and MCP surfaces. Values mirror
/// <see cref="SqlSafetyAnalysis"/>.
/// </summary>
public sealed record SqlHarnessSafetyAnalysis(
    string AnalysisKind,
    int AnalysisContractVersion,
    bool HiddenEffectsVerified,
    string ObjectAndPermissionStatus);