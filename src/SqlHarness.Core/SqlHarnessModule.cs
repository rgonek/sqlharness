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
    int OmittedMessages = 0)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

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
        : this(() => ProfileStore.Load(), SqlHarnessConfig.Default.Compare, SqlHarnessConfig.Default.Connection)
    {
    }

    public SqlHarnessModule(CompareConfig compareConfig)
        : this(compareConfig, null)
    {
    }

    public SqlHarnessModule(CompareConfig compareConfig, ConnectionConfig? connectionConfig)
        : this(() => ProfileStore.Load(), compareConfig, connectionConfig)
    {
    }

    /// <summary>
    /// Scoped composition root: the same session factories and session
    /// policy as the default module, but profile reads come from the
    /// supplied provider (for example an MCP frozen scope) instead of the
    /// global store. Construction opens no connection and performs no auth.
    /// </summary>
    public SqlHarnessModule(Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles)
        : this(loadProfiles, SqlHarnessConfig.Default.Compare, SqlHarnessConfig.Default.Connection)
    {
    }

    public SqlHarnessModule(Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles, CompareConfig compareConfig)
        : this(loadProfiles, compareConfig, null)
    {
    }

    public SqlHarnessModule(Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles, CompareConfig compareConfig, ConnectionConfig? connectionConfig)
        : this(
            new EngineSessionFactory(
                new SqlClientSessionFactory(new AzureCli(), (connectionConfig ?? SqlHarnessConfig.Default.Connection).TimeoutSeconds),
                new NpgsqlSessionFactory((connectionConfig ?? SqlHarnessConfig.Default.Connection).TimeoutSeconds)),
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

        if (operation is SqlHarnessAnnotationOperation annotation)
            return annotation.Run is null
                ? new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null)
                : await annotation.Run(ct);

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
            var parameterInputs = SqlParameterInputs.Resolve(query.Parameters, query.TypedParameters).ToArray();
            var tableValuedParameterNames = ParameterSetResolver.ValidateInputs(
                parameterInputs,
                target.Engine,
                mutation: query.UsageOverride == SqlUsage.Mutation);
            SqlParameterInputs.ValidateSourceSql(dialect, target.Database, parameterInputs, NoSessionTemps);
            var classificationExpansion = SqlParameterInputs.ExpandListsForClassification(query.Sql, parameterInputs);
            var safety = dialect.Classify(
                classificationExpansion.Text,
                query.UsageOverride ?? SqlUsage.Query,
                target.Database,
                query.AllowMutation,
                query.ConfirmDatabase,
                NoSessionTemps,
                tableValuedParameterNames);
            if (!safety.Allowed)
            {
                var message = $"SQL safety rejection: {safety.RejectionDescription}";
                if (operation is SqlHarnessQueryOperation && safety.Reason == SqlSafetyReason.MutationNotAllowed)
                    message += "\nPersistent changes use `sqlharness mutation`.";
                throw new SqlHarnessSafetyException(message);
            }
            var parameters = dialect.BindParameters(
                parameterInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
            var classificationParameters = SqlParameterInputs.CombineClassificationParameters(parameters, classificationExpansion.Parameters);
            dialect.ValidateParameterReferences(classificationParameters, classificationExpansion.Text);

            if (query.DryRun)
            {
                MissingParameterCheck.RequireSupplied(
                    target.Engine,
                    classificationParameters,
                    classificationExpansion.Text);
                return Checked(query, DryRunOutcome("query", BenchmarkReports.ClassificationLabel(safety), SqlRiskTiers.Label(safety.Tier), classificationParameters, 1,
                    benchmarkClassification: new BenchmarkClassificationReport("none", BenchmarkReports.ClassificationLabel(safety)),
                    notes: SqlParameterInputs.DryRunNotes(parameterInputs)));
            }

            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct, knownSecrets);
            phase = OperationPhase.Sql;

            var materialized = await TableParameterMaterializer.MaterializeAsync(session, parameterInputs, dialect, ct);
            var executionExpansion = materialized.Expand(query.Sql);
            var executionParameters = SqlParameterInputs.CombineClassificationParameters(materialized.Parameters, executionExpansion.Parameters);
            if (executionParameters.Count > ParameterSetResolver.MaximumCommandParameters)
                throw new SqlHarnessSafetyException($"SQL parameter list exceeds the {ParameterSetResolver.MaximumCommandParameters} final command parameter limit.");

            var execution = new SqlExecutionCommand(executionExpansion.Text, executionParameters, query.TimeoutSeconds)
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
                collected.OmittedMessageCount)
            {
                Warnings = (collected.Warnings ?? []).Concat(safety.Notices)
                    .Distinct(StringComparer.Ordinal).ToArray(),
            };
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
                SessionWarnings = SqlSessionWarningProjection.For(exception),
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
        int parameterSets,
        BenchmarkClassificationReport? benchmarkClassification = null,
        CompareClassificationReport? compareClassification = null,
        IReadOnlyList<string>? notes = null) =>
        WithReceipt(
            new SqlHarnessOutcome(
                SqlHarnessExitCode.Success,
                new SqlHarnessDryRunReport(
                    command,
                    classification,
                    tier,
                    BenchmarkReports.ToParameterReports(parameters),
                    parameterSets)
                {
                    BenchmarkClassification = benchmarkClassification,
                    CompareClassification = compareClassification,
                    Notes = notes ?? [],
                },
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
            RecordConditions(compare, () => ConditionsModelBuilder.Build(
                compare, target, prepared.Dialect, prepared.FixedParameters,
                [new ConditionsCaseInput("0", null, [])], storeSensitive: true));

            if (compare.DryRun)
            {
                MissingParameterCheck.RequireSupplied(
                    target.Engine,
                    prepared.ClassificationParameters,
                    new[]
                    {
                        prepared.ClassificationBaselineSql,
                        prepared.ClassificationCandidateSql,
                    }.Where(sql => sql is not null).Select(sql => sql!).ToArray());
                return DryRunOutcome(
                    "compare",
                    prepared.Classification.Baseline,
                    SqlRiskTiers.Label(prepared.Tier),
                    prepared.ClassificationParameters,
                    parameterSets: 1,
                    compareClassification: prepared.Classification,
                    notes: SqlParameterInputs.DryRunNotes(
                        SqlParameterInputs.Resolve(compare.Parameters, compare.TypedParameters).ToArray()));
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
                SafetyNotices = prepared.SafetyNotices,
                Owner = ArtifactOwner.From(compare.Target, prepared.Target),
                KnownSecrets = knownSecrets,
                OperationRef = compare.OperationRef,
                ParameterInputs = prepared.ParameterInputs,
                RecordMaterializedConditions = (caseIndex, details) => RecordConditions(compare, () =>
                    ConditionsModelBuilder.WithMaterializedParameters(
                        ConditionsModelBuilder.Build(compare, target, prepared.Dialect, prepared.FixedParameters,
                            [new ConditionsCaseInput("0", null, [])], storeSensitive: true),
                        caseIndex, details, storeSensitive: true,
                        fixedParameterNames: prepared.ParameterInputs.Select(input => input.Name).ToHashSet(StringComparer.OrdinalIgnoreCase))),
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
                Error: (exception as ArtifactStoragePreflightException)?.ToError(knownSecrets))
            {
                SessionWarnings = SqlSessionWarningProjection.For(exception),
            };
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
            foreach (var set in sets)
            {
                var caseInputs = prepared.ParameterInputs.Concat(set.Inputs).ToArray();
                SqlParameterInputs.ValidateSourceSql(dialect, prepared.Target.Database, caseInputs, NoSessionTemps);
                var caseTableNames = ParameterSetResolver.ValidateInputs(caseInputs, prepared.Target.Engine);
                var caseBaseline = SqlParameterInputs.ExpandListsForClassification(operation.BaselineSql, caseInputs);
                var caseCandidate = SqlParameterInputs.ExpandListsForClassification(operation.CandidateSql, caseInputs);
                var caseSetup = string.IsNullOrWhiteSpace(operation.SetupSql)
                    ? new ExpandedSql(operation.SetupSql ?? string.Empty, [])
                    : SqlParameterInputs.ExpandListsForClassification(operation.SetupSql, caseInputs);
                var caseSetupSafety = string.IsNullOrWhiteSpace(operation.SetupSql)
                    ? null
                    : dialect.Classify(caseSetup.Text, SqlUsage.CompareSetup, prepared.Target.Database, false, null, NoSessionTemps, caseTableNames);
                if (caseSetupSafety is not null)
                    CompareOperationPreparer.EnsureSafe(caseSetupSafety, "setup");
                var caseBaselineSafety = dialect.Classify(caseBaseline.Text, SqlUsage.Query, prepared.Target.Database, false, null,
                    caseSetupSafety?.SessionTempTables ?? NoSessionTemps, caseTableNames);
                var caseCandidateSafety = dialect.Classify(caseCandidate.Text, SqlUsage.Query, prepared.Target.Database, false, null,
                    caseSetupSafety?.SessionTempTables ?? NoSessionTemps, caseTableNames);
                CompareOperationPreparer.EnsureSafe(caseBaselineSafety, "baseline");
                CompareOperationPreparer.EnsureSafe(caseCandidateSafety, "candidate");
                var caseScalars = dialect.BindParameters(caseInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
                var caseBindings = SqlParameterInputs.CombineClassificationParameters(
                    SqlParameterInputs.CombineClassificationParameters(caseScalars, caseSetup.Parameters),
                    SqlParameterInputs.CombineClassificationParameters(caseBaseline.Parameters, caseCandidate.Parameters));
                SqlSetupVariableReferenceValidator.Validate(caseSetup.Text, caseBindings, caseBaseline.Text, caseCandidate.Text);
                dialect.ValidateParameterReferences(caseBindings, caseSetup.Text, caseBaseline.Text, caseCandidate.Text);
                SetupSqlExecution.Validate(dialect.Engine, caseSetup.Text, caseBindings);
                dialect.ValidateMeasuredBatch(caseBaseline.Text);
                dialect.ValidateMeasuredBatch(caseCandidate.Text);
            }
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
            RecordConditions(operation, () => ConditionsModelBuilder.Build(
                operation, target, dialect, fixedParameters,
                sets.Select(set => new ConditionsCaseInput(
                    set.Name,
                    matrixInput is null ? set.Name : null,
                    set.Parameters)).ToArray(),
                storeSensitive: true));

            if (operation.DryRun)
            {
                var representativeParameters = prepared.ClassificationParameters
                    .Where(parameter => !prepared.FixedParameters.Any(fixedParameter => fixedParameter.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var classificationSets = cellParameters
                    .Select(parameters => SqlParameterInputs.CombineClassificationParameters(parameters, representativeParameters))
                    .ToArray();
                for (var index = 0; index < cellParameters.Length; index++)
                    MissingParameterCheck.RequireSupplied(
                        target.Engine,
                        classificationSets[index],
                        new[]
                        {
                            prepared.ClassificationBaselineSql,
                            prepared.ClassificationCandidateSql,
                        }.Where(sql => sql is not null).Select(sql => sql!).ToArray());
                return DryRunOutcome(
                    "compare",
                    prepared.Classification.Baseline,
                    SqlRiskTiers.Label(prepared.Tier),
                    classificationSets[0],
                    cellParameters.Length,
                    compareClassification: prepared.Classification,
                    notes: SqlParameterInputs.DryRunNotes(
                        SqlParameterInputs.Resolve(operation.Parameters, operation.TypedParameters).ToArray()));
            }

            _artifactWriter.CheckStorage();

            var materializedCompareCases = new Dictionary<int, IReadOnlyList<MaterializedParameterInfo>>();
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
                SafetyNotices = prepared.SafetyNotices,
                Owner = ArtifactOwner.From(operation.Target, prepared.Target),
                KnownSecrets = knownSecrets,
                OperationRef = operation.OperationRef,
                ParameterInputs = prepared.ParameterInputs,
                RecordMaterializedConditions = (caseIndex, details) =>
                {
                    materializedCompareCases[caseIndex] = details;
                    var model = ConditionsModelBuilder.Build(operation, target, dialect, fixedParameters,
                        sets.Select(set => new ConditionsCaseInput(
                            set.Name,
                            matrixInput is null ? set.Name : null,
                            set.Parameters)).ToArray(), storeSensitive: true);
                    foreach (var item in materializedCompareCases.OrderBy(item => item.Key))
                        model = ConditionsModelBuilder.WithMaterializedParameters(model, item.Key, item.Value, storeSensitive: true,
                            fixedParameterNames: prepared.ParameterInputs.Select(input => input.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
                    RecordConditions(operation, () => model);
                },
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
                        _artifactWriter.WriteMatrixIndex(partialReport.Cells, failed.Coverage);
                    }
                    catch (Exception exception)
                    {
                        return WithReceipt(
                            MatrixOutcome(
                                SqlHarnessExitCode.LocalStorage,
                                partialReport,
                                SecretRedactor.Redact(exception, knownSecrets),
                                failed.Coverage,
                                failed.CompletedRuns) with
                            {
                                SessionWarnings = SqlSessionWarningProjection.For(failed.InnerException ?? failed),
                            },
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
                var message = FormatMatrixCellError(failed, knownSecrets);
                return WithReceipt(
                    MatrixOutcome(exitCode, failed.PartialReport, message, failed.Coverage, failed.CompletedRuns) with
                    {
                        SessionWarnings = SqlSessionWarningProjection.For(failed.InnerException ?? failed),
                    },
                    rawFootprint);
            }

            rawFootprint = result.RawFootprint;
            phase = OperationPhase.Artifact;
            _artifactWriter.WriteMatrixIndex(result.Report.Cells, result.Coverage);
            var success = MatrixOutcome(SqlHarnessExitCode.Success, result.Report, null, result.Coverage, result.Runs);
            return WithReceipt(success, rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = phase == OperationPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, knownSecrets),
                Error: (exception as ArtifactStoragePreflightException)?.ToError(knownSecrets))
            {
                SessionWarnings = SqlSessionWarningProjection.For(exception),
            };
            return WithReceipt(failure, rawFootprint);
        }
    }

    private static SqlHarnessOutcome MatrixOutcome(
        SqlHarnessExitCode exitCode,
        object? report,
        string? safeError,
        MatrixExecutionCoverage coverage,
        IReadOnlyList<CompareRunArtifact>? runs)
    {
        var error = report is null && safeError is not null
            ? SqlHarnessError.From(exitCode, safeError) with { MatrixStop = coverage }
            : null;
        return new SqlHarnessOutcome(exitCode, report, safeError, Error: error)
        {
            BenchmarkRuns = runs,
            Coverage = coverage,
        };
    }

    private static void RecordConditions(SqlHarnessOperation operation, Func<ConditionsModel> build)
    {
        try
        {
            operation.RecordConditions?.Invoke(build());
        }
        catch (Exception)
        {
            // A journal failure must not change SQL execution or its reported outcome.
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
            var parameterInputs = SqlParameterInputs.Resolve(measure.Parameters, measure.TypedParameters).ToArray();
            SqlParameterInputs.ValidateSourceSql(dialect, target.Database, parameterInputs, NoSessionTemps);
            var parameters = dialect.BindParameters(
                parameterInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
            var parameterSets = ResolveMeasureParameterSets(measure, dialect, parameters);
            var representativeSetInputs = parameterSets is { Count: > 0 }
                ? SqlParameterInputs.Resolve(parameterSets[0].Parameters, parameterSets[0].TypedParameters).ToArray()
                : [];
            var classificationInputs = parameterInputs.Concat(representativeSetInputs).ToArray();
            SqlParameterInputs.ValidateSourceSql(dialect, target.Database, classificationInputs, NoSessionTemps);
            var tableValuedNames = ParameterSetResolver.ValidateInputs(classificationInputs, target.Engine);
            var queryExpansion = SqlParameterInputs.ExpandListsForClassification(measure.QuerySql, classificationInputs);
            var setupExpansion = string.IsNullOrWhiteSpace(measure.SetupSql)
                ? new ExpandedSql(measure.SetupSql ?? string.Empty, [])
                : SqlParameterInputs.ExpandListsForClassification(measure.SetupSql, classificationInputs);
            var representativeListParameters = SqlParameterInputs.DeduplicateClassificationListParameters(
                setupExpansion.Parameters,
                queryExpansion.Parameters);
            var classificationParameters = SqlParameterInputs.CombineClassificationParameters(parameters, representativeListParameters);
            SqlSafetyDecision? setupSafety = null;
            IReadOnlySet<string> setupTemps = NoSessionTemps;
            if (!string.IsNullOrWhiteSpace(measure.SetupSql))
            {
                setupSafety = dialect.Classify(
                    setupExpansion.Text, SqlUsage.CompareSetup, target.Database, false, null, NoSessionTemps, tableValuedNames);
                CompareOperationPreparer.EnsureSafe(setupSafety, "setup");
                setupTemps = setupSafety.SessionTempTables;
            }

            var querySafety = dialect.Classify(
                queryExpansion.Text, SqlUsage.Query, target.Database, false, null, setupTemps, tableValuedNames);
            CompareOperationPreparer.EnsureSafe(querySafety, "query");
            if (parameterSets is not null)
            {
                foreach (var parameterSet in parameterSets)
                {
                    var caseInputs = parameterInputs.Concat(SqlParameterInputs.Resolve(parameterSet.Parameters, parameterSet.TypedParameters)).ToArray();
                    SqlParameterInputs.ValidateSourceSql(dialect, target.Database, caseInputs, NoSessionTemps);
                    var caseTableNames = ParameterSetResolver.ValidateInputs(caseInputs, target.Engine);
                    var caseQuery = SqlParameterInputs.ExpandListsForClassification(measure.QuerySql, caseInputs);
                    var caseSetup = string.IsNullOrWhiteSpace(measure.SetupSql)
                        ? new ExpandedSql(measure.SetupSql ?? string.Empty, [])
                        : SqlParameterInputs.ExpandListsForClassification(measure.SetupSql, caseInputs);
                    var caseSetupSafety = string.IsNullOrWhiteSpace(measure.SetupSql)
                        ? null
                        : dialect.Classify(caseSetup.Text, SqlUsage.CompareSetup, target.Database, false, null, NoSessionTemps, caseTableNames);
                    if (caseSetupSafety is not null)
                        CompareOperationPreparer.EnsureSafe(caseSetupSafety, "setup");
                    var caseQuerySafety = dialect.Classify(caseQuery.Text, SqlUsage.Query, target.Database, false, null,
                        caseSetupSafety?.SessionTempTables ?? NoSessionTemps, caseTableNames);
                    CompareOperationPreparer.EnsureSafe(caseQuerySafety, "query");
                    IReadOnlyList<SqlHarnessParameter> caseScalar;
                    try
                    {
                        caseScalar = dialect.BindParameters(caseInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
                    }
                    catch (SqlHarnessSafetyException exception) when (exception.IsParameterValue)
                    {
                        throw exception.WithParameterValue($"Parameter set '{parameterSet.Name}' is invalid: {exception.Message}");
                    }
                    var caseBindings = SqlParameterInputs.CombineClassificationParameters(
                        SqlParameterInputs.CombineClassificationParameters(caseScalar, caseSetup.Parameters), caseQuery.Parameters);
                    if (target.Engine == SqlEngine.SqlServer)
                        SqlSetupVariableReferenceValidator.Validate(caseSetup.Text, caseBindings, caseQuery.Text);
                    dialect.ValidateParameterReferences(caseBindings, caseSetup.Text, caseQuery.Text);
                    SetupSqlExecution.Validate(dialect.Engine, caseSetup.Text, caseBindings);
                    dialect.ValidateMeasuredBatch(caseQuery.Text);
                }
            }
            if (parameterSets is null)
                RecordConditions(measure, () => ConditionsModelBuilder.Build(
                    measure, target, dialect, parameters,
                    [new ConditionsCaseInput("0", null, [])], storeSensitive: true));
            if (dialect.Engine == SqlEngine.SqlServer && parameterSets is null)
                SqlSetupVariableReferenceValidator.Validate(setupExpansion.Text, classificationParameters, queryExpansion.Text);
            dialect.ValidateParameterReferences(classificationParameters, setupExpansion.Text, queryExpansion.Text);
            SetupSqlExecution.Validate(dialect.Engine, setupExpansion.Text, classificationParameters);
            dialect.ValidateMeasuredBatch(queryExpansion.Text);

            if (parameterSets is not null)
            {
                var boundSets = BindMeasureParameterSets(dialect, measure, parameterSets);
                var representativeParameters = classificationParameters
                    .Where(parameter => !parameters.Any(fixedParameter => fixedParameter.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var classificationSets = boundSets
                    .Select(set => set with
                    {
                        Parameters = SqlParameterInputs.CombineClassificationParameters(set.Parameters, representativeParameters),
                    })
                    .ToArray();
                var fixedNames = new HashSet<string>(parameters.Select(parameter => parameter.Name), StringComparer.OrdinalIgnoreCase);
                var materializedMeasureCases = new Dictionary<int, IReadOnlyList<MaterializedParameterInfo>>();
                RecordConditions(measure, () => ConditionsModelBuilder.Build(
                    measure, target, dialect, parameters,
                    boundSets.Select(set => new ConditionsCaseInput(
                        set.Name,
                        measure.TypedMatrix is null ? set.Name : null,
                        set.Parameters.Where(parameter => !fixedNames.Contains(parameter.Name)).ToArray())).ToArray(),
                    storeSensitive: true));
                if (measure.DryRun)
                {
                    foreach (var classificationSet in classificationSets)
                        MissingParameterCheck.RequireSupplied(target.Engine, classificationSet.Parameters, queryExpansion.Text);
                    return DryRunOutcome(
                        "measure",
                        BenchmarkReports.ClassificationLabel(querySafety),
                        SqlRiskTiers.Label(SqlRiskTiers.Max(querySafety.Tier, setupSafety?.Tier ?? SqlRiskTier.ReadOnly)),
                        classificationSets[0].Parameters,
                        boundSets.Count,
                        benchmarkClassification: new BenchmarkClassificationReport(
                            BenchmarkReports.ClassificationLabel(setupSafety), BenchmarkReports.ClassificationLabel(querySafety)),
                        notes: SqlParameterInputs.DryRunNotes(parameterInputs));
                }

                _artifactWriter.CheckStorage();
                phase = OperationPhase.Authentication;
                phase = OperationPhase.Sql;

                raw = new CanonicalResultAccumulator();
                var execution = await new MeasureParameterSetRunner(dialect, _sessionFactory, ComparisonMaximumRows).ExecuteAsync(
                    target,
                    measure,
                    boundSets,
                    raw,
                    ct,
                    setupSafety,
                    knownSecrets,
                    (caseIndex, details) =>
                    {
                        materializedMeasureCases[caseIndex] = details;
                        var model = ConditionsModelBuilder.Build(measure, target, dialect, parameters,
                            boundSets.Select(set => new ConditionsCaseInput(
                                set.Name,
                                measure.TypedMatrix is null ? set.Name : null,
                                set.Parameters.Where(parameter => !fixedNames.Contains(parameter.Name)).ToArray())).ToArray(),
                            storeSensitive: true);
                        foreach (var item in materializedMeasureCases.OrderBy(item => item.Key))
                            model = ConditionsModelBuilder.WithMaterializedParameters(model, item.Key, item.Value, storeSensitive: true,
                                fixedParameterNames: parameterInputs.Select(input => input.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
                        RecordConditions(measure, () => model);
                    });
                await using var multiSession = execution.FinalSession!;
                rawFootprint = raw.Complete().Footprint;
                var setReport = MeasureParameterSetReportProjector.Project(
                    multiSession.Identity,
                    measure.Repeat,
                    execution,
                    boundSets) with
                {
                    Warnings = execution.Warnings
                        .Concat(setupSafety?.Notices ?? [])
                        .Concat(querySafety.Notices)
                        .Distinct(StringComparer.Ordinal).ToArray(),
                };

                // Still the measured-SQL phase: a catalog failure stays a gap and cancellation maps like a measured batch.
                var setInventory = await IndexInventoryCapture.CaptureAsync(
                    dialect.Engine,
                    multiSession,
                    execution.Runs.SelectMany(run => run.Artifact.PlanXmls).ToArray(),
                    measure.TimeoutSeconds,
                    knownSecrets,
                    ct);
                phase = OperationPhase.Artifact;
                var setDirectory = _artifactWriter.Write(
                    setReport,
                    execution.Runs.Select(run => run.Artifact).ToArray(),
                    target.Database,
                    ArtifactOwner.From(measure.Target, target),
                    measure.OperationRef,
                    setInventory);
                setReport = setReport with { ArtifactDirectory = setDirectory };
                var setSuccess = new SqlHarnessOutcome(SqlHarnessExitCode.Success, setReport, null)
                {
                    BenchmarkRuns = execution.Runs.Select(run => run.Artifact).ToArray(),
                };
                return WithReceipt(setSuccess, rawFootprint);
            }

            if (measure.DryRun)
            {
                MissingParameterCheck.RequireSupplied(target.Engine, classificationParameters, queryExpansion.Text);
                return DryRunOutcome(
                    "measure",
                    BenchmarkReports.ClassificationLabel(querySafety),
                    SqlRiskTiers.Label(SqlRiskTiers.Max(querySafety.Tier, setupSafety?.Tier ?? SqlRiskTier.ReadOnly)),
                    classificationParameters,
                    parameterSets: 1,
                    benchmarkClassification: new BenchmarkClassificationReport(
                        BenchmarkReports.ClassificationLabel(setupSafety), BenchmarkReports.ClassificationLabel(querySafety)),
                    notes: SqlParameterInputs.DryRunNotes(parameterInputs));
            }

            _artifactWriter.CheckStorage();
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct, knownSecrets);
            phase = OperationPhase.Sql;

            var materialized = await TableParameterMaterializer.MaterializeAsync(session, parameterInputs, dialect, ct);
            RecordConditions(measure, () => ConditionsModelBuilder.WithMaterializedParameters(
                ConditionsModelBuilder.Build(measure, target, dialect, parameters,
                    [new ConditionsCaseInput("0", null, [])], storeSensitive: true),
                0, materialized.Details, storeSensitive: true,
                fixedParameterNames: parameterInputs.Select(input => input.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)));
            var setupExecution = string.IsNullOrWhiteSpace(measure.SetupSql) ? null : materialized.Expand(measure.SetupSql);
            var queryExecution = materialized.Expand(measure.QuerySql);
            var setupParameters = setupExecution is null ? materialized.Parameters : SqlParameterInputs.CombineClassificationParameters(materialized.Parameters, setupExecution.Parameters);
            var queryParameters = SqlParameterInputs.CombineClassificationParameters(materialized.Parameters, queryExecution.Parameters);

            raw = new CanonicalResultAccumulator();
            var setupWarnings = await SetupSqlExecution.ExecuteSetupAsync(
                dialect.Engine,
                setupExecution?.Text ?? measure.SetupSql,
                setupParameters,
                measure.TimeoutSeconds,
                session,
                raw: null,
                ct,
                setupSafety);
            var sessionState = await dialect.CaptureSessionStateAsync(session, ct);

            // Measure never runs ResultComparer; skip fingerprint retention and the 1M row comparison cap.
            await ExecuteBenchmarkRunAsync(dialect, session, queryExecution.Text, queryParameters, measure.TimeoutSeconds, 0, "measure", raw: null, captureComparison: false, sessionState, ct);
            var runs = new List<CollectedBenchmarkRun>(measure.Repeat);
            for (var repetition = 1; repetition <= measure.Repeat; repetition++)
                runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, queryExecution.Text, queryParameters, measure.TimeoutSeconds, repetition, "measure", repetition == 1 ? raw : null, captureComparison: false, sessionState, ct));

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
                Warnings = setupWarnings
                    .Concat(setupSafety?.Notices ?? [])
                    .Concat(querySafety.Notices)
                    .Concat(runs.SelectMany(run => run.Warnings))
                    .Distinct(StringComparer.Ordinal).ToArray(),
            };

            // Still the measured-SQL phase: a catalog failure stays a gap and cancellation maps like a measured batch.
            var inventory = await IndexInventoryCapture.CaptureAsync(
                dialect.Engine,
                session,
                runs.SelectMany(run => run.Artifact.PlanXmls).ToArray(),
                measure.TimeoutSeconds,
                knownSecrets,
                ct);
            phase = OperationPhase.Artifact;
            var directory = _artifactWriter.Write(
                report,
                runs.Select(run => run.Artifact).ToArray(),
                target.Database,
                ArtifactOwner.From(measure.Target, target),
                measure.OperationRef,
                inventory);
            report = report with { ArtifactDirectory = directory };
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null)
            {
                BenchmarkRuns = runs.Select(run => run.Artifact).ToArray(),
            };
            return WithReceipt(success, rawFootprint);
        }
        catch (MeasureCaseFailedException failed)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var failurePhase = string.Equals(failed.Execution.FailedCase?.Phase, "authentication", StringComparison.Ordinal)
                ? OperationPhase.Authentication
                : OperationPhase.Sql;
            var exitCode = OperationFailureMapper.Map(failed.InnerException ?? failed, failurePhase);
            var original = failed.InnerException ?? failed;
            var detail = SecretRedactor.Redact(original, knownSecrets);
            var failedCase = failed.Execution.FailedCase!;
            var safeError = $"Measure parameter set {failedCase.Index} ('{failedCase.SetName}') failed. {detail}";
            var coverage = MatrixExecutionCoverage.IdentifiedFailure(
                failed.RequestedCases,
                failed.Execution.CompletedCases,
                new MatrixFailedCase(failedCase.Index, SetName: failedCase.SetName, Phase: failedCase.Phase));
            var error = SqlHarnessError.From(exitCode, safeError) with { MatrixStop = coverage };
            var failure = new SqlHarnessOutcome(exitCode, failed.PartialReport, safeError, Error: error)
            {
                BenchmarkRuns = failed.CompletedRuns.Select(run => run.Artifact).ToArray(),
                Coverage = coverage,
                SessionWarnings = SqlSessionWarningProjection.For(failed.InnerException ?? failed),
            };
            return WithReceipt(failure, rawFootprint);
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var exitCode = phase == OperationPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, knownSecrets),
                Error: (exception as ArtifactStoragePreflightException)?.ToError(knownSecrets))
            {
                SessionWarnings = SqlSessionWarningProjection.For(exception),
            };
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
        SessionState? sessionState,
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
            ct,
            sessionState);

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
            var parameterInputs = SqlParameterInputs.Resolve(watch.Parameters, watch.TypedParameters).ToArray();
            SqlParameterInputs.ValidateSourceSql(dialect, target.Database, parameterInputs, NoSessionTemps);
            var tableValuedNames = ParameterSetResolver.ValidateInputs(parameterInputs, target.Engine);
            var expansion = SqlParameterInputs.ExpandListsForClassification(watch.Sql, parameterInputs);
            var safety = dialect.Classify(
                expansion.Text,
                SqlUsage.Query,
                target.Database,
                allowMutation: false,
                confirmDatabase: null,
                NoSessionTemps,
                tableValuedNames);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");
            var parameters = dialect.BindParameters(parameterInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
            var classificationParameters = SqlParameterInputs.CombineClassificationParameters(parameters, expansion.Parameters);
            dialect.ValidateParameterReferences(classificationParameters, expansion.Text);
            // Predicate syntax is validated before authentication so bad --until fails closed.
            if (watch.Until is not null)
                _ = WatchCondition.Parse(watch.Until);

            phase = OperationPhase.Authentication;
            var runner = new WatchRunner(_sessionFactory, _watchClock);
            var (outcome, raw) = await runner.ExecuteAsync(
                watch,
                target,
                parameters,
                parameterInputs,
                knownSecrets,
                ct);
            rawFootprint = raw;
            if (outcome.Report is SqlHarnessWatchReport watchReport)
                outcome = outcome with
                {
                    Report = watchReport with { Warnings = watchReport.Warnings.Concat(safety.Notices).Distinct(StringComparer.Ordinal).ToArray() },
                    SessionWarnings = outcome.SessionWarnings.Concat(safety.Notices).Distinct(StringComparer.Ordinal).ToArray(),
                };
            // Runner already mapped connect/SQL failures; preserve its exit code and report.
            return WithReceipt(outcome, rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(
                exitCode,
                null,
                SecretRedactor.Redact(exception, knownSecrets))
            {
                SessionWarnings = SqlSessionWarningProjection.For(exception),
            };
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
            var parameterInputs = SqlParameterInputs.Resolve(watch.Parameters, watch.TypedParameters).ToArray();
            SqlParameterInputs.ValidateSourceSql(dialect, target.Database, parameterInputs, NoSessionTemps);
            var tableValuedNames = ParameterSetResolver.ValidateInputs(parameterInputs, target.Engine);
            var expansion = SqlParameterInputs.ExpandListsForClassification(watch.Sql, parameterInputs);
            var safety = dialect.Classify(
                expansion.Text,
                SqlUsage.Query,
                target.Database,
                allowMutation: false,
                confirmDatabase: null,
                NoSessionTemps,
                tableValuedNames);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");
            var parameters = dialect.BindParameters(parameterInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
            var classificationParameters = SqlParameterInputs.CombineClassificationParameters(parameters, expansion.Parameters);
            dialect.ValidateParameterReferences(classificationParameters, expansion.Text);
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
                parameterInputs,
                knownSecrets,
                stream,
                ct);
            rawFootprint = raw;
            outcome = outcome with
            {
                SessionWarnings = outcome.SessionWarnings.Concat(safety.Notices).Distinct(StringComparer.Ordinal).ToArray(),
            };
            // Runner already mapped connect/SQL failures; preserve its exit code.
            return Checked(watch, WithReceipt(outcome, rawFootprint) with { OperationRef = watch.OperationRef });
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var message = SecretRedactor.Redact(exception, knownSecrets);
            EmitWatchNdjsonFailed(writer, exitCode, SqlHarnessError.From(exitCode, message, "validation"));
            var failure = new SqlHarnessOutcome(exitCode, null, message)
            {
                SessionWarnings = SqlSessionWarningProjection.For(exception),
            };
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
            var parameterInputs = SqlParameterInputs.Resolve(snapshot.Parameters, snapshot.TypedParameters).ToArray();
            SqlParameterInputs.ValidateSourceSql(dialect, target.Database, parameterInputs, NoSessionTemps);
            var tableValuedNames = ParameterSetResolver.ValidateInputs(parameterInputs, target.Engine);
            var expansion = SqlParameterInputs.ExpandListsForClassification(snapshot.Sql, parameterInputs);
            var safety = dialect.Classify(
                expansion.Text,
                SqlUsage.Query,
                target.Database,
                allowMutation: false,
                confirmDatabase: null,
                NoSessionTemps,
                tableValuedNames);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");
            var parameters = dialect.BindParameters(parameterInputs.Where(input => ParameterKindParser.Parse(input) == ParameterKind.Scalar));
            var classificationParameters = SqlParameterInputs.CombineClassificationParameters(parameters, expansion.Parameters);
            dialect.ValidateParameterReferences(classificationParameters, expansion.Text);
            phase = OperationPhase.Authentication;
            var runner = new SnapshotRunner(_sessionFactory, _snapshotStore);
            var (outcome, raw) = await runner.ExecuteAsync(
                snapshot,
                target,
                parameters,
                parameterInputs,
                knownSecrets,
                ct);
            rawFootprint = raw;
            if (outcome.Report is SqlHarnessSnapshotReport snapshotReport)
                outcome = outcome with
                {
                    Report = snapshotReport with { Warnings = snapshotReport.Warnings.Concat(safety.Notices).Distinct(StringComparer.Ordinal).ToArray() },
                    SessionWarnings = outcome.SessionWarnings.Concat(safety.Notices).Distinct(StringComparer.Ordinal).ToArray(),
                };
            // Runner already mapped connect/SQL/storage failures; preserve its exit code and report.
            return WithReceipt(outcome, rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var failure = new SqlHarnessOutcome(
                exitCode,
                null,
                SecretRedactor.Redact(exception, knownSecrets))
            {
                SessionWarnings = SqlSessionWarningProjection.For(exception),
            };
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