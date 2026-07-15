using System.Diagnostics;
using System.Text.Json;

namespace SqlHarness.Core.Auth;

public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
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
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }
}

public sealed class AzureCli(IProcessRunner runner) : IAzureCli
{
    private static string Executable => OperatingSystem.IsWindows() ? "cmd.exe" : "az";

    public async Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            Executable,
            BuildArguments(["account", "show", "-o", "json"]),
            cancellationToken: cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<JsonElement> RunJsonAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var fullArgs = new List<string>(args) { "-o", "json" };
        var result = await runner.RunAsync(
            Executable,
            BuildArguments(fullArgs),
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            var error = result.StandardError.Trim();
            throw new AzureCliException(
                $"az {string.Join(' ', args)} failed: {(error.Length > 0 ? error : "exit " + result.ExitCode)}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<string> BuildArguments(IEnumerable<string> args)
    {
        if (!OperatingSystem.IsWindows())
            return args.ToList();

        var fullArgs = new List<string> { "/c", "az" };
        fullArgs.AddRange(args);
        return fullArgs;
    }
}
