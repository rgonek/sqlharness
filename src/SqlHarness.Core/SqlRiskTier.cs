namespace SqlHarness.Core;

/// <summary>Risk of a batch's persistent effects; a batch takes the maximum over its statements.</summary>
internal enum SqlRiskTier
{
    ReadOnly,
    Session,
    Dml,
    Schema,
    Privilege,
    Opaque,
}

internal static class SqlRiskTiers
{
    internal static string Label(SqlRiskTier tier) => tier switch
    {
        SqlRiskTier.ReadOnly => "read-only",
        SqlRiskTier.Session => "session",
        SqlRiskTier.Dml => "dml",
        SqlRiskTier.Schema => "schema",
        SqlRiskTier.Privilege => "privilege",
        SqlRiskTier.Opaque => "opaque",
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    internal static SqlRiskTier Max(SqlRiskTier left, SqlRiskTier right) => left >= right ? left : right;
}