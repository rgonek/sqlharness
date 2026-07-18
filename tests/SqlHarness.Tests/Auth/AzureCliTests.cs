using SqlHarness.Core.Auth;

namespace SqlHarness.Tests.Auth;

public sealed class AzureCliTests
{
    [Fact]
    public void DefaultConstructor_IsPubliclyAvailable()
    {
        IAzureCli cli = new AzureCli();

        Assert.IsType<AzureCli>(cli);
    }

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

    [Theory]
    [InlineData("two words")]
    [InlineData("quoted\"value")]
    [InlineData("quoted'value")]
    [InlineData("left&right")]
    [InlineData("left|right")]
    [InlineData("left<right")]
    [InlineData("left>right")]
    [InlineData("left^right")]
    [InlineData("left%right")]
    [InlineData("left!right")]
    [InlineData("left(right")]
    [InlineData("left)right")]
    public async Task RunJsonAsync_WindowsRejectsUnsafeArgumentBeforeExecution(string unsafeArgument)
    {
        var runner = new TrackingProcessRunner();
        var cli = new AzureCli(runner, isWindows: true);

        var exception = await Assert.ThrowsAsync<AzureCliException>(() =>
            cli.RunJsonAsync([unsafeArgument]));

        Assert.Equal("Azure CLI argument is unsafe for Windows command execution.", exception.Message);
        Assert.DoesNotContain(unsafeArgument, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunJsonAsync_NonWindowsPassesUnsafeArgumentToRunner()
    {
        var runner = new TrackingProcessRunner();
        var cli = new AzureCli(runner, isWindows: false);

        await cli.RunJsonAsync(["two words"]);

        Assert.Equal(1, runner.InvocationCount);
        Assert.Equal("az", runner.FileName);
        Assert.Equal(["two words", "-o", "json"], runner.Arguments);
    }

    [Fact]
    public void BuildArguments_WindowsPreservesSafeTokenFlowValues()
    {
        string[] args =
        [
            "account",
            "get-access-token",
            "--resource",
            "https://management.azure.com/",
            "12345678-1234-1234-1234-123456789abc",
            "name.with-dots/value:part",
        ];

        Assert.Equal(["/c", "az", .. args], AzureCli.BuildArguments(args, isWindows: true));
    }

    private sealed class StubProcessRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class TrackingProcessRunner : IProcessRunner
    {
        public int InvocationCount { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            FileName = fileName;
            Arguments = arguments.ToArray();
            return Task.FromResult(new ProcessResult(0, "{}", ""));
        }
    }
}