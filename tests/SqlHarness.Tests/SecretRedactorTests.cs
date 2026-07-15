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
}
