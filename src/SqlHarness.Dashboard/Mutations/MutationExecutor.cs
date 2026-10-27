using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;

namespace SqlHarness.Dashboard.Mutations;

internal enum MutationExecutionOrigin { Operator, Automatic }

internal sealed record MutationExecutionContext(string RequestId, MutationExecutionOrigin Origin);

internal sealed class MutationExecutor(
    ISqlHarnessModule module,
    Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
    Func<SqlHarnessConfig> loadConfig,
    IActivityJournal journal,
    TimeProvider time)
{
    internal Task<MutationBrokerResult> ExecuteAsync(MutationRequest request, CancellationToken ct) =>
        ExecuteAsync(request, new MutationExecutionContext(Guid.NewGuid().ToString("N"), MutationExecutionOrigin.Operator), ct);

    internal async Task<MutationBrokerResult> ExecuteAsync(
        MutationRequest request,
        MutationExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ResolvedTarget target;
        TargetProfile profile;
        try
        {
            var profiles = loadProfiles();
            if (!profiles.TryGetValue(request.Profile, out profile!))
                return Result(MutationStatus.DatabaseMismatch, "The profile no longer resolves to the approved target.");

            target = TargetResolver.Resolve(
                new SqlTargetRequest(request.Profile, request.Vars), profiles);
            if (!IdentityMatches(request, target))
                return Result(MutationStatus.DatabaseMismatch, "The profile no longer resolves to the approved target.");
        }
        catch (Exception exception)
        {
            return Result(MutationStatus.Failed, SecretRedactor.Redact(exception, []));
        }

        var modeText = profile.Mutations?.Mode ?? loadConfig().Mutations.DefaultMode;
        if (!Enum.TryParse<MutationMode>(modeText, true, out var mode))
            return Result(MutationStatus.Failed, "The mutation mode is invalid.");
        if (mode == MutationMode.Never ||
            (context.Origin == MutationExecutionOrigin.Automatic &&
             (mode != MutationMode.Yolo || !string.Equals(request.Tier, "dml", StringComparison.OrdinalIgnoreCase))))
            return Result(MutationStatus.Disabled, null);

        var reader = target.Auth;
        if (string.Equals(request.Tier, "privilege", StringComparison.OrdinalIgnoreCase))
        {
            var principal = await ResolveReaderPrincipalAsync(request, ct).ConfigureAwait(false);
            if (principal is null)
                return Result(MutationStatus.Unavailable, "Could not resolve the reader principal");
            if (request.Grantees.Any(grantee => principal.Any(name =>
                    string.Equals(grantee, name, StringComparison.OrdinalIgnoreCase))))
                return Result(MutationStatus.PrincipalProtected, null);
        }

        if (profile.Mutations?.Writer is not { } writer)
            return Result(MutationStatus.Failed, "The mutation writer is not configured.");

        var auth = writer.SameAsReader
            ? reader
            : reader with { SqlUser = writer.SqlUser, PasswordEnvVar = writer.PasswordEnvVar };
        if (auth.Strategy == AuthStrategy.Sql &&
            (string.IsNullOrWhiteSpace(auth.PasswordEnvVar) ||
             string.IsNullOrEmpty(Environment.GetEnvironmentVariable(auth.PasswordEnvVar))))
            return Result(MutationStatus.Failed,
                $"SQL password environment variable '{auth.PasswordEnvVar}' is missing or empty.");

        var secrets = auth.Strategy == AuthStrategy.Sql
            ? new[] { auth.SqlUser, auth.PasswordEnvVar is { } variable ? Environment.GetEnvironmentVariable(variable) : null }
                .Where(value => !string.IsNullOrEmpty(value)).Cast<string>().ToArray()
            : Array.Empty<string>();
        var operation = new SqlHarnessMutationOperation(
            new SqlTargetRequest(request.Profile, request.Vars), request.Sql, request.Parameters,
            request.TimeoutSeconds, request.MaxRows, request.Database)
        {
            SessionKey = request.SessionKey,
            Approval = new ApprovedMutation(request.Digest, auth),
        };

        journal.UpdateMutation(context.RequestId, "executing", null, null, time.GetUtcNow());
        MutationBrokerResult result;
        try
        {
            var outcome = await module.ExecuteAsync(operation, ct).ConfigureAwait(false);
            if (outcome.ExitCode == SqlHarnessExitCode.Success && outcome.Report is SqlHarnessQueryReport query)
                result = new MutationBrokerResult(MutationStatus.Success, null, Redact(query, secrets));
            else if (outcome.ExitCode == SqlHarnessExitCode.TargetMismatch)
                result = Result(MutationStatus.DatabaseMismatch, outcome.SafeError);
            else if (outcome.MutationOutcomeUnknown)
                result = Result(MutationStatus.OutcomeUnknown, Redact(outcome.SafeError, secrets));
            else
                result = Result(MutationStatus.Failed, Redact(outcome.SafeError, secrets));
        }
        catch (Exception exception)
        {
            var unknown = OperationFailureMapper.IsMutationOutcomeUnknown(exception);
            result = Result(unknown ? MutationStatus.OutcomeUnknown : MutationStatus.Failed,
                SecretRedactor.Redact(exception, secrets));
        }

        if (result.Status == MutationStatus.Success && result.Result is { } confirmed)
        {
            try { journal.RecordMutationResult(context.RequestId, confirmed.RecordsAffected); }
            catch { /* Journal failure must not change the mutation result. */ }
        }

        var state = result.Status switch
        {
            MutationStatus.Success => "succeeded",
            MutationStatus.OutcomeUnknown => "outcome_unknown",
            _ => "failed",
        };
        journal.UpdateMutation(context.RequestId, state, result.Status, null, time.GetUtcNow());
        return result;
    }

    private async Task<IReadOnlyList<string>?> ResolveReaderPrincipalAsync(MutationRequest request, CancellationToken ct)
    {
        var sql = string.Equals(request.Engine, SqlEngineNames.Postgres, StringComparison.OrdinalIgnoreCase)
            ? "SELECT current_user AS l, session_user AS u"
            : "SELECT SUSER_SNAME() AS l, USER_NAME() AS u";
        try
        {
            var outcome = await module.ExecuteAsync(new SqlHarnessQueryOperation(
                new SqlTargetRequest(request.Profile, request.Vars), sql, [], request.TimeoutSeconds, 1, false, request.Database), ct)
                .ConfigureAwait(false);
            if (outcome.ExitCode != SqlHarnessExitCode.Success || outcome.Report is not SqlHarnessQueryReport report ||
                report.ResultSets.Count == 0 || report.ResultSets[0].Rows.Count == 0 || report.ResultSets[0].Rows[0].Count < 2)
                return null;
            var login = Convert.ToString(report.ResultSets[0].Rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
            var user = Convert.ToString(report.ResultSets[0].Rows[0][1], System.Globalization.CultureInfo.InvariantCulture);
            return new[] { login, user }.Where(value => !string.IsNullOrEmpty(value)).Cast<string>().ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool IdentityMatches(MutationRequest request, ResolvedTarget target) =>
        string.Equals(request.Engine, SqlEngineNames.Format(target.Engine), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(request.Server, target.Server, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(request.Database, target.Database, StringComparison.OrdinalIgnoreCase);

    private static MutationBrokerResult Result(string status, string? message) => new(status, message, null);

    private static string? Redact(string? value, IReadOnlyList<string> secrets) =>
        value is null ? null : SecretRedactor.Redact(value, secrets);

    private static SqlHarnessQueryReport Redact(SqlHarnessQueryReport report, IReadOnlyList<string> secrets) => report with
    {
        Target = report.Target with
        {
            RequestedServer = SecretRedactor.Redact(report.Target.RequestedServer, secrets),
            RequestedDatabase = SecretRedactor.Redact(report.Target.RequestedDatabase, secrets),
            ActualServer = SecretRedactor.Redact(report.Target.ActualServer, secrets),
            ActualDatabase = SecretRedactor.Redact(report.Target.ActualDatabase, secrets),
            Mode = SecretRedactor.Redact(report.Target.Mode, secrets),
            Engine = report.Target.Engine is null ? null : SecretRedactor.Redact(report.Target.Engine, secrets),
            TransportPolicy = report.Target.TransportPolicy is null ? null : SecretRedactor.Redact(report.Target.TransportPolicy, secrets),
        },
        StatementClassification = SecretRedactor.Redact(report.StatementClassification, secrets),
        Messages = report.Messages.Select(message => SecretRedactor.Redact(message, secrets)).ToArray(),
        ResultSets = report.ResultSets.Select(set => set with
        {
            Columns = set.Columns.Select(column => column with
            {
                Name = SecretRedactor.Redact(column.Name, secrets),
                DataType = SecretRedactor.Redact(column.DataType, secrets),
            }).ToArray(),
            Rows = set.Rows.Select(row => (IReadOnlyList<object?>)row.Select(value => value is string text
                ? SecretRedactor.Redact(text, secrets)
                : value).ToArray()).ToArray(),
        }).ToArray(),
    };
}
