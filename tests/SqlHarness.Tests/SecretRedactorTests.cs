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

    [Fact]
    public void Redact_does_not_publish_a_validation_inner_exception()
    {
        var exception = new SqlHarnessSafetyException(
            "Invalid value for SQL parameter 'n' of type 'int'.",
            new FormatException("The input string 'private-audit-value' was not in a correct format."));

        var safe = SecretRedactor.Redact(exception, []);

        Assert.Equal("Invalid value for SQL parameter 'n' of type 'int'.", safe);
        Assert.DoesNotContain("private-audit-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("input string", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("n:int=private-audit-value", safe, StringComparison.Ordinal);
        Assert.Contains("'n'", safe, StringComparison.Ordinal);
        Assert.Contains("int", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_removes_overlapping_values_longest_first()
    {
        var safe = SecretRedactor.Redact(
            "rejected private-audit-value and private-audit",
            ["private-audit", "private-audit-value"]);

        Assert.Equal("rejected [REDACTED] and [REDACTED]", safe);
        Assert.DoesNotContain("private-audit", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("-value", safe, StringComparison.Ordinal);
    }
}