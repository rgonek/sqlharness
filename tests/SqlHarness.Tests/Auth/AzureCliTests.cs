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

    [Fact]
    public async Task RunJsonAsync_NonWindowsPassesUnsafeArgumentToRunner()
    {
        var runner = new TrackingProcessRunner();
        var locator = new AzureCliLocator(() => "", () => "/opt/az/bin", path => path == "/opt/az/bin/az");
        var cli = new AzureCli(runner, isWindows: false, locator, TimeSpan.FromSeconds(1));

        await cli.RunJsonAsync(["two words"]);

        Assert.Equal(1, runner.InvocationCount);
        Assert.Equal("/opt/az/bin/az", runner.FileName);
        Assert.Equal(["two words", "-o", "json"], runner.Arguments);
    }

    [Fact]
    public async Task Windows_uses_absolute_system_cmd_and_neutral_working_directory()
    {
        const string systemDirectory = @"C:\Windows\System32";
        var runner = new RecordingProcessRunner(new ProcessResult(0, "{}", ""));
        var locator = new AzureCliLocator(
            () => systemDirectory,
            () => null,
            _ => false);
        var cli = new AzureCli(runner, isWindows: true, locator, TimeSpan.FromSeconds(1));

        await cli.RunJsonAsync(["account", "show"]);

        Assert.Equal(@"C:\Windows\System32\cmd.exe", runner.FileName);
        Assert.Equal(systemDirectory, runner.WorkingDirectory);
        Assert.Equal("1", runner.Environment?["NoDefaultCurrentDirectoryInExePath"]);
    }

    [Fact]
    public async Task Unix_resolves_az_from_absolute_PATH_entries_only()
    {
        var runner = new RecordingProcessRunner(new ProcessResult(0, "{}", ""));
        var locator = new AzureCliLocator(
            () => "",
            () => string.Join(':', "", ".", "relative/bin", "/opt/az/bin", "/usr/bin"),
            path => path is "/opt/az/bin/az" or "./az");
        var cli = new AzureCli(runner, isWindows: false, locator, TimeSpan.FromSeconds(1));

        await cli.RunJsonAsync(["account", "show"]);

        Assert.Equal("/opt/az/bin/az", runner.FileName);
    }

    [Fact]
    public async Task Unix_preserves_backslash_at_end_of_absolute_PATH_entry()
    {
        var runner = new RecordingProcessRunner(new ProcessResult(0, "{}", ""));
        var locator = new AzureCliLocator(
            () => "",
            () => "/opt/az/bin\\",
            path => path == "/opt/az/bin\\/az");
        var cli = new AzureCli(runner, isWindows: false, locator, TimeSpan.FromSeconds(1));

        await cli.RunJsonAsync(["account", "show"]);

        Assert.Equal("/opt/az/bin\\/az", runner.FileName);
    }

    [Fact]
    public async Task Unix_missing_az_throws_AzureCliException()
    {
        var runner = new RecordingProcessRunner(new ProcessResult(0, "{}", ""));
        var locator = new AzureCliLocator(() => "", () => "/opt/az/bin", _ => false);
        var cli = new AzureCli(runner, isWindows: false, locator, TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AzureCliException>(() =>
            cli.RunJsonAsync(["account", "show"]));

        Assert.Equal("Azure CLI executable was not found on PATH.", exception.Message);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task RunJsonAsync_LaunchFailureUsesRedactedException()
    {
        const string sensitiveLaunchError = "Could not launch C:\\private\\az.exe with token=secret";
        var runner = new LaunchFailingProcessRunner(new System.ComponentModel.Win32Exception(sensitiveLaunchError));
        var locator = new AzureCliLocator(() => "", () => "/opt/az/bin", _ => true);
        var cli = new AzureCli(runner, isWindows: false, locator, TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<AzureCliException>(() =>
            cli.RunJsonAsync(["account", "show"]));

        Assert.Equal("Azure CLI process could not be started.", exception.Message);
        Assert.DoesNotContain(sensitiveLaunchError, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private\\az.exe", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_kills_and_throws_AzureCliException()
    {
        var runner = new DelayingProcessRunner();
        var locator = new AzureCliLocator(() => "", () => "/opt/az/bin", _ => true);
        var cli = new AzureCli(runner, isWindows: false, locator, TimeSpan.FromMilliseconds(200));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<AzureCliException>(() =>
            cli.RunJsonAsync(["account", "show"]));

        Assert.Equal("Azure CLI did not finish within the time limit.", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(runner.CancellationObserved);
    }

    [Fact]
    public async Task Caller_cancellation_still_surfaces_as_OperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new DelayingProcessRunner();
        var locator = new AzureCliLocator(() => "", () => "/opt/az/bin", _ => true);
        var cli = new AzureCli(runner, isWindows: false, locator, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cli.RunJsonAsync(["account", "show"], cancellation.Token));
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
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            InvocationCount++;
            FileName = fileName;
            Arguments = arguments.ToArray();
            return Task.FromResult(new ProcessResult(0, "{}", ""));
        }
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
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) => Task.FromResult(result);
    }

    private sealed class RecordingProcessRunner(ProcessResult result) : IProcessRunner
    {
        public int InvocationCount { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }
        public string? WorkingDirectory { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            InvocationCount++;
            FileName = fileName;
            Arguments = arguments.ToArray();
            Environment = environment;
            WorkingDirectory = workingDirectory;
            return Task.FromResult(result);
        }
    }

    private sealed class DelayingProcessRunner : IProcessRunner
    {
        public bool CancellationObserved { get; private set; }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            return new ProcessResult(0, "{}", "");
        }
    }

    private sealed class LaunchFailingProcessRunner(Exception exception) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) => Task.FromException<ProcessResult>(exception);
    }
}
