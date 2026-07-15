using SqlHarness.Core.Auth;

namespace SqlHarness.Tests.Auth;

public sealed class AzureCliTests
{
    [Fact]
    public async Task RunJsonAsync_NonzeroExitUsesRedactedException()
    {
        var sensitiveArgument = $"arg-{Guid.NewGuid():N}";
        var sensitiveError = $"stderr-{Guid.NewGuid():N}";
        var cli = new AzureCli(new StubProcessRunner(new ProcessResult(23, "", sensitiveError)));

        var exception = await Assert.ThrowsAsync<AzureCliException>(() =>
            cli.RunJsonAsync(["query", sensitiveArgument]));

        Assert.Equal("Azure CLI command failed with exit code 23.", exception.Message);
        Assert.DoesNotContain(sensitiveArgument, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveError, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunJsonAsync_MalformedJsonUsesSanitizedException()
    {
        var sensitiveOutput = $"not-json-{Guid.NewGuid():N}";
        var cli = new AzureCli(new StubProcessRunner(new ProcessResult(0, sensitiveOutput, "")));

        var exception = await Assert.ThrowsAsync<AzureCliException>(() =>
            cli.RunJsonAsync(["query"]));

        Assert.Equal("Azure CLI returned invalid JSON.", exception.Message);
        Assert.DoesNotContain(sensitiveOutput, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, new[] { "/c", "az", "account", "show" })]
    [InlineData(false, new[] { "account", "show" })]
    public void BuildArguments_UsesExpectedPlatformShim(bool isWindows, string[] expected)
    {
        Assert.Equal(expected, AzureCli.BuildArguments(["account", "show"], isWindows));
    }

    private sealed class StubProcessRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
