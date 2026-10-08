using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

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
    private readonly IGainSource _gainSource;
    private readonly ICompareArtifactWriter _artifactWriter;
    private readonly CompareCellRunner _cellRunner;
    private readonly CompareMatrixRunner _matrixRunner;
    private readonly Func<IReadOnlyDictionary<string, TargetProfile>> _loadProfiles;
    private readonly IWatchClock _watchClock;
    private readonly ISnapshotStore _snapshotStore;
    private readonly IQueryStoreArtifactWriter _queryStoreArtifacts;
    private readonly IIndexAnalysisArtifactWriter _indexAnalysisArtifacts;

    private const string IndexEvidenceWarning =
        "Missing-index evidence is cumulative since SQL Server start and can be shortened or reset by restart, failover, index DDL, or a DMV clear.";

    /// <summary>
    /// Row cap for result-fingerprint retention. Production uses
    /// <see cref="CanonicalComparisonAccumulator.MaximumComparedRows"/>; tests may inject a lower limit.
    /// Only applied when comparison capture is enabled (compare with ordered/multiset/set).
    /// </summary>
    internal int ComparisonMaximumRows { get; init; } = CanonicalComparisonAccumulator.MaximumComparedRows;

    public SqlHarnessModule()
        : this(() => ProfileStore.Load())
    {
    }

    /// <summary>
    /// Scoped composition root: the same session factories and session
    /// policy as the default module, but profile reads come from the
    /// supplied provider (for example an MCP frozen scope) instead of the
    /// global store. Construction opens no connection and performs no auth.
    /// </summary>
    public SqlHarnessModule(Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles)
        : this(
            new EngineSessionFactory(new SqlClientSessionFactory(new AzureCli()), new NpgsqlSessionFactory()),
            new JournalGainStore(SqlHarnessPaths.ActivityDatabase, () => SqlHarnessConfigLoader.Load().Config.Journal.Enabled),
            new CompareArtifactWriter(),
            loadProfiles,
            queryStoreArtifacts: new QueryStoreArtifactWriter(),
            indexAnalysisArtifacts: new IndexAnalysisArtifactWriter())
    {
    }

    internal SqlHarnessModule(
        ISqlSessionFactory sessionFactory,
        IGainSource gainSource,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
        IWatchClock? watchClock = null,
        ISnapshotStore? snapshotStore = null,
        IQueryStoreArtifactWriter? queryStoreArtifacts = null,
        IIndexAnalysisArtifactWriter? indexAnalysisArtifacts = null)
        : this(
            sessionFactory,
            gainSource,
            new CompareArtifactWriter(),
            loadProfiles,
            watchClock,
            snapshotStore,
            queryStoreArtifacts,
            indexAnalysisArtifacts)
    {
    }

    internal SqlHarnessModule(
        ISqlSessionFactory sessionFactory,
        IGainSource gainSource,
        ICompareArtifactWriter artifactWriter,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles,
        IWatchClock? watchClock = null,
        ISnapshotStore? snapshotStore = null,
        IQueryStoreArtifactWriter? queryStoreArtifacts = null,
        IIndexAnalysisArtifactWriter? indexAnalysisArtifacts = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _gainSource = gainSource ?? throw new ArgumentNullException(nameof(gainSource));
        _artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
        _cellRunner = new CompareCellRunner(_sessionFactory, _artifactWriter);
        _matrixRunner = new CompareMatrixRunner(_cellRunner);
        _loadProfiles = loadProfiles ?? throw new ArgumentNullException(nameof(loadProfiles));
        _watchClock = watchClock ?? new SystemWatchClock();
        _snapshotStore = snapshotStore ?? new SnapshotStore(SqlHarnessPaths.SnapshotsDir);
        _queryStoreArtifacts = queryStoreArtifacts ?? new QueryStoreArtifactWriter();
        _indexAnalysisArtifacts = indexAnalysisArtifacts ?? new IndexAnalysisArtifactWriter();
    }

    public async Task<SqlHarnessOutcome> ExecuteAsync(
        SqlHarnessOperation operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation is SqlHarnessGainOperation)
        {
            try
            {
                return Checked(operation, new SqlHarnessOutcome(SqlHarnessExitCode.Success, _gainSource.Aggregate(), null));
            }
            catch (Exception exception)
            {
                return new SqlHarnessOutcome(
                    SqlHarnessExitCode.LocalStorage,
                    null,
                    SecretRedactor.Redact(exception, []));
            }
        }

        if (operation is SqlHarnessPlanOperation plan)
            return Checked(plan, ExecutePlan(plan));

        if (operation is SqlHarnessCompareOperation compare)
            return Checked(compare, await ExecuteCompareAsync(compare, ct));

        if (operation is SqlHarnessCompareMatrixOperation matrix)
            return Checked(matrix, await ExecuteCompareMatrixAsync(matrix, ct));

        if (operation is SqlHarnessMeasureOperation measure)
            return Checked(measure, await ExecuteMeasureAsync(measure, ct));

        if (operation is SqlHarnessSchemaOperation schema)
            return Checked(schema, await ExecuteSchemaAsync(schema, ct));

        if (operation is SqlHarnessPingOperation ping)
            return Checked(ping, await ExecutePingAsync(ping, ct));

        if (operation is SqlHarnessCountsOperation counts)
            return Checked(counts, await ExecuteCountsAsync(counts, ct));

        if (operation is SqlHarnessSpaceOperation space)
            return Checked(space, await ExecuteSpaceAsync(space, ct));

        if (operation is SqlHarnessWatchOperation watch)
            return Checked(watch, await ExecuteWatchAsync(watch, ct));

        if (operation is SqlHarnessSnapshotOperation snapshot)
            return Checked(snapshot, await ExecuteSnapshotAsync(snapshot, ct));

        if (operation is SqlHarnessQueryStoreTopOperation qstop)
            return Checked(qstop, await ExecuteQueryStoreTopAsync(qstop, ct));

        if (operation is SqlHarnessIndexesOperation indexes)
            return Checked(indexes, await ExecuteIndexesAsync(indexes, ct));

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
        var knownSecrets = new List<string> { query.Sql };
        knownSecrets.AddRange(query.Parameters.Where(value => !string.IsNullOrEmpty(value)));
        SqlParameterSecrets.AddValues(knownSecrets, query.TypedParameters);

        try
        {
            ValidateBounds(query);
            var target = TargetResolver.Resolve(query.Target, _loadProfiles());
            var dialect = SqlDialects.For(target.Engine);
            var safety = dialect.Classify(
                query.Sql,
                SqlUsage.Query,
                target.Database,
                query.AllowMutation,
                query.ConfirmDatabase,
                NoSessionTemps);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.RejectionDescription}");
            SqlParameterSecrets.AddValues(knownSecrets, query.Parameters);
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(query.Parameters, query.TypedParameters));
            dialect.ValidateParameterReferences(parameters, query.Sql);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;

            var execution = new SqlExecutionCommand(query.Sql, parameters, query.TimeoutSeconds);
            var collected = await QueryResultCollector.CollectAsync(
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
                SecretRedactor.Redact(exception, knownSecrets));
            return Checked(query, WithReceipt(failure, rawFootprint));
        }
        finally
        {
            raw?.Dispose();
        }
    }

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

    /// <summary>
    /// Attaches the emission receipt: it carries the raw footprint for the activity journal
    /// (JournalingModule records raw and emitted footprints) and returns the outcome's exit code.
    /// </summary>
    private static SqlHarnessOutcome WithReceipt(SqlHarnessOutcome outcome, OutputFootprint raw)
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(outcome.ExitCode))
        {
            RawFootprint = raw,
        };
        return outcome with { EmissionReceipt = receipt };
    }

    private async Task<SqlHarnessOutcome> ExecuteCompareAsync(SqlHarnessCompareOperation compare, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string> { compare.BaselineSql, compare.CandidateSql };
        if (!string.IsNullOrWhiteSpace(compare.SetupSql))
            knownSecrets.Add(compare.SetupSql);
        knownSecrets.AddRange(compare.Parameters.Where(value => !string.IsNullOrEmpty(value)));
        SqlParameterSecrets.AddValues(knownSecrets, compare.TypedParameters);

        try
        {
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
            foreach (var parameter in prepared.FixedParameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            CompareOperationPreparer.ValidateVariants(
                prepared,
                compare.SetupSql,
                compare.BaselineSql,
                compare.CandidateSql,
                [prepared.FixedParameters]);
            var target = prepared.Target;

            _artifactWriter.CheckStorage();

            var request = new CompareCellRequest(
                target,
                compare.SetupSql,
                compare.BaselineSql,
                compare.CandidateSql,
                prepared.FixedParameters,
                compare.TimeoutSeconds,
                compare.Repeat,
                compare.CompareResults)
            {
                Classification = prepared.Classification,
                Owner = ArtifactOwner.From(compare.Target, prepared.Target),
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
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string> { operation.BaselineSql, operation.CandidateSql, operation.Matrix };
        if (!string.IsNullOrWhiteSpace(operation.SetupSql))
            knownSecrets.Add(operation.SetupSql);
        knownSecrets.AddRange(operation.Parameters.Where(value => !string.IsNullOrEmpty(value)));
        SqlParameterSecrets.AddValues(knownSecrets, operation.TypedParameters);
        SqlParameterSecrets.AddMatrixValues(knownSecrets, operation.TypedMatrix);

        try
        {
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
            SqlParameterSecrets.AddMatrixValues(knownSecrets, operation.Matrix);

            // The fixed parameters are already bound, so their names need no second parse.
            var matrix = SqlParameterMatrixParser.Bind(
                SqlParameterInputs.ResolveMatrix(operation.Matrix, operation.TypedMatrix),
                prepared.FixedParameters.Select(parameter => parameter.Name));
            foreach (var displayValue in matrix.DisplayValues)
            {
                if (!string.IsNullOrEmpty(displayValue))
                    knownSecrets.Add(displayValue);
            }

            var fixedParameters = prepared.FixedParameters;
            var dialect = prepared.Dialect;
            foreach (var parameter in fixedParameters)
                AddTypedSecret(knownSecrets, parameter);

            // Second bind through the dialect: the engine may reject a type the shared binder accepts.
            var matrixName = MatrixParameterName(matrix);
            var matrixParameters = new List<SqlHarnessParameter>(matrix.DisplayValues.Count);
            foreach (var displayValue in matrix.DisplayValues)
            {
                var bound = dialect.BindParameters([new SqlHarnessParameterInput(matrixName, matrix.Type, displayValue)]);
                if (bound.Count != 1)
                    throw new SqlHarnessSafetyException($"The --matrix option for SQL parameter '{matrix.Name}' is invalid.");
                matrixParameters.Add(bound[0]);
                AddTypedSecret(knownSecrets, bound[0]);
            }

            CompareOperationPreparer.ValidateVariants(
                prepared,
                operation.SetupSql,
                operation.BaselineSql,
                operation.CandidateSql,
                matrixParameters.Select(matrixParameter => (IReadOnlyList<SqlHarnessParameter>)[.. fixedParameters, matrixParameter]).ToArray());

            var target = prepared.Target;
            _artifactWriter.CheckStorage();

            var template = new CompareCellRequest(
                target,
                operation.SetupSql,
                operation.BaselineSql,
                operation.CandidateSql,
                fixedParameters,
                operation.TimeoutSeconds,
                operation.Repeat,
                operation.CompareResults)
            {
                Classification = prepared.Classification,
                Owner = ArtifactOwner.From(operation.Target, prepared.Target),
            };
            _matrixRunner.ComparisonMaximumRows = ComparisonMaximumRows;
            CompareMatrixResult result;
            try
            {
                result = await _matrixRunner.RunAsync(
                    new CompareMatrixRun(
                        template,
                        fixedParameters,
                        matrixParameters,
                        matrix.DisplayValues,
                        matrix.Name,
                        matrix.Type),
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
                var exitCode = phase == OperationPhase.Artifact
                    ? SqlHarnessExitCode.LocalStorage
                    : OperationFailureMapper.Map(failed.InnerException ?? failed, phase);
                return WithReceipt(
                    new SqlHarnessOutcome(exitCode, failed.PartialReport, FormatMatrixCellError(failed, knownSecrets)),
                    rawFootprint);
            }

            rawFootprint = result.RawFootprint;
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

    private static void AddTypedSecret(List<string> knownSecrets, SqlHarnessParameter parameter)
    {
        if (parameter.Value is DBNull)
            return;

        // Convert.ToString(byte[]) is "System.Byte[]", which does not match a server-quoted Base64 value.
        if (parameter.Value is byte[] bytes)
        {
            knownSecrets.Add(Convert.ToBase64String(bytes));
            return;
        }

        knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string MatrixParameterName(ParsedParameterMatrix matrix)
    {
        if (matrix.Name.Length < 2 || matrix.Name[0] != '@')
            throw new SqlHarnessSafetyException("The --matrix option is invalid.");
        return matrix.Name[1..];
    }

    private static string FormatMatrixCellError(CompareMatrixCellFailedException failed, IReadOnlyList<string> knownSecrets)
    {
        var original = failed.InnerException ?? failed;
        // Longer secrets first, so "1" cannot split "100" into "[REDACTED]00".
        // Prefix the cell label after redaction so a value of "1" cannot erase the index.
        var detail = SecretRedactor.Redact(original, LongestFirst(knownSecrets));
        return $"Comparison matrix cell {failed.Index} for SQL parameter '{failed.ParameterName}' failed. {detail}";
    }

    private static IReadOnlyList<string> LongestFirst(IReadOnlyList<string> secrets) =>
        secrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .OrderByDescending(secret => secret.Length)
            .ToArray();

    private async Task<SqlHarnessOutcome> ExecuteMeasureAsync(SqlHarnessMeasureOperation measure, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var knownSecrets = new List<string> { measure.QuerySql };
        if (!string.IsNullOrWhiteSpace(measure.SetupSql))
            knownSecrets.Add(measure.SetupSql);
        knownSecrets.AddRange(measure.Parameters.Where(value => !string.IsNullOrEmpty(value)));
        SqlParameterSecrets.AddValues(knownSecrets, measure.TypedParameters);

        try
        {
            if (measure.ParameterSets is { Count: > 0 })
            {
                foreach (var set in measure.ParameterSets)
                {
                    if (set?.Parameters is null)
                        continue;
                    knownSecrets.AddRange(set.Parameters.Where(value => !string.IsNullOrEmpty(value)));
                }
            }

            ValidateMeasure(measure);
            var target = TargetResolver.Resolve(measure.Target, _loadProfiles());
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
            SqlParameterSecrets.AddValues(knownSecrets, measure.Parameters);
            if (measure.ParameterSets is { Count: > 0 } rawSets)
            {
                foreach (var set in rawSets)
                    SqlParameterSecrets.AddValues(knownSecrets, set?.Parameters);
            }

            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(measure.Parameters, measure.TypedParameters));
            dialect.ValidateParameterReferences(parameters, measure.SetupSql, measure.QuerySql);
            SetupSqlExecution.Validate(dialect.Engine, measure.SetupSql, parameters);
            dialect.ValidateMeasuredBatch(measure.QuerySql);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            if (measure.ParameterSets is { Count: > 0 } parameterSets)
            {
                var boundSets = BindMeasureParameterSets(dialect, measure, parameterSets, knownSecrets);
                _artifactWriter.CheckStorage();
                phase = OperationPhase.Authentication;
                await using var multiSession = await _sessionFactory.ConnectAsync(target, ct);
                phase = OperationPhase.Sql;

                raw = new CanonicalResultAccumulator();
                var execution = await new MeasureParameterSetRunner(dialect, ComparisonMaximumRows).ExecuteAsync(
                    multiSession,
                    measure,
                    boundSets,
                    raw,
                    ct);
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
                    ArtifactOwner.From(measure.Target, target));
                setReport = setReport with { ArtifactDirectory = setDirectory };
                var setSuccess = new SqlHarnessOutcome(SqlHarnessExitCode.Success, setReport, null)
                {
                    BenchmarkRuns = execution.Runs.Select(run => run.Artifact).ToArray(),
                };
                return WithReceipt(setSuccess, rawFootprint);
            }

            _artifactWriter.CheckStorage();
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;

            raw = new CanonicalResultAccumulator();
            if (!string.IsNullOrWhiteSpace(measure.SetupSql))
            {
                var setupCommands = dialect.Engine == SqlEngine.SqlServer
                    ? SetupSqlExecution.PrepareCommands(measure.SetupSql, parameters, measure.TimeoutSeconds)
                    : [new SqlExecutionCommand(measure.SetupSql, parameters, measure.TimeoutSeconds)];
                foreach (var command in setupCommands)
                {
                    await BenchmarkRunner.ExecuteRawAsync(session, command, raw, ct);
                }
            }

            // Measure never runs ResultComparer; skip fingerprint retention and the 1M row comparison cap.
            await ExecuteBenchmarkRunAsync(dialect, session, measure.QuerySql, parameters, measure.TimeoutSeconds, 0, "measure", raw, captureComparison: false, ct);
            var runs = new List<CollectedBenchmarkRun>(measure.Repeat);
            for (var repetition = 1; repetition <= measure.Repeat; repetition++)
                runs.Add(await ExecuteBenchmarkRunAsync(dialect, session, measure.QuerySql, parameters, measure.TimeoutSeconds, repetition, "measure", raw, captureComparison: false, ct));

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
            var directory = _artifactWriter.Write(report, runs.Select(run => run.Artifact).ToArray(), target.Database, ArtifactOwner.From(measure.Target, target));
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
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, LongestFirst(knownSecrets)),
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
        IReadOnlyList<SqlHarnessParameterSetInput> parameterSets,
        List<string> knownSecrets)
    {
        var prepared = MeasureParameterSetValidator.Prepare(
            measure.Parameters,
            parameterSets,
            measure.SetupSql,
            measure.QuerySql,
            dialect,
            measure.TypedParameters);
        var bound = new PreparedMeasureParameterSet[prepared.Count];
        for (var index = 0; index < prepared.Count; index++)
        {
            foreach (var parameter in prepared[index].Parameters)
                AddTypedSecret(knownSecrets, parameter);

            var parsed = dialect.BindParameters(
                SqlParameterInputs.Resolve(measure.Parameters, measure.TypedParameters)
                    .Concat(parameterSets[index].Parameters.Select(SqlParameterParser.ToInput)));
            foreach (var parameter in parsed)
                AddTypedSecret(knownSecrets, parameter);
            bound[index] = prepared[index] with { Parameters = parsed };
        }

        return bound;
    }

    private Task<CollectedBenchmarkRun> ExecuteBenchmarkRunAsync(
        ISqlDialect dialect,
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
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
    }

    private static void ValidateBounds(SqlHarnessQueryOperation query)
    {
        if (query.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (query.MaxRows is < 0 or > 500)
            throw new SqlHarnessSafetyException("Maximum displayed rows must be between 0 and 500.");
    }

    private async Task<SqlHarnessOutcome> ExecuteSchemaAsync(SqlHarnessSchemaOperation schema, CancellationToken ct)
    {
        var phase = OperationPhase.Validation; var raw = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>();
        if (schema.Filter is not null) knownSecrets.Add(schema.Filter);
        if (schema.Object is not null) knownSecrets.Add(schema.Object);
        try
        {
            if (schema.TimeoutSeconds is < 1 or > 300) throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
            if (schema.MaxObjects is < 1 or > 500) throw new SqlHarnessSafetyException("Schema object limit must be between 1 and 500.");
            var selection = SchemaReader.ParseObjectSelection(schema.Object);
            var target = TargetResolver.Resolve(schema.Target, _loadProfiles());
            var dialect = SqlDialects.For(target.Engine);
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct); phase = OperationPhase.Sql;
            await using var reader = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(
                    dialect.SchemaSql,
                    SchemaReader.Parameters(schema.Filter, schema.MaxObjects, selection.Schema, selection.Name),
                    schema.TimeoutSeconds),
                ct);
            var result = target.Engine == SqlEngine.Postgres
                ? await PostgresSchema.ReadAsync(reader, ct)
                : await SchemaReader.ReadAsync(reader, ct);
            raw = result.Raw;
            if (selection.IsObjectMode && (result.Objects.Count != 1 || result.Omitted != 0))
                throw new SqlHarnessSafetyException(SchemaReader.MissingOrAmbiguousMessage);
            return WithReceipt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, new SqlHarnessSchemaReport(session.Identity, result.Objects, result.Omitted), null), raw);
        }
        catch (Exception exception)
        {
            return WithReceipt(new SqlHarnessOutcome(OperationFailureMapper.Map(exception, phase), null, SecretRedactor.Redact(exception, knownSecrets)), raw);
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteWatchAsync(SqlHarnessWatchOperation watch, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string> { watch.Sql };
        knownSecrets.AddRange(watch.Parameters.Where(value => !string.IsNullOrEmpty(value)));
        SqlParameterSecrets.AddValues(knownSecrets, watch.TypedParameters);
        if (watch.Until is not null)
            knownSecrets.Add(watch.Until);

        try
        {
            ValidateWatch(watch);
            var target = TargetResolver.Resolve(watch.Target, _loadProfiles());
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
            SqlParameterSecrets.AddValues(knownSecrets, watch.Parameters);
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(watch.Parameters, watch.TypedParameters));
            dialect.ValidateParameterReferences(parameters, watch.Sql);
            // Predicate syntax is validated before authentication so bad --until fails closed.
            if (watch.Until is not null)
                _ = WatchCondition.Parse(watch.Until);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

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
    /// but keeps the exit code and gain receipt, so gain counts the whole
    /// stream as emitted output.
    /// </summary>
    public async Task<SqlHarnessOutcome> ExecuteWatchNdjsonAsync(
        SqlHarnessWatchOperation watch,
        TextWriter writer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(writer);

        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string> { watch.Sql };
        knownSecrets.AddRange(watch.Parameters.Where(value => !string.IsNullOrEmpty(value)));
        SqlParameterSecrets.AddValues(knownSecrets, watch.TypedParameters);
        if (watch.Until is not null)
            knownSecrets.Add(watch.Until);

        try
        {
            ValidateWatch(watch);
            var target = TargetResolver.Resolve(watch.Target, _loadProfiles());
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
            SqlParameterSecrets.AddValues(knownSecrets, watch.Parameters);
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(watch.Parameters, watch.TypedParameters));
            dialect.ValidateParameterReferences(parameters, watch.Sql);
            // Predicate syntax is validated before authentication so bad --until fails closed.
            if (watch.Until is not null)
                _ = WatchCondition.Parse(watch.Until);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

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
            return Checked(watch, WithReceipt(outcome, rawFootprint));
        }
        catch (Exception exception)
        {
            var exitCode = OperationFailureMapper.Map(exception, phase);
            var message = SecretRedactor.Redact(exception, knownSecrets);
            EmitWatchNdjsonFailed(writer, exitCode, SqlHarnessError.From(exitCode, message, "validation"), knownSecrets);
            var failure = new SqlHarnessOutcome(exitCode, null, message);
            return Checked(watch, WithReceipt(failure, rawFootprint));
        }
    }

    private static void EmitWatchNdjsonFailed(
        TextWriter writer,
        SqlHarnessExitCode exitCode,
        SqlHarnessError error,
        IReadOnlyCollection<string> knownSecrets)
    {
        // Best effort: a dead transport cannot take a terminal record, but the
        // returned outcome still reports the failure (never a success).
        try
        {
            var secrets = knownSecrets as IReadOnlyList<string> ?? knownSecrets.ToArray();
            new WatchNdjsonWriter(writer).WriteFailed(
                new
                {
                    exitCode = (int)exitCode,
                    error = error with { Message = SecretRedactor.Redact(error.Message, secrets) },
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
        var knownSecrets = new List<string> { snapshot.Sql };
        knownSecrets.AddRange(snapshot.Parameters.Where(value => !string.IsNullOrEmpty(value)));
        SqlParameterSecrets.AddValues(knownSecrets, snapshot.TypedParameters);

        try
        {
            ValidateSnapshot(snapshot);
            var target = TargetResolver.Resolve(snapshot.Target, _loadProfiles());
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
            SqlParameterSecrets.AddValues(knownSecrets, snapshot.Parameters);
            var parameters = dialect.BindParameters(SqlParameterInputs.Resolve(snapshot.Parameters, snapshot.TypedParameters));
            dialect.ValidateParameterReferences(parameters, snapshot.Sql);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

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

    private async Task<SqlHarnessOutcome> ExecutePingAsync(SqlHarnessPingOperation ping, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var phase = OperationPhase.Validation;
        var raw = new OutputFootprint(0, 0);
        var knownSecrets = CollectTargetSecrets(ping.Target);
        try
        {
            if (ping.TimeoutSeconds is < 1 or > 300)
                throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");

            var target = TargetResolver.Resolve(ping.Target, _loadProfiles());
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;
            var sql = SqlDialects.For(target.Engine).PingSql;
            await using var reader = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(sql, [], ping.TimeoutSeconds),
                ct);
            var probe = await PingQuery.ReadAsync(reader, ct);
            var report = new SqlHarnessPingReport(
                session.Identity,
                probe.Server,
                probe.Database,
                probe.Login,
                stopwatch.ElapsedMilliseconds);
            return WithReceipt(
                new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
                raw);
        }
        catch (Exception exception)
        {
            return WithReceipt(
                new SqlHarnessOutcome(
                    OperationFailureMapper.Map(exception, phase),
                    null,
                    SecretRedactor.Redact(exception, knownSecrets)),
                raw);
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteCountsAsync(SqlHarnessCountsOperation counts, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var raw = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>(CollectTargetSecrets(counts.Target));
        if (counts.Like is not null)
            knownSecrets.Add(counts.Like);

        try
        {
            if (counts.TimeoutSeconds is < 1 or > 300)
                throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
            if (counts.Top is < 1 or > 500)
                throw new SqlHarnessSafetyException("Counts top limit must be between 1 and 500.");
            ArgumentNullException.ThrowIfNull(counts.Tables);

            var target = TargetResolver.Resolve(counts.Target, _loadProfiles());
            var dialect = SqlDialects.For(target.Engine);
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;

            ResolvedCountSelection selection;
            await using (var catalogReader = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(
                    dialect.CountsCatalogSql,
                    CountsQuery.CatalogParameters(counts.Tables, counts.Like, counts.Top),
                    counts.TimeoutSeconds),
                ct))
            {
                selection = await CountsQuery.ReadCatalogAsync(catalogReader, counts.Tables, ct);
            }

            IReadOnlyList<SqlHarnessCountReport> tables;
            if (counts.Exact)
            {
                var exactRows = await CountsQuery.ExecuteExactAsync(
                    session,
                    selection.Objects,
                    counts.TimeoutSeconds,
                    ct,
                    dialect.BuildCountsExactSql);
                tables = selection.Objects
                    .Select((item, index) => new SqlHarnessCountReport(
                        item.Schema,
                        item.Name,
                        exactRows[index],
                        "exact"))
                    .ToArray();
            }
            else
            {
                tables = selection.Objects
                    .Select(item => new SqlHarnessCountReport(
                        item.Schema,
                        item.Name,
                        item.ApproxRows,
                        "approx"))
                    .ToArray();
            }

            var report = new SqlHarnessCountsReport(session.Identity, tables, selection.Omitted);
            return WithReceipt(
                new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
                raw);
        }
        catch (Exception exception)
        {
            return WithReceipt(
                new SqlHarnessOutcome(
                    OperationFailureMapper.Map(exception, phase),
                    null,
                    SecretRedactor.Redact(exception, knownSecrets)),
                raw);
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteSpaceAsync(SqlHarnessSpaceOperation space, CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var raw = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>(CollectTargetSecrets(space.Target));
        if (space.Object is not null)
            knownSecrets.Add(space.Object);

        try
        {
            if (space.TimeoutSeconds is < 1 or > 300)
                throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
            if (space.Top is < 1 or > 500)
                throw new SqlHarnessSafetyException("Space top limit must be between 1 and 500.");

            // Parse object before target resolution: name, schema.name, or null (no object).
            var (objectSchema, objectName) = ParseSpaceObject(space.Object);
            var target = TargetResolver.Resolve(space.Target, _loadProfiles());
            var dialect = SqlDialects.For(target.Engine);
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;
            await using var reader = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(
                    dialect.SpaceSql,
                    SpaceQuery.Parameters(space.Top, objectSchema, objectName),
                    space.TimeoutSeconds),
                ct);
            var collected = target.Engine == SqlEngine.Postgres
                ? await PostgresSpace.ReadAsync(reader, objectRequested: objectName is not null, ct)
                : await SpaceQuery.ReadAsync(reader, objectRequested: objectName is not null, ct);
            raw = collected.Raw;
            var report = new SqlHarnessSpaceReport(
                session.Identity,
                collected.Files,
                collected.Allocation,
                collected.Tables,
                collected.Indexes);
            return WithReceipt(
                new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
                raw);
        }
        catch (Exception exception)
        {
            return WithReceipt(
                new SqlHarnessOutcome(
                    OperationFailureMapper.Map(exception, phase),
                    null,
                    SecretRedactor.Redact(exception, knownSecrets)),
                raw);
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteQueryStoreTopAsync(
        SqlHarnessQueryStoreTopOperation operation,
        CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>(CollectTargetSecrets(operation.Target))
        {
            QueryStoreTopQuery.Sql,
        };

        try
        {
            if (operation.Top is < 1 or > 500)
                throw new SqlHarnessSafetyException("Query Store top limit must be between 1 and 500.");
            if (operation.TimeoutSeconds is < 1 or > 300)
                throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
            if (operation.WindowMinutes is < 1 or > 44640)
                throw new SqlHarnessSafetyException("Query Store window must be between 1 and 44640 minutes.");

            var target = TargetResolver.Resolve(operation.Target, _loadProfiles());
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;
            // Query Store exists only on SQL Server. Do not send the batch to Postgres.
            if (target.Engine == SqlEngine.Postgres)
                throw new InvalidOperationException("Query Store is available only on SQL Server.");

            await using var reader = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(
                    QueryStoreTopQuery.Sql,
                    QueryStoreTopQuery.Parameters(operation.WindowMinutes, operation.Top),
                    operation.TimeoutSeconds),
                ct);
            var collected = await QueryStoreTopQuery.ReadAsync(reader, ct);
            rawFootprint = collected.RawFootprint;
            foreach (var text in collected.SensitiveTexts)
            {
                if (!string.IsNullOrEmpty(text.QuerySqlText))
                    knownSecrets.Add(text.QuerySqlText);
            }

            var report = new SqlHarnessQueryStoreTopReport(
                session.Identity,
                operation.WindowMinutes,
                operation.Top,
                collected.Queries,
                ArtifactDirectory: null);
            phase = OperationPhase.Artifact;
            var directory = _queryStoreArtifacts.Write(report, collected.SensitiveTexts, target.Database);
            report = report with { ArtifactDirectory = directory };
            return WithReceipt(
                new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
                rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = phase == OperationPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : OperationFailureMapper.Map(exception, phase);
            return WithReceipt(
                new SqlHarnessOutcome(
                    exitCode,
                    null,
                    SecretRedactor.Redact(exception, LongestFirst(knownSecrets))),
                rawFootprint);
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteIndexesAsync(
        SqlHarnessIndexesOperation operation,
        CancellationToken ct)
    {
        var phase = OperationPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        var knownSecrets = new List<string>(CollectTargetSecrets(operation.Target))
        {
            IndexAnalysisQuery.Sql,
        };

        try
        {
            if (operation.Top is < 1 or > 500)
                throw new SqlHarnessSafetyException("Index analysis top limit must be between 1 and 500.");
            if (operation.TimeoutSeconds is < 1 or > 300)
                throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
            if (!IndexObjectSyntax.TryParse(operation.Object, out var objectSchema, out var objectName, out var objectError))
                throw new SqlHarnessSafetyException(objectError);

            var target = TargetResolver.Resolve(operation.Target, _loadProfiles());
            phase = OperationPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = OperationPhase.Sql;
            // Missing-index DMVs exist only on SQL Server. Do not send the batch to Postgres.
            if (target.Engine == SqlEngine.Postgres)
                throw new InvalidOperationException("Index overlap analysis is available only on SQL Server.");

            await using var reader = await session.ExecuteReaderAsync(
                new SqlExecutionCommand(
                    IndexAnalysisQuery.Sql,
                    IndexAnalysisQuery.Parameters(operation.Top, objectSchema, objectName),
                    operation.TimeoutSeconds),
                ct);
            var collected = await IndexAnalysisQuery.ReadAsync(reader, operation.Object is not null, ct);
            rawFootprint = collected.RawFootprint;
            foreach (var sensitive in collected.SensitiveIndexes)
            {
                if (sensitive.FilterDefinition is not null)
                    knownSecrets.Add(sensitive.FilterDefinition);
            }

            var candidateReports = new List<IndexCandidateReport>(collected.Candidates.Count);
            foreach (var candidate in collected.Candidates)
            {
                var match = IndexOverlapClassifier.FindBest(candidate, collected.ExistingIndexes);
                var best = match.BestIndex;
                string? bestName = null;
                if (best is { Name.Length: > 0 })
                    bestName = best.Name;

                candidateReports.Add(new IndexCandidateReport(
                    candidate.CandidateId,
                    candidate.Schema,
                    candidate.Table,
                    candidate.EqualityColumns,
                    candidate.InequalityColumns,
                    candidate.IncludeColumns,
                    candidate.UserSeeks,
                    candidate.UserScans,
                    candidate.AverageTotalUserCost,
                    candidate.AverageUserImpactPercent,
                    candidate.CumulativeImpactScore,
                    candidate.LastUserSeek,
                    candidate.LastUserScan,
                    match.Classification,
                    bestName,
                    match.MatchedKeyColumnCount,
                    match.CandidateKeyColumnCount,
                    match.MissingIncludeColumns,
                    best?.Disabled,
                    best?.HasFilter,
                    best?.FilterHash));
            }

            string? objectFilter = null;
            if (operation.Object is not null)
            {
                if (collected.Candidates.Count == 0)
                    objectFilter = operation.Object;
                else
                {
                    var first = collected.Candidates[0];
                    objectFilter = first.Schema + "." + first.Table;
                }
            }

            var report = new SqlHarnessIndexesReport(
                session.Identity,
                collected.ObservationSince,
                collected.ObservedAt,
                operation.Top,
                objectFilter,
                [IndexEvidenceWarning],
                candidateReports,
                ArtifactDirectory: null);
            phase = OperationPhase.Artifact;
            var directory = _indexAnalysisArtifacts.Write(
                report,
                collected.Candidates,
                collected.ExistingIndexes,
                collected.SensitiveIndexes,
                target.Database);
            report = report with { ArtifactDirectory = directory };
            return WithReceipt(
                new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null),
                rawFootprint);
        }
        catch (Exception exception)
        {
            var exitCode = phase == OperationPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : OperationFailureMapper.Map(exception, phase);
            return WithReceipt(
                new SqlHarnessOutcome(
                    exitCode,
                    null,
                    SecretRedactor.Redact(exception, LongestFirst(knownSecrets))),
                rawFootprint);
        }
    }

    private static IReadOnlyList<string> CollectTargetSecrets(SqlTargetRequest target)
    {
        var secrets = new List<string>();
        foreach (var value in target.Vars.Values)
        {
            if (!string.IsNullOrEmpty(value))
                secrets.Add(value);
        }

        if (!string.IsNullOrEmpty(target.SqlUser))
            secrets.Add(target.SqlUser);
        if (!string.IsNullOrEmpty(target.PasswordEnvVar))
            secrets.Add(target.PasswordEnvVar);
        return secrets;
    }

    /// <summary>
    /// Space --object: null = database-wide; one name; or schema.name. Same part rules as schema.
    /// </summary>
    private static (string? Schema, string? Name) ParseSpaceObject(string? objectSpec)
    {
        if (objectSpec is null)
            return (null, null);

        const string invalid = "Space --object must be a single object name or schema.name.";
        if (string.IsNullOrWhiteSpace(objectSpec))
            throw new SqlHarnessSafetyException(invalid);

        var firstDot = objectSpec.IndexOf('.');
        if (firstDot < 0)
            return (null, objectSpec);

        var lastDot = objectSpec.LastIndexOf('.');
        if (firstDot != lastDot)
            throw new SqlHarnessSafetyException(invalid);

        var schema = objectSpec[..firstDot];
        var name = objectSpec[(firstDot + 1)..];
        if (schema.Length == 0 || name.Length == 0)
            throw new SqlHarnessSafetyException(invalid);

        return (schema, name);
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