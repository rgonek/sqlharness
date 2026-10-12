using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace SqlHarness.Core.Auth;

internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null);
}

internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
        }

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start process.");
        process.StandardInput.Close();

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessTreeAsync(process);
            throw;
        }
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch
        {
            // Cleanup is best-effort and must never mask the original cancellation.
        }
    }
}

internal sealed class AzureCliLocator(
    Func<string> systemDirectory,
    Func<string?> pathVariable,
    Func<string, bool> fileExists)
{
    internal static AzureCliLocator Default { get; } = new(
        () => Environment.SystemDirectory,
        () => Environment.GetEnvironmentVariable("PATH"),
        File.Exists);

    internal string WindowsCommandProcessor => systemDirectory().TrimEnd('\\', '/') + @"\cmd.exe";

    internal string NeutralWorkingDirectory => systemDirectory();

    internal string? FindUnixAz()
    {
        var path = pathVariable();
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var entry in path.Split(':'))
        {
            // The extra POSIX-root check keeps simulated Unix resolution testable on Windows.
            var isPosixAbsoluteOnWindows = OperatingSystem.IsWindows() &&
                entry.StartsWith('/') &&
                !entry.StartsWith("//", StringComparison.Ordinal);
            if (!Path.IsPathFullyQualified(entry) && !isPosixAbsoluteOnWindows)
                continue;

            var executable = entry.TrimEnd('/') + "/az";
            if (fileExists(executable))
                return executable;
        }

        return null;
    }
}

public sealed class AzureCli : IAzureCli
{
    private readonly IProcessRunner _runner;
    private readonly bool _isWindows;
    private readonly AzureCliLocator _locator;
    private readonly TimeSpan _timeout;

    public AzureCli() : this(new ProcessRunner(), OperatingSystem.IsWindows())
    {
    }

    internal AzureCli(IProcessRunner runner) : this(runner, OperatingSystem.IsWindows())
    {
    }

    internal AzureCli(IProcessRunner runner, bool isWindows)
        : this(runner, isWindows, AzureCliLocator.Default, TimeSpan.FromSeconds(60))
    {
    }

    internal AzureCli(IProcessRunner runner, bool isWindows, AzureCliLocator locator, TimeSpan timeout)
    {
        _runner = runner;
        _isWindows = isWindows;
        _locator = locator;
        _timeout = timeout;
    }

    public async Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAzAsync(["account", "show", "-o", "json"], cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<JsonElement> RunJsonAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var fullArgs = new List<string>(args) { "-o", "json" };
        var result = await RunAzAsync(fullArgs, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new AzureCliException($"Azure CLI command failed with exit code {result.ExitCode}.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AzureCliException("Azure CLI returned invalid JSON.");
        }
    }

    private async Task<ProcessResult> RunAzAsync(
        IEnumerable<string> args,
        CancellationToken cancellationToken)
    {
        var executable = _isWindows
            ? _locator.WindowsCommandProcessor
            : _locator.FindUnixAz() ?? throw new AzureCliException("Azure CLI executable was not found on PATH.");
        var workingDirectory = _isWindows ? _locator.NeutralWorkingDirectory : "/";
        IReadOnlyDictionary<string, string>? environment = _isWindows
            ? new Dictionary<string, string> { ["NoDefaultCurrentDirectoryInExePath"] = "1" }
            : null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            return await _runner.RunAsync(
                executable,
                BuildArguments(args, _isWindows),
                workingDirectory,
                deadline.Token,
                environment);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AzureCliException("Azure CLI did not finish within the time limit.");
        }
        catch (Exception exception) when (
            exception is Win32Exception or
                FileNotFoundException or
                DirectoryNotFoundException or
                UnauthorizedAccessException or
                InvalidOperationException)
        {
            throw new AzureCliException("Azure CLI process could not be started.");
        }
    }

    internal static IReadOnlyList<string> BuildArguments(IEnumerable<string> args, bool isWindows)
    {
        if (!isWindows)
            return args.ToList();

        var fullArgs = new List<string> { "/c", "az" };
        fullArgs.AddRange(args);
        return fullArgs;
    }
}