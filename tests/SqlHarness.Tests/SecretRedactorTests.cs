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
    public void Short_known_passwords_are_still_redacted()
    {
        var safe = SecretRedactor.Redact("Password=C;", ["C"]);

        Assert.Contains("Password=[REDACTED]", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=C", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_preserves_target_and_user_fields_in_a_connection_string()
    {
        var safe = SecretRedactor.Redact(
            "Data Source=sql.example.test,1433;Initial Catalog=Sales;User ID=app_user;Password=\"p;w\"\"x\";Server=alt;Database=db2;uid=u2;pwd=p2",
            []);

        Assert.Equal(
            "Data Source=sql.example.test,1433;Initial Catalog=Sales;User ID=app_user;Password=[REDACTED];Server=alt;Database=db2;uid=u2;pwd=[REDACTED]",
            safe);
    }

    [Theory]
    [InlineData("Password='it''s;secret';Server=s", "Password=[REDACTED];Server=s")]
    [InlineData("Password=plain;Database=d", "Password=[REDACTED];Database=d")]
    public void Redact_removes_quoted_and_unquoted_passwords_but_keeps_neighbours(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.Redact(input, []));
    }

    [Theory]
    [InlineData("access_token=abc123", "access_token=[REDACTED]")]
    [InlineData("access-token: abc123", "access-token: [REDACTED]")]
    [InlineData("Access Token=abc123;", "Access Token=[REDACTED];")]
    [InlineData("accessToken=abc123", "accessToken=[REDACTED]")]
    [InlineData("{\"access_token\":\"abc123\"}", "{\"access_token\":\"[REDACTED]\"}")]
    [InlineData("password: abc123", "password: [REDACTED]")]
    [InlineData("{\"password\":\"abc123\"}", "{\"password\":\"[REDACTED]\"}")]
    [InlineData("{\"pwd\": \"abc123\", \"server\": \"s1\"}", "{\"pwd\": \"[REDACTED]\", \"server\": \"s1\"}")]
    [InlineData("Password=abc123", "Password=[REDACTED]")]
    [InlineData("{\"password\": \"a b c\"}", "{\"password\": \"[REDACTED]\"}")]
    [InlineData("client_secret='ab;cd'", "client_secret='[REDACTED]'")]
    [InlineData("access_token=\"ab cd\"", "access_token=\"[REDACTED]\"")]
    [InlineData("password: 'it''s x'", "password: '[REDACTED]'")]
    [InlineData("{\\\"password\\\":\\\"abc\\\"}", "{\\\"password\\\":\\\"[REDACTED]\\\"}")]
    [InlineData("client_secret=abc123&x=1", "client_secret=[REDACTED]&x=1")]
    [InlineData("Client Secret=abc123", "Client Secret=[REDACTED]")]
    public void Redact_masks_values_of_explicit_authentication_secret_fields(string input, string expected)
    {
        var safe = SecretRedactor.Redact(input, []);

        Assert.Equal(expected, safe);
        Assert.DoesNotContain("abc123", safe, StringComparison.Ordinal);
    }

    // A truncated diagnostic can cut a quoted value before its closing quote; the rest of the line is masked.
    [Theory]
    [InlineData("access_token=\"abc def", "access_token=\"[REDACTED]")]
    [InlineData("password: \"abc", "password: \"[REDACTED]")]
    [InlineData("client_secret='abc;def\nServer=s1", "client_secret='[REDACTED]\nServer=s1")]
    [InlineData("{\\\"pwd\\\":\\\"abc", "{\\\"pwd\\\":\\\"[REDACTED]")]
    [InlineData("{\\\"password\\\":\\\"a\\\\\\\"b\\\"}", "{\\\"password\\\":\\\"[REDACTED]")]
    public void Redact_masks_an_unterminated_quoted_secret_field_value_to_the_end_of_the_line(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.Redact(input, []));
    }

    [Fact]
    public void Redact_masks_a_jwt_without_a_field_label()
    {
        var safe = SecretRedactor.Redact("failed with eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.sig here", []);

        Assert.Equal("failed with [REDACTED] here", safe);
    }

    [Theory]
    [InlineData("use the --access-token option")]
    [InlineData("access-token-provider returned no value")]
    [InlineData("az account get-access-token --resource https://database.windows.net/")]
    [InlineData("Azure CLI returned no SQL access token.")]
    [InlineData("environment variable SQLHARNESS_PASSWORD is not set")]
    [InlineData("see C:\\work\\password-notes\\Server.sql")]
    public void Redact_keeps_labels_and_explanatory_words(string input)
    {
        Assert.Equal(input, SecretRedactor.Redact(input, []));
    }

    [Fact]
    public void Redact_scrubs_one_character_and_overlapping_registered_credentials_without_a_label()
    {
        // Registered credentials of any length win over target readability, so "sql.e[REDACTED]ample.test" is intended.
        var safe = SecretRedactor.Redact("login failed for sql.example.test using xy and xyz", ["x", "xyz"]);

        Assert.DoesNotContain("xyz", safe, StringComparison.Ordinal);
        Assert.Equal("login failed for sql.e[REDACTED]ample.test using [REDACTED]y and [REDACTED]", safe);
    }

    [Fact]
    public void Redact_is_idempotent_over_masked_field_values()
    {
        const string input = "{\"password\": \"a b\", \"access_token\":\"x y\"} client_secret='p;q' pwd: z";
        var once = SecretRedactor.Redact(input, []);

        Assert.Equal(once, SecretRedactor.Redact(once, []));
    }
}
