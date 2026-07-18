using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_removes_SQL_password_connection_fragment_from_nested_exception()
    {
        var exception = new Exception(
            "Connection failed",
            new InvalidOperationException("Data Source=safe;User ID=app;Password=sql-secret;Database=db;"));

        var safe = SecretRedactor.Redact(exception, []);

        Assert.DoesNotContain("sql-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=sql-secret", safe, StringComparison.Ordinal);
        Assert.Contains("Password=[REDACTED]", safe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redact_removes_password_from_a_non_first_nested_aggregate_branch()
    {
        var exception = new AggregateException(
            "Parallel failures",
            new InvalidOperationException("First safe failure"),
            new AggregateException(
                "Nested failures",
                new InvalidOperationException("Nested safe failure"),
                new InvalidOperationException("Connection string: Password=branch-secret;Database=db;")));

        var safe = SecretRedactor.Redact(exception, []);

        Assert.DoesNotContain("branch-secret", safe, StringComparison.Ordinal);
        Assert.Contains("Password=[REDACTED]", safe, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            safe.Split("Password=[REDACTED]", StringSplitOptions.None).Length > 2,
            "The aggregate branch must be traversed separately from AggregateException.Message.");
        Assert.Contains("First safe failure", safe, StringComparison.Ordinal);
        Assert.Contains("Nested safe failure", safe, StringComparison.Ordinal);
    }
}