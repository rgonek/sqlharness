using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

using JournalOperationPhase = SqlHarness.Core.Journal.OperationPhase;

namespace SqlHarness.Core;

public sealed record SqlHarnessColumnReport(int Ordinal, string Name, string DataType, bool AllowNull);

public sealed record SqlHarnessResultSetReport(
    IReadOnlyList<SqlHarnessColumnReport> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    long RowCount,
    long OmittedRowCount);

public sealed record SqlHarnessQueryReport(
    SqlHarnessTargetIdentityReport Target,
    string StatementClassification,
    IReadOnlyList<SqlHarnessResultSetReport> ResultSets,
    IReadOnlyList<string> Messages,
    int RecordsAffected,
    long DurationMilliseconds,
    string ResultHash,
    OutputFootprint RawFootprint,
    int OmittedMessages = 0);

public sealed class SqlHarnessModule : ISqlHarnessModule
{
    private static readonly IReadOnlySet<string> NoSessionTemps =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly ISqlSessionFactory _sessionFactory;
    private readonly ICompareArtifactWriter _artifactWriter;
    private readonly CompareCellRunner _cellRunner;
    private readonly CompareMatrixRunner _matrixRunner;
    private readonly Func<IReadOnlyDictionary<string, TargetProfile>> _loadProfiles;
    private readonly CompareConfig _compareConfig;
    private readonly IWatchClock _watchClock;
    private readonly ISnapshotStore _snapshotStore;

    /// <summary>
    /// Row cap for result-fingerprint retention. Production uses
    /// <see cref="CanonicalComparisonAccumulator.MaximumComparedRows"/>; tests may inject a lower limit.
    /// Only applied when comparison capture is enabled (compare with ordered/multiset/set).
    /// </summary>
    internal int ComparisonMaximumRows { get; init; } = CanonicalComparisonAccumulator.MaximumComparedRows;

    public SqlHarnessModule()
        : this(() => ProfileStore.Load(), SqlHarnessConfig.Default.Compare)
    {
    }

    public SqlHarnessModule(CompareConfig compareConfig)
        : this(() => ProfileStore.Load(), compareConfig)
    {
    }

    /// <summary>
    /// Scoped composition root: the same session factories and session
    /// policy as the default module, but profile reads come from the
    /// supplied provider (for example an MCP frozen scope) instead of the
    /// global store. Construction opens no connection and performs no auth.
    /// </summary>
    public SqlHarnessModule(Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles)
        : this(loadProfiles, SqlHarnessConfig.Default.Compare)
    {
    }

    public SqlHarnessModule(Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles, CompareConfig compareConfig)
        : this(
            new EngineSessionFactory(new SqlClientSessionFactory(new AzureCli()), new NpgsqlSessionFactory()),
            new CompareArtifactWriter(),
            loadProfiles,
            compareConfig: compareConfig)
    {
    }

    internal SqlHarnessModule(
        ISqlSessionFactory sessionFactory,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
        IWatchClock? watchClock = null,
        ISnapshotStore? snapshotStore = null,
        CompareConfig? compareConfig = null)
        : this(
            sessionFactory,
            new CompareArtifactWriter(),
            loadProfiles,
            watchClock,
            snapshotStore,
            compareConfig)
    {
    }

    internal SqlHarnessModule(
        ISqlSessionFactory sessionFactory,
        ICompareArtifactWriter artifactWriter,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
        IWatchClock? watchClock = null,
        ISnapshotStore? snapshotStore = null,
        CompareConfig? compareConfig = null)
    {
        _sessionFactory = new TimedSessionFactory(sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory)));
        _artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
        _cellRunner = new CompareCellRunner(_sessionFactory, _artifactWriter);
        _matrixRunner = new CompareMatrixRunner(_cellRunner);
        _loadProfiles = loadProfiles ?? throw new ArgumentNullException(nameof(loadProfiles));
        _compareConfig = compareConfig ?? SqlHarnessConfig.Default.Compare;
        _watchClock = watchClock ?? new SystemWatchClock();
        _snapshotStore = snapshotStore ?? new SnapshotStore(SqlHarnessPaths.SnapshotsDir);
    }

    public async Task<SqlHarnessOutcome> ExecuteAsync(
        SqlHarnessOperation operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var effective = SqlHarnessOperationReferences.Ensure(operation);
        var outcome = await ExecuteAssignedAsync(effective, ct);
        return outcome with { OperationRef = effective.OperationRef };
    }

    private async Task<SqlHarnessOutcome> ExecuteAssignedAsync(
        SqlHarnessOperation operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation is SqlHarnessArtifactReadOperation artifactRead)
            return Checked(artifactRead, ExecuteArtifactRead(artifactRead));

        if (operation is SqlHarnessPlanOperation plan)
            return Checked(plan, ExecutePlan(plan));

        if (operation is SqlHarnessCompareOperation compare)
            return Checked(compare, await ExecuteCompareAsync(compare, ct));

        if (operation is SqlHarnessCompareMatrixOperation matrix)
            return Checked(matrix, await ExecuteCompareMatrixAsync(matrix, ct));

        if (operation is SqlHarnessMeasureOperation measure)
            return Checked(measure, await ExecuteMeasureAsync(measure, ct));

        if (operation is SqlHarnessWatchOperation watch)
            return Checked(watch, await ExecuteWatchAsync(watch, ct));

        if (operation is SqlHarnessSnapshotOperation snapshot)
            return Checked(snapshot, await ExecuteSnapshotAsync(snapshot, ct));

        if (operation is SqlHarnessMutationOperation mutation)
            return Checked(mutation, await ExecuteMutationAsync(mutation, ct));

        if (operation is not SqlHarnessQueryOperation query)
        {
            return new SqlHarnessOutcome(
                SqlHarnessExitCode.Safety,
                null,
                SecretRedactor.Redact("The SQLHarness operation is not implemented.", []));
        }

        var stopwatch = Stopwatch.StartNew();
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var knownSecrets = new List<string>();

        try
        {
            ValidateBounds(query);
            var target = query.ResolvedTarget ?? TargetResolver.Resolve(query.Target, _loadProfiles());
            OperationSecrets.AddCredentials(knownSecrets, target);
            var dialect = SqlDialects.For(target.Engine);
            var safety = dialect.Classify(
                query.Sql,
                query.UsageOverride ?? SqlUsage.Query,
                target.Database,
                query.AllowMutation,
                query.ConfirmDatabase,
                NoSessionTemps);
            if (!safety.Allowed)
            {
                var message = $"SQL safety rejection: {safety.RejectionDescription}";
                if (operation is SqlHarnessQueryOperation && safety.Reason == SqlSafetyReason.MutationNotAllowed)
                    message += "\nPersistent changes use `sqlharness mutation`.";
                throw new SqlHarnessSafetyException(message);
            }
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(query.Parameters, query.TypedParameters));
            dialect.ValidateParameterReferences(parameters, query.Sql);

            if (query.DryRun)
            {
                MissingParameterCheck.RequireSupplied(target.Engine, parameters, query.Sql);
                return Checked(query, DryRunOutcome("query", BenchmarkReports.ClassificationLabel(safety), SqlRiskTiers.Label(safety.Tier), parameters, 1));
            }

            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct, knownSecrets);
            phase = OperationPhase.Sql;

            var execution = new SqlExecutionCommand(query.Sql, parameters, query.TimeoutSeconds)
            {
                PostgresMode = target.Engine == SqlEngine.Postgres && safety.HasMutation
                    ? PostgresExecutionMode.ApprovedMutation
                    : PostgresExecutionMode.ReadOnly,
            };
            CollectedQueryResult collected;
            using (OperationPhaseRecorder.Current?.Start(JournalOperationPhase.Execute))
                collected = await QueryResultCollector.CollectAsync(
                    session,
                    execution,
                    query.MaxRows,
                    knownSecrets,
                    () =>
                    {
                        raw = new CanonicalResultAccumulator();
                        return raw;
                    },
                    ct);
            rawFootprint = raw!.Complete().Footprint;
            var targetReport = session.Identity;
            var report = new SqlHarnessQueryReport(
                targetReport,
                BenchmarkReports.ClassificationLabel(safety),
                collected.ResultSets,
                collected.Messages,
                collected.RecordsAffected,
                stopwatch.ElapsedMilliseconds,
                collected.Canonical.Hash,
                rawFootprint,
                collected.OmittedMessageCount);
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
            return Checked(query, WithReceipt(success, rawFootprint));
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(
                exitCode,
                null,
                SecretRedactor.Redact(exception, knownSecrets))
            {
                MutationOutcomeUnknown = query.UsageOverride == SqlUsage.Mutation &&
                    phase == OperationPhase.Sql && OperationFailureMapper.IsMutationOutcomeUnknown(exception),
            };
            return Checked(query, WithReceipt(failure, rawFootprint));
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteMutationAsync(SqlHarnessMutationOperation operation, CancellationToken ct)
    {
        var knownSecrets = new List<string>();
        ResolvedTarget? target = null;
        try
        {
            var query = new SqlHarnessQueryOperation(
                operation.Target, operation.Sql, [], operation.TimeoutSeconds, operation.MaxRows, true,
                operation.ExpectDatabase)
            {
                TypedParameters = operation.Parameters,
                DryRun = operation.DryRun,
            };
            ValidateBounds(query);
            target = TargetResolver.Resolve(operation.Target, _loadProfiles());
            OperationSecrets.AddCredentials(knownSecrets, target);
            var dialect = SqlDialects.For(target.Engine);
            var safety = dialect.Classify(
                operation.Sql, SqlUsage.Mutation, target.Database, false, null, NoSessionTemps);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");

            var parameters = dialect.BindParameters(operation.Parameters);
            dialect.ValidateParameterReferences(parameters, operation.Sql);

            if (safety.Tier is SqlRiskTier.ReadOnly or SqlRiskTier.Session)
                return MutationOutcome(SqlHarnessExitCode.Safety, MutationStatus.NotAMutation,
                    "The batch does not contain a persistent mutation.");

            if (operation.ExpectDatabase is not null &&
                !string.Equals(operation.ExpectDatabase, target.Database, StringComparison.Ordinal))
                return MutationOutcome(SqlHarnessExitCode.TargetMismatch, MutationStatus.DatabaseMismatch,
                    "The resolved database does not match the expected database.");

            if (operation.DryRun)
            {
                MissingParameterCheck.RequireSupplied(target.Engine, parameters, operation.Sql);
                return Checked(operation, DryRunOutcome(
                    "mutation", BenchmarkReports.ClassificationLabel(safety), SqlRiskTiers.Label(safety.Tier), parameters, 1));
            }

            var request = new MutationRequest(
                operation.Target.Profile ?? string.Empty,
                operation.Target.Vars,
                SqlEngineNames.Format(target.Engine),
                target.Server,
                target.Database,
                operation.Sql,
                operation.Parameters,
                operation.TimeoutSeconds,
                operation.MaxRows,
                SqlRiskTiers.Label(safety.Tier),
                safety.Grantees,
                operation.SessionKey);

            if (operation.Approval is null)
                return Checked(operation, new SqlHarnessOutcome(SqlHarnessExitCode.Success,
                    new SqlHarnessMutationPrepared(request), null));

            if (!string.Equals(operation.Approval.Digest, request.Digest, StringComparison.Ordinal))
                throw new SqlHarnessSafetyException("Approval does not match the request.");

            var approvedQuery = query with
            {
                ConfirmDatabase = target.Database,
                ResolvedTarget = target with { Auth = operation.Approval.Writer },
                UsageOverride = SqlUsage.Mutation,
                OperationRef = operation.OperationRef,
            };
            return Checked(operation, await ExecuteAssignedAsync(approvedQuery, ct));
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, OperationPhase.Validation);
            return Checked(operation, new SqlHarnessOutcome(
                exitCode,
                null,
                SecretRedactor.Redact(exception, knownSecrets)));
        }
    }

    private static SqlHarnessOutcome MutationOutcome(SqlHarnessExitCode exitCode, string status, string message) =>
        new(exitCode, new MutationBrokerResult(status, message, null), message);

    /// <summary>
    /// Enforces the internal typed-result contract on the dispatch boundary:
    /// an operation can never leave the facade paired with a mismatched
    /// report. Null (failure) reports are always compatible.
    /// </summary>
    private static SqlHarnessOutcome Checked(SqlHarnessOperation operation, SqlHarnessOutcome outcome)
    {
        OperationReportContract.AssertCompatible(operation, outcome.Report);
        return outcome;
    }

    private static SqlHarnessOutcome ExecuteArtifactRead(SqlHarnessArtifactReadOperation operation)
    {
        try
        {
            var resolved = ArtifactReader.ReadSectionWithMetadata(
                operation.Root, operation.ArtifactId, operation.Section, operation.Owner,
                operation.Cursor, operation.PlanSelector, operation.IncludeText);
            var json = JsonSerializer.Serialize(resolved.Section, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
            var lines = json.Length == 0 ? 0 : json.Count(character => character == '\n') + 1;
            return WithReceipt(new SqlHarnessOutcome(
                SqlHarnessExitCode.Success,
                new SqlHarnessArtifactReadReport(resolved.Section, resolved.ProducerOperationRef),
                null)
            {
                ParentOperationRef = resolved.ProducerOperationRef,
            }, new OutputFootprint(bytes, lines));
        }
        catch (ArtifactReadException exception)
        {
            return WithReceipt(new SqlHarnessOutcome(exception.ExitCode, null, exception.Message), new OutputFootprint(0, 0));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WithReceipt(new SqlHarnessOutcome(SqlHarnessExitCode.LocalStorage, null, "Artifact storage is unavailable."), new OutputFootprint(0, 0));
        }
    }

    /// <summary>
    /// Attaches the emission receipt: it carries the raw footprint for the activity journal
    /// (JournalingModule records raw and emitted footprints) and returns the outcome's exit code.
    /// </summary>
    private static SqlHarnessOutcome WithReceipt(SqlHarnessOutcome outcome, OutputFootprint? raw)
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(outcome.ExitCode))
        {
            RawFootprint = raw,
        };
        return outcome with { EmissionReceipt = receipt };
    }

    private static SqlHarnessOutcome DryRunOutcome(
        string command,
        string classification,
        string tier,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int parameterSets) =>
        WithReceipt(
            new SqlHarnessOutcome(
                SqlHarnessExitCode.Success,
                new SqlHarnessDryRunReport(
                    command,
                    classification,
                    tier,
                    BenchmarkReports.ToParameterReports(parameters),
                    parameterSets),
                null),
            new OutputFootprint(0, 0));

    private async Task<SqlHarnessOutcome> ExecuteCompareAsync(SqlHarnessCompareOperation compare, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        OutputFootprint? rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>();

        try
        {
            ValidateCompareContext(compare.Context);
            var prepared = CompareOperationPreparer.PrepareFixed(
                compare.Target,
                _loadProfiles(),
                compare.SetupSql,
                compare.BaselineSql,
                compare.CandidateSql,
                compare.Parameters,
                compare.TimeoutSeconds,
                compare.Repeat,
                knownSecrets,
                compare.TypedParameters);

            CompareOperationPreparer.ValidateVariants(
                prepared,
                compare.SetupSql,
                compare.BaselineSql,
                compare.CandidateSql,
                [prepared.FixedParameters]);
            var target = prepared.Target;

            if (compare.DryRun)
            {
                MissingParameterCheck.RequireSupplied(target.Engine, prepared.FixedParameters, compare.BaselineSql, compare.CandidateSql);
                return DryRunOutcome(
                    "compare",
                    prepared.Classification.Baseline,
                    SqlRiskTiers.Label(prepared.Tier),
                    prepared.FixedParameters,
                    parameterSets: 1);
            }

            _artifactWriter.CheckStorage();

            var request = new CompareCellRequest(
                target,
                compare.SetupSql,
                compare.BaselineSql,
                compare.CandidateSql,
                prepared.FixedParameters,
                compare.TimeoutSeconds,
                compare.Repeat,
                compare.CompareResults,
                compare.CompareNullability)
            {
                Classification = prepared.Classification,
                SetupSafety = prepared.SetupSafety,
                Owner = ArtifactOwner.From(compare.Target, prepared.Target),
                KnownSecrets = knownSecrets,
                OperationRef = compare.OperationRef,
            };
            _cellRunner.ComparisonMaximumRows = ComparisonMaximumRows;

            CompareCellResult cell;
            try
            {
                cell = await _cellRunner.RunAsync(request, ct);
            }
            catch (CompareCellFailedException failed)
            {
                // Unwrap so exit-code mapping and redaction see the original failure and phase.
                phase = failed.Phase switch
                {
                    CompareCellPhase.Authentication => OperationPhase.Authentication,
                    CompareCellPhase.Artifact => OperationPhase.Artifact,
                    _ => OperationPhase.Sql,
                };
                rawFootprint = failed.RawFootprint;
                // Capture keeps the original stack. The following throw is for definite assignment.
                var inner = failed.InnerException ?? failed;
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw inner;
            }

            rawFootprint = cell.RawFootprint;
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, cell.Report, null)
            {
                BenchmarkRuns = cell.Runs,
            };
            return WithReceipt(success, rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = phase == OperationPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, knownSecrets),
                Error: (exception as ArtifactStoragePreflightException)?.ToError(knownSecrets));
            return WithReceipt(failure, rawFootprint);
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteCompareMatrixAsync(
        SqlHarnessCompareMatrixOperation operation,
        CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        OutputFootprint? rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>();

        try
        {
            ValidateCompareContext(operation.Context);
            var prepared = CompareOperationPreparer.PrepareFixed(
                operation.Target,
                _loadProfiles(),
                operation.SetupSql,
                operation.BaselineSql,
                operation.CandidateSql,
                operation.Parameters,
                operation.TimeoutSeconds,
                operation.Repeat,
                knownSecrets,
                operation.TypedParameters);

            var fixedParameters = prepared.FixedParameters;
            var dialect = prepared.Dialect;
            var matrixInput = operation.TypedMatrix is null && string.IsNullOrEmpty(operation.Matrix)
                ? null
                : SqlParameterInputs.ResolveMatrix(operation.Matrix, operation.TypedMatrix);
            var sets = ParameterSetResolver.Resolve(dialect, fixedParameters, matrixInput, operation.ParameterSets);
            if (sets.Count == 0)
                throw new SqlHarnessSafetyException("A compare matrix or at least two parameter sets must be supplied.");
            var parameterName = matrixInput is null ? null : "@" + SqlParameterNames.Normalize(matrixInput.Name);
            var parameterType = matrixInput?.Type;
            var cellParameters = sets
                .Select(set => (IReadOnlyList<SqlHarnessParameter>)[.. fixedParameters, .. set.Parameters])
                .ToArray();

            CompareOperationPreparer.ValidateVariants(
                prepared,
                operation.SetupSql,
                operation.BaselineSql,
                operation.CandidateSql,
                cellParameters);

            var target = prepared.Target;

            if (operation.DryRun)
            {
                foreach (var parameters in cellParameters)
                    MissingParameterCheck.RequireSupplied(target.Engine, parameters, operation.BaselineSql, operation.CandidateSql);
                return DryRunOutcome(
                    "compare",
                    prepared.Classification.Baseline,
                    SqlRiskTiers.Label(prepared.Tier),
                    cellParameters[0],
                    cellParameters.Length);
            }

            _artifactWriter.CheckStorage();

            var template = new CompareCellRequest(
                target,
                operation.SetupSql,
                operation.BaselineSql,
                operation.CandidateSql,
                fixedParameters,
                operation.TimeoutSeconds,
                operation.Repeat,
                operation.CompareResults,
                operation.CompareNullability)
            {
                Classification = prepared.Classification,
                SetupSafety = prepared.SetupSafety,
                Owner = ArtifactOwner.From(operation.Target, prepared.Target),
                KnownSecrets = knownSecrets,
                OperationRef = operation.OperationRef,
            };
            _matrixRunner.ComparisonMaximumRows = ComparisonMaximumRows;
            CompareMatrixResult result;
            try
            {
                result = await _matrixRunner.RunAsync(
                    new CompareMatrixRun(
                        template,
                        fixedParameters,
                        sets,
                        parameterName,
                        parameterType),
                    ct);
            }
            catch (CompareMatrixCellFailedException failed)
            {
                phase = failed.Phase switch
                {
                    CompareCellPhase.Authentication => OperationPhase.Authentication,
                    CompareCellPhase.Artifact => OperationPhase.Artifact,
                    _ => OperationPhase.Sql,
                };
                rawFootprint = failed.RawFootprint;
                if (failed.PartialReport is { } partialReport)
                {
                    try
                    {
                        phase = OperationPhase.Artifact;
                        _artifactWriter.WriteMatrixIndex(partialReport.Cells);
                    }
                    catch (Exception exception)
                    {
                        return WithReceipt(
                            new SqlHarnessOutcome(SqlHarnessExitCode.LocalStorage, partialReport, SecretRedactor.Redact(exception, knownSecrets)),
                            rawFootprint);
                    }
                    phase = failed.Phase switch
                    {
                        CompareCellPhase.Authentication => OperationPhase.Authentication,
                        CompareCellPhase.Artifact => OperationPhase.Artifact,
                        _ => OperationPhase.Sql,
                    };
                }
                var exitCode = phase == OperationPhase.Artifact
                    ? SqlHarnessExitCode.LocalStorage
                    : OperationFailureMapper.Map(failed.InnerException ?? failed, phase);
                return WithReceipt(
                    new SqlHarnessOutcome(exitCode, failed.PartialReport, FormatMatrixCellError(failed, knownSecrets)),
                    rawFootprint);
            }

            rawFootprint = result.RawFootprint;
            phase = OperationPhase.Artifact;
            _artifactWriter.WriteMatrixIndex(result.Report.Cells);
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, result.Report, null)
            {
                BenchmarkRuns = result.Runs,
            };
            return WithReceipt(success, rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = phase == OperationPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, knownSecrets),
                Error: (exception as ArtifactStoragePreflightException)?.ToError(knownSecrets));
            return WithReceipt(failure, rawFootprint);
        }
    }

    private static string FormatMatrixCellError(CompareMatrixCellFailedException failed, IReadOnlyList<string> knownSecrets)
    {
        var original = failed.InnerException ?? failed;
        // Prefix the cell label after redaction so a redacted value cannot erase the index.
        var detail = SecretRedactor.Redact(original, knownSecrets);
        var label = failed.ParameterName is { } parameterName
            ? $"SQL parameter '{parameterName}'"
            : $"parameter set '{failed.SetName}'";
        return $"Comparison matrix cell {failed.Index} for {label} failed. {detail}";
    }

    private async Task<SqlHarnessOutcome> ExecuteMeasureAsync(SqlHarnessMeasureOperation measure, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var knownSecrets = new List<string>();

        try
        {
            ValidateMeasure(measure);
            var target = TargetResolver.Resolve(measure.Target, _loadProfiles());
            OperationSecrets.AddCredentials(knownSecrets, target);
            var dialect = SqlDialects.For(target.Engine);
            SqlSafetyDecision? setupSafety = null;
            IReadOnlySet<string> setupTemps = NoSessionTemps;
            if (!string.IsNullOrWhiteSpace(measure.SetupSql))
            {
                setupSafety = dialect.Classify(
                    measure.SetupSql, SqlUsage.CompareSetup, target.Database, false, null, NoSessionTemps);
                CompareOperationPreparer.EnsureSafe(setupSafety, "setup");
                setupTemps = setupSafety.SessionTempTables;
            }

            var querySafety = dialect.Classify(
                measure.QuerySql, SqlUsage.Query, target.Database, false, null, setupTemps);
            CompareOperationPreparer.EnsureSafe(querySafety, "query");
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(measure.Parameters, measure.TypedParameters));
            var parameterSets = ResolveMeasureParameterSets(measure, dialect, parameters);
            if (dialect.Engine == SqlEngine.SqlServer && parameterSets is null)
                SqlSetupVariableReferenceValidator.Validate(measure.SetupSql, parameters, measure.QuerySql);
            dialect.ValidateParameterReferences(parameters, measure.SetupSql, measure.QuerySql);
            SetupSqlExecution.Validate(dialect.Engine, measure.SetupSql, parameters);
            dialect.ValidateMeasuredBatch(measure.QuerySql);

            if (parameterSets is not null)
            {
                var boundSets = BindMeasureParameterSets(dialect, measure, parameterSets);
                if (measure.DryRun)
                {
                    foreach (var boundSet in boundSets)
                        MissingParameterCheck.RequireSupplied(target.Engine, boundSet.Parameters, measure.QuerySql);
                    return DryRunOutcome(
                        "measure",
                        BenchmarkReports.ClassificationLabel(querySafety),
                        SqlRiskTiers.Label(SqlRiskTiers.Max(querySafety.Tier, setupSafety?.Tier ?? SqlRiskTier.ReadOnly)),
                        boundSets[0].Parameters,
                        boundSets.Count);
                }

                _artifactWriter.CheckStorage();
                phase = OperationPhase.Authentication;
                await using var multiSession = await _sessionFactory.ConnectAsync(target, ct, knownSecrets);
                phase = OperationPhase.Sql;

                raw = new CanonicalResultAccumulator();
                var execution = await new MeasureParameterSetRunner(dialect, ComparisonMaximumRows).ExecuteAsync(
                    multiSession,
                    measure,
                    boundSets,
                    raw,
                    ct,
                    setupSafety);
                rawFootprint = raw.Complete().Footprint;
                var setReport = MeasureParameterSetReportProjector.Project(
                    multiSession.Identity,
                    measure.Repeat,
                    execution,
                    boundSets);

                phase = OperationPhase.Artifact;
                var setDirectory = _artifactWriter.Write(
                    setReport,
                    execution.Runs.Select(run => run.Artifact).ToArray(),
                    target.Database,
                    ArtifactOwner.From(measure.Target, target),
                    measure.OperationRef);
                setReport = setReport with { ArtifactDirectory = setDirectory };
                var setSuccess = new SqlHarnessOutcome(SqlHarnessExitCode.Success, setReport, null)
                {
                    BenchmarkRuns = execution.Runs.Select(run => run.Artifact).ToArray(),
                };
                return WithReceipt(setSuccess, rawFootprint);
            }

            if (measure.DryRun)
            {
                MissingParameterCheck.RequireSupplied(target.Engine, parameters, measure.QuerySql);
                return DryRunOutcome(
                    "measure",
                    BenchmarkReports.ClassificationLabel(querySafety),
                    SqlRiskTiers.Label(SqlRiskTiers.Max(querySafety.Tier, setupSafety?.Tier ?? SqlRiskTier.ReadOnly)),
                    parameters,
                    parameterSets: 1);
            }

            _artifactWriter.CheckStorage();
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct, knownSecrets);
            phase = OperationPhase.Sql;

            raw = new CanonicalResultAccumulator();
            await SetupSqlExecution.ExecuteSetupAsync(
                dialect.Engine,
                measure.SetupSql,
                parameters,
                measure.TimeoutSeconds,
                session,
                raw: null,
                ct,
                setupSafety);

            // Measure never runs ResultComparer; skip fingerprint retention and the 1M row comparison cap.
            await ExecuteBenchmarkRunAsync(dialect, session, measure.QuerySql, parameters, measure.TimeoutSeconds, 0, "measure", raw: null, captureComparison: false, ct);
            var runs = new List<CollectedBenchmarkRun>(measure.Repeat);
            for (var repetition = 1; repetition <= measure.Repeat; repetition++)
                runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, measure.QuerySql, parameters, measure.TimeoutSeconds, repetition, "measure", repetition == 1 ? raw : null, captureComparison: false, ct));

            rawFootprint = raw.Complete().Footprint;
            var targetReport = session.Identity;
            var report = new SqlHarnessMeasureReport(
                targetReport,
                measure.Repeat,
                runs.Count,
                runs.Select(run => run.ResultHash).Distinct(StringComparer.Ordinal).Count() == 1,
                BenchmarkReports.CreateVariantReport("measure", runs),
                null)
            {
                Classification = new BenchmarkClassificationReport(
                    BenchmarkReports.ClassificationLabel(setupSafety),
                    BenchmarkReports.ClassificationLabel(querySafety)),
                Parameters = BenchmarkReports.ToParameterReports(parameters),
            };

            phase = OperationPhase.Artifact;
            var directory = _artifactWriter.Write(report, runs.Select(run => run.Artifact).ToArray(), target.Database, ArtifactOwner.From(measure.Target, target), measure.OperationRef);
            report = report with { ArtifactDirectory = directory };
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null)
            {
                BenchmarkRuns = runs.Select(run => run.Artifact).ToArray(),
            };
            return WithReceipt(success, rawFootprint);
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var exitCode = phase == OperationPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, knownSecrets),
                Error: (exception as ArtifactStoragePreflightException)?.ToError(knownSecrets));
            return WithReceipt(failure, rawFootprint);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private static IReadOnlyList<PreparedMeasureParameterSet> BindMeasureParameterSets(
        ISqlDialect dialect,
        SqlHarnessMeasureOperation measure,
        IReadOnlyList<SqlHarnessParameterSetInput> parameterSets)
    {
        return MeasureParameterSetValidator.Prepare(
            measure.Parameters,
            parameterSets,
            measure.SetupSql,
            measure.QuerySql,
            dialect,
            measure.TypedParameters);
    }

    private static IReadOnlyList<SqlHarnessParameterSetInput>? ResolveMeasureParameterSets(
        SqlHarnessMeasureOperation measure,
        ISqlDialect dialect,
        IReadOnlyList<SqlHarnessParameter> fixedParameters)
    {
        if (measure.TypedMatrix is null)
            return measure.ParameterSets is { Count: > 0 } ? measure.ParameterSets : null;

        var matrix = SqlParameterInputs.ResolveMatrix(string.Empty, measure.TypedMatrix);
        var resolved = ParameterSetResolver.Resolve(dialect, fixedParameters, matrix, measure.ParameterSets);
        return resolved
            .Select(set => new SqlHarnessParameterSetInput(set.Name, [])
            {
                TypedParameters = [new SqlHarnessParameterInput(matrix.Name, matrix.Type, set.ParameterValue)],
            })
            .ToArray();
    }

    private Task<CollectedBenchmarkRun> ExecuteBenchmarkRunAsync(
        ISqlDialect dialect,
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator? raw,
        bool captureComparison,
        CancellationToken ct) =>
        BenchmarkRunner.ExecuteAsync(
            dialect,
            session,
            sql,
            parameters,
            timeoutSeconds,
            repetition,
            variant,
            parameterSet: null,
            raw,
            captureComparison,
            ComparisonMaximumRows,
            ct);

    private static void ValidateMeasure(SqlHarnessMeasureOperation measure)
    {
        if (measure.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (measure.Repeat is < 1 or > 100)
            throw new SqlHarnessSafetyException("Measurement repetitions must be between 1 and 100.");
        if (measure.TypedMatrix is not null && measure.ParameterSets is not null)
            throw new SqlHarnessSafetyException("A parameter matrix and parameter sets cannot be supplied together.");
    }

    private void ValidateCompareContext(CompareContext? context)
    {
        try
        {
            CompareContext.Parse(context?.Subject, context?.Scope, _compareConfig.RequireContext);
        }
        catch (ArgumentException exception)
        {
            throw new SqlHarnessSafetyException(exception.Message);
        }
    }

    private static void ValidateBounds(SqlHarnessQueryOperation query)
    {
        if (query.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (query.MaxRows is < 0 or > 500)
            throw new SqlHarnessSafetyException("Maximum displayed rows must be between 0 and 500.");
    }

    private async Task<SqlHarnessOutcome> ExecuteWatchAsync(SqlHarnessWatchOperation watch, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>();

        try
        {
            ValidateWatch(watch);
            var target = TargetResolver.Resolve(watch.Target, _loadProfiles());
            OperationSecrets.AddCredentials(knownSecrets, target);
            var dialect = SqlDialects.For(target.Engine);
            var safety = dialect.Classify(
                watch.Sql,
                SqlUsage.Query,
                target.Database,
                allowMutation: false,
                confirmDatabase: null,
                NoSessionTemps);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(watch.Parameters, watch.TypedParameters));
            dialect.ValidateParameterReferences(parameters, watch.Sql);
            // Predicate syntax is validated before authentication so bad --until fails closed.
            if (watch.Until is not null)
                _ = WatchCondition.Parse(watch.Until);

            phase = OperationPhase.Authentication;
            var runner = new WatchRunner(_sessionFactory, _watchClock);
            var (outcome, raw) = await runner.ExecuteAsync(
                watch,
                target,
                parameters,
                knownSecrets,
                ct);
            rawFootprint = raw;
            // Runner already mapped connect/SQL failures; preserve its exit code and report.
            return WithReceipt(outcome, rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(
                exitCode,
                null,
                SecretRedactor.Redact(exception, knownSecrets));
            return WithReceipt(failure, rawFootprint);
        }
    }

    /// <summary>
    /// NDJSON twin of <see cref="ExecuteWatchAsync"/> for
    /// <c>watch --output ndjson</c>. Same validation, safety and parameter
    /// pipeline; failures before the run emit a best-effort <c>failed</c>
    /// record (without <c>started</c>, which needs the session identity).
    /// The returned outcome carries no report â€” the stream is the output â€”
    /// but keeps the exit code and journal receipt, so the activity journal counts the whole
    /// stream as emitted output.
    /// </summary>
    public async Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation watch,
        TextWriter writer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(writer);
        watch = (SqlHarnessWatchOperation)SqlHarnessOperationReferences.Ensure(watch);

        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>();

        try
        {
            ValidateWatch(watch);
            var target = TargetResolver.Resolve(watch.Target, _loadProfiles());
            OperationSecrets.AddCredentials(knownSecrets, target);
            var dialect = SqlDialects.For(target.Engine);
            var safety = dialect.Classify(
                watch.Sql,
                SqlUsage.Query,
                target.Database,
                allowMutation: false,
                confirmDatabase: null,
                NoSessionTemps);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(watch.Parameters, watch.TypedParameters));
            dialect.ValidateParameterReferences(parameters, watch.Sql);
            // Predicate syntax is validated before authentication so bad --until fails closed.
            if (watch.Until is not null)
                _ = WatchCondition.Parse(watch.Until);

            phase = OperationPhase.Authentication;
            var stream = new WatchNdjsonWriter(writer);
            var runner = new WatchRunner(_sessionFactory, _watchClock);
            var (outcome, raw) = await runner.ExecuteNdjsonAsync(
                watch,
                target,
                parameters,
                knownSecrets,
                stream,
                ct);
            rawFootprint = raw;
            // Runner already mapped connect/SQL failures; preserve its exit code.
            return Checked(watch, WithReceipt(outcome, rawFootprint) with { OperationRef = watch.OperationRef });
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var message = SecretRedactor.Redact(exception, knownSecrets);
            EmitWatchNdjsonFailed(writer, exitCode, SqlHarnessError.From(exitCode, message, "validation"));
            var failure = new SqlHarnessOutcome(exitCode, null, message);
            return Checked(watch, WithReceipt(failure, rawFootprint) with { OperationRef = watch.OperationRef });
        }
    }

    private static void EmitWatchNdjsonFailed(
        TextWriter writer,
        SqlHarnessExitCode exitCode,
        SqlHarnessError error)
    {
        // The caller redacts error.Message exactly once. Best effort: a dead
        // transport cannot take a terminal record, but the
        // returned outcome still reports the failure (never a success).
        try
        {
            new WatchNdjsonWriter(writer).WriteFailed(
                new
                {
                    exitCode = (int)exitCode,
                    error,
                },
                0);
        }
        catch
        {
            // Swallowed: the outcome carries the failure.
        }
    }

    private static void ValidateWatch(SqlHarnessWatchOperation watch)
    {
        if (watch.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (watch.MaxRows is < 0 or > 500)
            throw new SqlHarnessSafetyException("Maximum displayed rows must be between 0 and 500.");
        if (watch.HistoryLimit is < 1 or > 10000)
            throw new SqlHarnessSafetyException("--history-limit must be between 1 and 10000.");
        if (watch.Interval <= TimeSpan.Zero || watch.Interval > TimeSpan.FromHours(24))
            throw new SqlHarnessSafetyException("Watch interval must be greater than zero and at most 24 hours.");
        if (watch.MaxDuration <= TimeSpan.Zero || watch.MaxDuration > TimeSpan.FromHours(24))
            throw new SqlHarnessSafetyException("Watch max duration must be greater than zero and at most 24 hours.");

        var hasUntil = !string.IsNullOrWhiteSpace(watch.Until);
        var hasUntilUnchanged = watch.UntilUnchanged is not null;
        if (hasUntil == hasUntilUnchanged)
            throw new SqlHarnessSafetyException("Specify exactly one of --until or --until-unchanged.");
        if (watch.UntilUnchanged is < 1)
            throw new SqlHarnessSafetyException("--until-unchanged must be a positive integer.");
        // --until evaluates the first retained display row; max-rows 0 would always fail after a successful poll.
        if (hasUntil && watch.MaxRows < 1)
            throw new SqlHarnessSafetyException("Watch --until requires --max-rows of at least 1.");
    }

    private async Task<SqlHarnessOutcome> ExecuteSnapshotAsync(SqlHarnessSnapshotOperation snapshot, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>();

        try
        {
            ValidateSnapshot(snapshot);
            var target = TargetResolver.Resolve(snapshot.Target, _loadProfiles());
            OperationSecrets.AddCredentials(knownSecrets, target);
            var dialect = SqlDialects.For(target.Engine);
            var safety = dialect.Classify(
                snapshot.Sql,
                SqlUsage.Query,
                target.Database,
                allowMutation: false,
                confirmDatabase: null,
                NoSessionTemps);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(snapshot.Parameters, snapshot.TypedParameters));
            dialect.ValidateParameterReferences(parameters, snapshot.Sql);

            phase = OperationPhase.Authentication;
            var runner = new SnapshotRunner(_sessionFactory, _snapshotStore);
            var (outcome, raw) = await runner.ExecuteAsync(
                snapshot,
                target,
                parameters,
                knownSecrets,
                ct);
            rawFootprint = raw;
            // Runner already mapped connect/SQL/storage failures; preserve its exit code and report.
            return WithReceipt(outcome, rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(
                exitCode,
                null,
                SecretRedactor.Redact(exception, knownSecrets));
            return WithReceipt(failure, rawFootprint);
        }
    }

    private static void ValidateSnapshot(SqlHarnessSnapshotOperation snapshot)
    {
        if (snapshot.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (snapshot.MaxRows is < 0 or > 500)
            throw new SqlHarnessSafetyException("Maximum displayed rows must be between 0 and 500.");
        if (snapshot.Diff && snapshot.Force)
            throw new SqlHarnessSafetyException("--force cannot be combined with --diff.");
        if (string.IsNullOrWhiteSpace(snapshot.Name) ||
            snapshot.Name.Length > 64 ||
            !char.IsAsciiLetterOrDigit(snapshot.Name[0]) ||
            snapshot.Name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')))
        {
            throw new SqlHarnessSafetyException(
                "Snapshot name must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$.");
        }
    }

    private const string InvalidPlanEitherMessage =
        "The execution plan is not a valid SQL Server Showplan document or Postgres EXPLAIN JSON document.";

    private SqlHarnessOutcome ExecutePlan(SqlHarnessPlanOperation operation)
    {
        var raw = operation.RawFootprint;
        try
        {
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, DistillPlanDocument(operation.ShowplanXml), null);
            return WithReceipt(outcome, raw);
        }
        catch (Exception exception)
        {
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, SecretRedactor.Redact(exception, [operation.ShowplanXml]));
            return WithReceipt(outcome, raw);
        }
    }

    private static DistilledPlan DistillPlanDocument(string document)
    {
        var trimmed = document.AsSpan().TrimStart();
        if (trimmed.IsEmpty)
            throw new SqlHarnessSafetyException(InvalidPlanEitherMessage);

        return trimmed[0] switch
        {
            '<' => PlanDistiller.Distill(document),
            '{' or '[' => PostgresPlanDistiller.Distill(document),
            _ => throw new SqlHarnessSafetyException(InvalidPlanEitherMessage),
        };
    }
}

internal sealed record CollectedCompare(
    CanonicalResult Canonical,
    CanonicalComparisonResult Comparison,
    IReadOnlyList<string> PlanXmls);

internal sealed record CollectedCompareRun(
    CompareRunArtifact Artifact,
    IReadOnlyList<ExecutionPlan> Plans,
    CanonicalComparisonResult Comparison)
{
    public string Variant => Artifact.Variant;
    public string ResultHash => Artifact.ResultHash;
}
