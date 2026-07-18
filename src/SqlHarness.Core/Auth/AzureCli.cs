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
        CancellationToken cancellationToken = default);
}

internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start process.");

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

public sealed class AzureCli : IAzureCli
{
    private readonly IProcessRunner _runner;
    private readonly bool _isWindows;

    public AzureCli() : this(new ProcessRunner(), OperatingSystem.IsWindows())
    {
    }

    internal AzureCli(IProcessRunner runner) : this(runner, OperatingSystem.IsWindows())
    {
    }

    internal AzureCli(IProcessRunner runner, bool isWindows)
    {
        _runner = runner;
        _isWindows = isWindows;
    }

    private string Executable => _isWindows ? "cmd.exe" : "az";

    public async Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            Executable,
            BuildArguments(["account", "show", "-o", "json"], _isWindows),
            cancellationToken: cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<JsonElement> RunJsonAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var fullArgs = new List<string>(args) { "-o", "json" };
        var result = await _runner.RunAsync(
            Executable,
            BuildArguments(fullArgs, _isWindows),
            cancellationToken: cancellationToken);
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

    internal static IReadOnlyList<string> BuildArguments(IEnumerable<string> args, bool isWindows)
    {
        if (!isWindows)
            return args.ToList();

        var validatedArgs = args.ToList();
        if (validatedArgs.Any(IsUnsafeForWindowsCommand))
        {
            throw new AzureCliException(
                "Azure CLI argument is unsafe for Windows command execution.");
        }

        var fullArgs = new List<string> { "/c", "az" };
        fullArgs.AddRange(validatedArgs);
        return fullArgs;
    }

    private static bool IsUnsafeForWindowsCommand(string argument) =>
        argument.Any(char.IsWhiteSpace) ||
        argument.IndexOfAny(['"', '\'', '&', '|', '<', '>', '^', '%', '!', '(', ')']) >= 0;
}