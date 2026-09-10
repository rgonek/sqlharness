namespace SqlHarness.Core.Dialect;

internal interface ISqlDialect
{
    SqlEngine Engine { get; }
    string IdentitySql { get; }
    string PingSql { get; }
}
