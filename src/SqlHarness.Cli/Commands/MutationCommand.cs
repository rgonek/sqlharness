using System.ComponentModel;

using Spectre.Console.Cli;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;
using SqlHarness.Core.Mutations;

namespace SqlHarness.Cli.Commands;

public sealed class MutationCommand(
    ISqlHarnessModule module,
    OutputContext output,
    Renderer renderer,
    CliInput input,
    IMutationBrokerClient broker,
    McpHostConsole console,
    int approvalTimeoutSeconds,
    string sessionKey,
    SqlHarnessConfig config) : SqlHarnessCommand<MutationCommand.Settings>(module, output, renderer, config)
{
    public sealed class Settings : TargetSettings
    {
        [CommandOption("--file <PATH>")]
        public string? File { get; set; }

        [Description("Bind name[[:type]]=value. Types: nvarchar, nvarchar(max), varchar, varchar(max), char, nchar, int, bigint, smallint, tinyint, bit, decimal, decimal(p,s), numeric, numeric(p,s), float, real, date, time, datetime, datetime2, datetimeoffset, uniqueidentifier, varbinary, varbinary(max), varbinary(n), binary(n). SQL Server only: money, smallmoney, smalldatetime, hierarchyid, geography, geometry. Null: name:null or name:type:null.")]
        [CommandOption("--param <VALUE>")]
        public string[] Parameters { get; set; } = [];

        [Description("SQL execution timeout in seconds (1..300); defaults to execution.defaultTimeoutSeconds from config.json.")][CommandOption("--timeout <SECONDS>")]
        public int? Timeout { get; set; }

        [Description("Presentation-only row cap (0..500). The server still computes the full result; canonical hashes cover all rows.")]
        [CommandOption("--max-rows <COUNT>")][DefaultValue(50)]
        public int MaxRows { get; set; } = 50;

        [CommandOption("--expect-database <DATABASE>")]
        public string? ExpectDatabase { get; set; }

        [Description("Check the exact arguments offline and stop before connecting or submitting an approval request.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!settings.TryTarget(out var target, out var targetError))
            return Invalid(targetError);
        if (settings.Json && settings.Output is not null)
            return Invalid("Choose only one of --output, --json, or --json-summary.");
        var timeout = settings.Timeout ?? DefaultExecutionTimeoutSeconds;
        if (timeout is < 1 or > 300 || settings.MaxRows is < 0 or > 500)
            return Invalid("--timeout must be 1..300 and --max-rows must be 0..500.");
        var hasFile = !string.IsNullOrWhiteSpace(settings.File);
        if (!hasFile && !input.StdinRedirected)
            return Invalid("Provide exactly one SQL source: --file or redirected stdin.");

        try
        {
            var sql = hasFile
                ? await SqlInputReader.ReadFileAsync(settings.File!, ct)
                : await SqlInputReader.ReadStdinAsync(input.Stdin, ct);
            var parameters = settings.Parameters.Select(ParseParameter).ToArray();
            var operation = new SqlHarnessMutationOperation(
                target, sql, parameters, timeout, settings.MaxRows, settings.ExpectDatabase)
            {
                DryRun = settings.DryRun,
                SessionKey = sessionKey,
            };
            var mode = ResolveOutputMode(settings.Json, output: settings.Output);
            var agentOptions = new AgentOutputOptions(settings.MaxOutputBytes, settings.MaxCellChars);
            if (ValidateAgentOutputOptions(mode, agentOptions) is int invalidOutputOptions)
                return invalidOutputOptions;
            var mark = Output.Begin();
            var outcome = await Module.ExecuteAsync(operation, ct);

            if (outcome.Report is SqlHarnessMutationPrepared prepared && !settings.DryRun)
            {
                var brokerResult = await SubmitWithProgressAsync(prepared.Request, ct);
                var terminal = new SqlHarnessOutcome(ExitCode(brokerResult.Status), brokerResult,
                    brokerResult.Message, Error: brokerResult.Status == MutationStatus.Success
                        ? null
                        : new SqlHarnessError(brokerResult.Status, "mutation", brokerResult.Message ?? brokerResult.Status))
                {
                    OperationRef = outcome.OperationRef,
                    ParentOperationRef = outcome.ParentOperationRef,
                };
                Renderer.RenderMutation(terminal, brokerResult, mode, Output.Capture, "mutation", agentOptions);
                Output.Capture.Flush();
                return await Output.CompleteAsync(terminal, mark, ct);
            }

            if (outcome.Report is MutationBrokerResult preflightResult)
                Renderer.RenderMutation(outcome, preflightResult, mode, Output.Capture, "mutation", agentOptions);
            else if (mode == OutputMode.Agent)
                Renderer.RenderAgent(outcome, "mutation", Output.Capture, agentOptions);
            else
                Renderer.Render(outcome, mode, Output.Capture, "mutation");
            Output.Capture.Flush();
            return await Output.CompleteAsync(outcome, mark, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlInputTooLargeException)
        {
            return InvalidInputTooLarge();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid("Unable to read SQL input file.",
                new SqlHarnessError("input_file_unavailable", "input", "Unable to read SQL input file."));
        }
        catch (FormatException exception)
        {
            return Invalid(exception.Message);
        }
    }

    private async Task<MutationBrokerResult> SubmitWithProgressAsync(MutationRequest request, CancellationToken ct)
    {
        console.Error.WriteLine($"Waiting for operator approval in the dashboard (up to {approvalTimeoutSeconds} s)…");
        console.Error.Flush();
        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = RepeatProgressAsync(progressCancellation.Token);
        try
        {
            return await broker.SubmitAsync(request, progress: null, ct: ct);
        }
        finally
        {
            progressCancellation.Cancel();
            await progressTask.ConfigureAwait(false);
        }
    }

    private async Task RepeatProgressAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                console.Error.WriteLine($"Waiting for operator approval in the dashboard (up to {approvalTimeoutSeconds} s)…");
                console.Error.Flush();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static SqlHarnessParameterInput ParseParameter(string input)
    {
        var equals = input.IndexOf('=');
        string declaration;
        string? value;
        if (equals >= 0)
        {
            declaration = input[..equals];
            value = input[(equals + 1)..];
        }
        else
        {
            var colon = input.LastIndexOf(':');
            if (colon <= 0 || input[(colon + 1)..] != "null")
                throw new FormatException("SQL parameter must use name=value, name:type=value, name:null, or name:type:null syntax.");
            declaration = input[..colon];
            value = null;
        }

        var typeSeparator = declaration.IndexOf(':');
        return typeSeparator < 0
            ? new SqlHarnessParameterInput(declaration, null, value)
            : new SqlHarnessParameterInput(declaration[..typeSeparator], declaration[(typeSeparator + 1)..], value);
    }

    private static SqlHarnessExitCode ExitCode(string status) => status switch
    {
        MutationStatus.Success => SqlHarnessExitCode.Success,
        MutationStatus.DatabaseMismatch => SqlHarnessExitCode.TargetMismatch,
        MutationStatus.Failed or MutationStatus.OutcomeUnknown => SqlHarnessExitCode.SqlExecution,
        _ => SqlHarnessExitCode.Safety,
    };
}
