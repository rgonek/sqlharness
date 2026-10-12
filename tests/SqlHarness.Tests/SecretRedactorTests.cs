using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_preserves_caller_SQL_quoted_in_a_server_error_including_an_unregistered_JWT()
    {
        const string sql = "SELECT * FROM orders WHERE password = 'caller-value' AND access_token = 'eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.sig'";
        var exception = new InvalidOperationException($"Server rejected SQL: {sql}");

        var safe = SecretRedactor.Redact(exception, []);

        Assert.Equal($"Server rejected SQL: {sql}", safe);
    }

    [Fact]
    public void Redact_replaces_only_registered_secrets_anywhere_in_an_exception()
    {
        const string password = "resolved-profile-password";
        const string token = "resolved-azure-token";
        var exception = new InvalidOperationException(
            $"Login failed for SQL: SELECT '{token}'; connection Password={password}");

        var safe = SecretRedactor.Redact(exception, [password, token]);

        Assert.Equal(
            "Login failed for SQL: SELECT '[REDACTED]'; connection Password=[REDACTED]",
            safe);
        Assert.DoesNotContain(password, safe, StringComparison.Ordinal);
        Assert.DoesNotContain(token, safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_traverses_nested_aggregate_failures_for_known_secrets()
    {
        var exception = new AggregateException(
            "Parallel failures",
            new InvalidOperationException("First safe failure"),
            new AggregateException(
                "Nested failures",
                new InvalidOperationException("Nested safe failure"),
                new InvalidOperationException("Connection failure: branch-secret")));

        var safe = SecretRedactor.Redact(exception, ["branch-secret"]);

        Assert.DoesNotContain("branch-secret", safe, StringComparison.Ordinal);
        Assert.Contains("First safe failure", safe, StringComparison.Ordinal);
        Assert.Contains("Nested safe failure", safe, StringComparison.Ordinal);
        Assert.Contains("Connection failure: [REDACTED]", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_replaces_known_secret_in_a_parameter_error_message()
    {
        var exception = new SqlHarnessSafetyException(
            "Invalid value 'private-audit-value' for SQL parameter 'n' of type 'int'.",
            new FormatException("The input string 'private-audit-value' was not in a correct format."));

        var safe = SecretRedactor.Redact(exception, ["private-audit-value"]);

        Assert.Equal("Invalid value '[REDACTED]' for SQL parameter 'n' of type 'int'.", safe);
        Assert.DoesNotContain("private-audit-value", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("n:int=private-audit-value", safe, StringComparison.Ordinal);
        Assert.Contains("'n'", safe, StringComparison.Ordinal);
        Assert.Contains("int", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_replaces_short_known_secrets_without_a_field_label()
    {
        Assert.Equal("Login failed with [REDACTED]", SecretRedactor.Redact("Login failed with C", ["C"]));
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

    [Fact]
    public void Redact_does_not_rescan_replacement_markers_for_later_secrets()
    {
        var safe = SecretRedactor.Redact("@ClientID uses D", ["C", "D"]);

        Assert.Equal("@[REDACTED]lientI[REDACTED] uses [REDACTED]", safe);
    }

    [Fact]
    public void Redact_keeps_validation_errors_at_the_safe_parameter_diagnostic()
    {
        var exception = new SqlHarnessSafetyException(
            "Invalid value for SQL parameter 'n' of type 'int'.",
            new FormatException("The input string 'private-audit-value' was not in a correct format."));

        var safe = SecretRedactor.Redact(exception, []);

        Assert.Equal("Invalid value for SQL parameter 'n' of type 'int'.", safe);
    }
}