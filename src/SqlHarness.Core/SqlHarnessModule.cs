using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlHarness.Core.Auth;
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
    OutputFootprint RawFootprint);

public sealed class SqlHarnessModule : ISqlHarnessModule
{
    private static readonly TimeSpan StatisticsCleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly ISqlSessionFactory _sessionFactory;
    private readonly IGainStore _gainStore;
    private readonly ICompareArtifactWriter _artifactWriter;
    private readonly Func<IReadOnlyDictionary<string, TargetProfile>> _loadProfiles;

    public SqlHarnessModule()
        : this(new SqlClientSessionFactory(new AzureCli()), new GainStore(), new CompareArtifactWriter(), () => ProfileStore.Load())
    {
    }

    internal SqlHarnessModule(
        ISqlSessionFactory sessionFactory,
        IGainStore gainStore,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles)
        : this(sessionFactory, gainStore, new CompareArtifactWriter(), loadProfiles)
    {
    }

    internal SqlHarnessModule(
        ISqlSessionFactory sessionFactory,
        IGainStore gainStore,
        ICompareArtifactWriter artifactWriter,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _gainStore = gainStore ?? throw new ArgumentNullException(nameof(gainStore));
        _artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
        _loadProfiles = loadProfiles ?? throw new ArgumentNullException(nameof(loadProfiles));
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
                return new SqlHarnessOutcome(SqlHarnessExitCode.Success, _gainStore.Aggregate(), null);
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
            return ExecutePlan(plan);

        if (operation is SqlHarnessCompareOperation compare)
            return await ExecuteCompareAsync(compare, ct);

        if (operation is SqlHarnessMeasureOperation measure)
            return await ExecuteMeasureAsync(measure, ct);

        if (operation is not SqlHarnessQueryOperation query)
        {
            return new SqlHarnessOutcome(
                SqlHarnessExitCode.Safety,
                null,
                SecretRedactor.Redact("The SQLHarness operation is not implemented.", []));
        }

        var stopwatch = Stopwatch.StartNew();
        var phase = ExecutionPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var knownSecrets = new List<string> { query.Sql };
        knownSecrets.AddRange(query.Parameters.Where(value => !string.IsNullOrEmpty(value)));

        try
        {
            ValidateBounds(query);
            var target = TargetResolver.Resolve(query.Target, _loadProfiles());
            var safety = new SqlSafetyClassifier().Classify(
                query.Sql,
                SqlUsage.Query,
                target.Database,
                query.AllowMutation,
                query.ConfirmDatabase);
            if (!safety.Allowed)
                throw new SqlHarnessSafetyException($"SQL safety rejection: {safety.Reason}.");
            var parameters = SqlParameterParser.Parse(query.Parameters);
            SqlParameterReferenceValidator.Validate(parameters, query.Sql);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            phase = ExecutionPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = ExecutionPhase.Sql;

            var execution = new SqlExecutionCommand(query.Sql, parameters, query.TimeoutSeconds);
            var messageStart = session.Messages.Count;
            await using var reader = await session.ExecuteReaderAsync(execution, ct);
            raw = new CanonicalResultAccumulator();
            var collected = await CollectAsync(
                reader,
                query.MaxRows,
                () => session.Messages,
                messageStart,
                knownSecrets,
                raw,
                ct);
            rawFootprint = raw.Complete().Footprint;
            var targetReport = session.Identity;
            var report = new SqlHarnessQueryReport(
                targetReport,
                safety.HasMutation ? "mutation" : "read-only",
                collected.ResultSets,
                collected.Messages,
                reader.RecordsAffected,
                stopwatch.ElapsedMilliseconds,
                collected.Canonical.Hash,
                rawFootprint);
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
            return WithReceipt(success, stopwatch.ElapsedMilliseconds, rawFootprint);
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var exitCode = MapException(exception, phase);
            var failure = new SqlHarnessOutcome(
                exitCode,
                null,
                SecretRedactor.Redact(exception, knownSecrets));
            return WithReceipt(failure, stopwatch.ElapsedMilliseconds, rawFootprint);
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private SqlHarnessOutcome WithReceipt(
        SqlHarnessOutcome outcome,
        long durationMilliseconds,
        OutputFootprint raw,
        string command = "query")
    {
        var receipt = new SqlHarnessEmissionReceipt((emitted, _) =>
        {
            try
            {
                var rawTokens = raw.EstimatedTokenCount;
                var emittedTokens = emitted.EstimatedTokenCount;
                _gainStore.Append(new GainRecord(
                    DateTimeOffset.UtcNow,
                    command,
                    outcome.ExitCode == SqlHarnessExitCode.Success,
                    Math.Max(durationMilliseconds, 0),
                    raw.Bytes,
                    raw.Lines,
                    emitted.Bytes,
                    emitted.Lines,
                    rawTokens,
                    emittedTokens,
                    Math.Max(rawTokens - emittedTokens, 0)));
                return Task.FromResult(outcome.ExitCode);
            }
            catch (Exception)
            {
                return Task.FromResult(SqlHarnessExitCode.LocalStorage);
            }
        });
        return outcome with { EmissionReceipt = receipt };
    }

    private async Task<SqlHarnessOutcome> ExecuteCompareAsync(SqlHarnessCompareOperation compare, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var phase = ExecutionPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var knownSecrets = new List<string> { compare.BaselineSql, compare.CandidateSql };
        if (!string.IsNullOrWhiteSpace(compare.SetupSql))
            knownSecrets.Add(compare.SetupSql);
        knownSecrets.AddRange(compare.Parameters.Where(value => !string.IsNullOrEmpty(value)));

        try
        {
            ValidateCompare(compare);
            var target = TargetResolver.Resolve(compare.Target, _loadProfiles());
            var classifier = new SqlSafetyClassifier();
            EnsureSafe(classifier.Classify(compare.BaselineSql, SqlUsage.Query, target.Database, false), "baseline");
            EnsureSafe(classifier.Classify(compare.CandidateSql, SqlUsage.Query, target.Database, false), "candidate");
            if (!string.IsNullOrWhiteSpace(compare.SetupSql))
                EnsureSafe(classifier.Classify(compare.SetupSql, SqlUsage.CompareSetup, target.Database, false), "setup");
            var parameters = SqlParameterParser.Parse(compare.Parameters);
            SqlParameterReferenceValidator.Validate(parameters, compare.SetupSql, compare.BaselineSql, compare.CandidateSql);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            phase = ExecutionPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = ExecutionPhase.Sql;

            raw = new CanonicalResultAccumulator();
            if (!string.IsNullOrWhiteSpace(compare.SetupSql))
                await ExecuteRawAsync(session, new SqlExecutionCommand(compare.SetupSql, parameters, compare.TimeoutSeconds), raw, ct);

            await ExecuteBenchmarkRunAsync(session, compare.BaselineSql, parameters, compare.TimeoutSeconds, 0, "baseline", raw, ct);
            await ExecuteBenchmarkRunAsync(session, compare.CandidateSql, parameters, compare.TimeoutSeconds, 0, "candidate", raw, ct);

            var runs = new List<CollectedCompareRun>(compare.Repeat * 2);
            for (var repetition = 1; repetition <= compare.Repeat; repetition++)
            {
                if (repetition % 2 == 1)
                {
                    runs.Add(await ExecuteBenchmarkRunAsync(session, compare.BaselineSql, parameters, compare.TimeoutSeconds, repetition, "baseline", raw, ct));
                    runs.Add(await ExecuteBenchmarkRunAsync(session, compare.CandidateSql, parameters, compare.TimeoutSeconds, repetition, "candidate", raw, ct));
                }
                else
                {
                    runs.Add(await ExecuteBenchmarkRunAsync(session, compare.CandidateSql, parameters, compare.TimeoutSeconds, repetition, "candidate", raw, ct));
                    runs.Add(await ExecuteBenchmarkRunAsync(session, compare.BaselineSql, parameters, compare.TimeoutSeconds, repetition, "baseline", raw, ct));
                }
            }

            rawFootprint = raw.Complete().Footprint;
            var baselineRuns = runs.Where(run => run.Variant == "baseline").ToArray();
            var candidateRuns = runs.Where(run => run.Variant == "candidate").ToArray();
            var targetReport = session.Identity;
            var allHashes = runs.Select(run => run.ResultHash).Distinct(StringComparer.Ordinal).ToArray();
            var report = new SqlHarnessCompareReport(
                targetReport,
                compare.Repeat,
                runs.Count,
                allHashes.Length == 1,
                CreateVariantReport("baseline", baselineRuns),
                CreateVariantReport("candidate", candidateRuns),
                null);

            phase = ExecutionPhase.Artifact;
            var publicRuns = runs.Select(run => run.Artifact).ToArray();
            var directory = _artifactWriter.Write(report, publicRuns, target.Database);
            report = report with { ArtifactDirectory = directory };
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
            return WithReceipt(success, stopwatch.ElapsedMilliseconds, rawFootprint, "compare");
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var exitCode = phase == ExecutionPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : MapException(exception, phase);
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, knownSecrets));
            return WithReceipt(failure, stopwatch.ElapsedMilliseconds, rawFootprint, "compare");
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private async Task<SqlHarnessOutcome> ExecuteMeasureAsync(SqlHarnessMeasureOperation measure, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var phase = ExecutionPhase.Validation;
        var rawFootprint = new OutputFootprint(0, 0);
        CanonicalResultAccumulator? raw = null;
        var knownSecrets = new List<string> { measure.QuerySql };
        if (!string.IsNullOrWhiteSpace(measure.SetupSql))
            knownSecrets.Add(measure.SetupSql);
        knownSecrets.AddRange(measure.Parameters.Where(value => !string.IsNullOrEmpty(value)));

        try
        {
            ValidateMeasure(measure);
            var target = TargetResolver.Resolve(measure.Target, _loadProfiles());
            var classifier = new SqlSafetyClassifier();
            EnsureSafe(classifier.Classify(measure.QuerySql, SqlUsage.Query, target.Database, false), "query");
            if (!string.IsNullOrWhiteSpace(measure.SetupSql))
                EnsureSafe(classifier.Classify(measure.SetupSql, SqlUsage.CompareSetup, target.Database, false), "setup");
            var parameters = SqlParameterParser.Parse(measure.Parameters);
            SqlParameterReferenceValidator.Validate(parameters, measure.SetupSql, measure.QuerySql);

            foreach (var parameter in parameters)
            {
                if (parameter.Value is not DBNull)
                    knownSecrets.Add(Convert.ToString(parameter.Value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            phase = ExecutionPhase.Authentication;
            await using var session = await _sessionFactory.ConnectAsync(target, ct);
            phase = ExecutionPhase.Sql;

            raw = new CanonicalResultAccumulator();
            if (!string.IsNullOrWhiteSpace(measure.SetupSql))
                await ExecuteRawAsync(session, new SqlExecutionCommand(measure.SetupSql, parameters, measure.TimeoutSeconds), raw, ct);

            await ExecuteBenchmarkRunAsync(session, measure.QuerySql, parameters, measure.TimeoutSeconds, 0, "measure", raw, ct);
            var runs = new List<CollectedCompareRun>(measure.Repeat);
            for (var repetition = 1; repetition <= measure.Repeat; repetition++)
                runs.Add(await ExecuteBenchmarkRunAsync(session, measure.QuerySql, parameters, measure.TimeoutSeconds, repetition, "measure", raw, ct));

            rawFootprint = raw.Complete().Footprint;
            var targetReport = session.Identity;
            var report = new SqlHarnessMeasureReport(
                targetReport,
                measure.Repeat,
                runs.Count,
                runs.Select(run => run.ResultHash).Distinct(StringComparer.Ordinal).Count() == 1,
                CreateVariantReport("measure", runs),
                null);

            phase = ExecutionPhase.Artifact;
            var directory = _artifactWriter.Write(report, runs.Select(run => run.Artifact).ToArray(), target.Database);
            report = report with { ArtifactDirectory = directory };
            var success = new SqlHarnessOutcome(SqlHarnessExitCode.Success, report, null);
            return WithReceipt(success, stopwatch.ElapsedMilliseconds, rawFootprint, "measure");
        }
        catch (Exception exception)
        {
            if (raw is not null)
                rawFootprint = raw.SnapshotFootprint();
            var exitCode = phase == ExecutionPhase.Artifact
                ? SqlHarnessExitCode.LocalStorage
                : MapException(exception, phase);
            var failure = new SqlHarnessOutcome(exitCode, null, SecretRedactor.Redact(exception, knownSecrets));
            return WithReceipt(failure, stopwatch.ElapsedMilliseconds, rawFootprint, "measure");
        }
        finally
        {
            raw?.Dispose();
        }
    }

    private static async Task<CollectedCompareRun> ExecuteBenchmarkRunAsync(
        ISqlSession session,
        string sql,
        IReadOnlyList<SqlHarnessParameter> parameters,
        int timeoutSeconds,
        int repetition,
        string variant,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        const string enable = "SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;";
        const string disable = "SET STATISTICS XML OFF; SET STATISTICS TIME OFF; SET STATISTICS IO OFF;";
        Exception? primaryException = null;
        var messageStart = -1;
        var messagesCaptured = false;
        try
        {
            await ExecuteAndDrainAsync(session, new SqlExecutionCommand(enable, [], timeoutSeconds), ct);
            messageStart = session.Messages.Count;
            await using var reader = await session.ExecuteReaderAsync(new SqlExecutionCommand(sql, parameters, timeoutSeconds), ct);
            var result = await CollectCompareAsync(reader, raw, ct);
            var messages = session.Messages.Skip(messageStart).ToArray();
            foreach (var message in messages)
                raw.AddMessage("sql", message);
            messagesCaptured = true;
            var io = StatisticsIoParser.Parse(string.Join(Environment.NewLine, messages));
            var time = StatisticsTimeParser.Parse(string.Join(Environment.NewLine, messages));
            var plans = result.PlanXmls.Select(ExecutionPlanParser.Parse).ToArray();
            var artifact = new CompareRunArtifact(
                variant,
                repetition,
                time.CpuTimeMs,
                time.ElapsedTimeMs,
                io.LogicalReads,
                io.Tables,
                result.Canonical.Hash,
                result.PlanXmls,
                messages.Length);
            return new CollectedCompareRun(artifact, plans);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            if (messageStart >= 0 && !messagesCaptured)
            {
                try
                {
                    AppendMessages(session, messageStart, raw);
                }
                catch
                {
                    // Preserve the primary benchmark failure if message snapshotting also fails.
                }
            }
            throw;
        }
        finally
        {
            using var cleanupCts = new CancellationTokenSource(StatisticsCleanupTimeout);
            try
            {
                await ExecuteAndDrainAsync(
                        session,
                        new SqlExecutionCommand(disable, [], timeoutSeconds),
                        cleanupCts.Token)
                    .WaitAsync(StatisticsCleanupTimeout, CancellationToken.None);
            }
            catch when (primaryException is not null)
            {
                // Preserve the benchmark failure while still bounding the best-effort cleanup.
            }
        }
    }

    private static async Task<CollectedCompare> CollectCompareAsync(
        ISqlReader reader,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        using var canonical = new CanonicalResultAccumulator();
        var planXmls = new List<string>();
        do
        {
            if (reader.FieldCount == 0)
                continue;
            if (reader.FieldCount == 1 && reader.GetName(0).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase))
            {
                while (await reader.ReadAsync(ct))
                {
                    var planXml = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                    planXmls.Add(planXml);
                    raw.AddMessage("planXml", planXml);
                }
                continue;
            }

            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(index => new CanonicalColumn(index, reader.GetName(index), reader.GetFieldType(index).FullName ?? reader.GetFieldType(index).Name, reader.GetAllowNull(index)))
                .ToArray();
            canonical.BeginResultSet(columns);
            raw.BeginResultSet(columns);
            while (await reader.ReadAsync(ct))
            {
                var values = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => NormalizeValue(reader.GetValue(index)))
                    .ToArray();
                canonical.AddRow(values);
                raw.AddRow(values);
            }
            canonical.EndResultSet();
            raw.EndResultSet();
        } while (await reader.NextResultAsync(ct));
        return new CollectedCompare(canonical.Complete(), planXmls);
    }

    private static async Task ExecuteRawAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        var messageStart = session.Messages.Count;
        Exception? primaryException = null;
        try
        {
            await using var reader = await session.ExecuteReaderAsync(command, ct);
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => new CanonicalColumn(
                        index,
                        reader.GetName(index),
                        reader.GetFieldType(index).FullName ?? reader.GetFieldType(index).Name,
                        reader.GetAllowNull(index)))
                    .ToArray();
                raw.BeginResultSet(columns);
                while (await reader.ReadAsync(ct))
                {
                    raw.AddRow(Enumerable.Range(0, reader.FieldCount)
                        .Select(index => NormalizeValue(reader.GetValue(index)))
                        .ToArray());
                }
                raw.EndResultSet();
            } while (await reader.NextResultAsync(ct));
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            try
            {
                AppendMessages(session, messageStart, raw);
            }
            catch when (primaryException is not null)
            {
                // Preserve the primary setup failure if message snapshotting also fails.
            }
        }
    }

    private static void AppendMessages(ISqlSession session, int messageStart, CanonicalResultAccumulator raw)
    {
        foreach (var message in session.Messages.Skip(messageStart))
            raw.AddMessage("sql", message);
    }

    private static async Task ExecuteAndDrainAsync(
        ISqlSession session,
        SqlExecutionCommand command,
        CancellationToken ct)
    {
        await using var reader = await session.ExecuteReaderAsync(command, ct);
        do
        {
            while (await reader.ReadAsync(ct))
            {
            }
        } while (await reader.NextResultAsync(ct));
    }

    private static CompareVariantReport CreateVariantReport(string name, IReadOnlyList<CollectedCompareRun> runs)
    {
        var operators = runs
            .SelectMany(run => run.Plans)
            .SelectMany(plan => plan.Operators)
            .Select(op => new CompareOperatorReport(op.NodeId, op.PhysicalOp, op.Object, op.HasWarnings, op.HasSpill, op.HasImplicitConversion))
            .Distinct()
            .ToArray();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        if (operators.Any(op => op.HasWarnings)) warnings.Add("PlanWarning");
        if (operators.Any(op => op.HasSpill)) warnings.Add("SpillToTempDb");
        if (operators.Any(op => op.HasImplicitConversion)) warnings.Add("ImplicitConversion");
        var tables = runs.SelectMany(run => run.Artifact.LogicalReadsByTable)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value), StringComparer.Ordinal);
        return new CompareVariantReport(
            name,
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.CpuTimeMilliseconds))),
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.ElapsedTimeMilliseconds))),
            ToPublic(Distribution.From(runs.Select(run => run.Artifact.LogicalReads))),
            tables,
            operators,
            warnings.Order(StringComparer.Ordinal).ToArray());
    }

    private static CompareDistribution ToPublic(Distribution value) => new(value.Min, value.Median, value.Max);

    private static void ValidateCompare(SqlHarnessCompareOperation compare)
    {
        if (compare.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (compare.Repeat is < 1 or > 100)
            throw new SqlHarnessSafetyException("Compare repetitions must be between 1 and 100.");
    }

    private static void ValidateMeasure(SqlHarnessMeasureOperation measure)
    {
        if (measure.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (measure.Repeat is < 1 or > 100)
            throw new SqlHarnessSafetyException("Measurement repetitions must be between 1 and 100.");
    }

    private static void EnsureSafe(SqlSafetyDecision decision, string label)
    {
        if (!decision.Allowed)
            throw new SqlHarnessSafetyException($"SQL safety rejection for {label}: {decision.Reason}.");
    }

    private static async Task<CollectedQuery> CollectAsync(
        ISqlReader reader,
        int maxRows,
        Func<IReadOnlyList<string>> messageSnapshot,
        int messageStart,
        IReadOnlyList<string> secrets,
        CanonicalResultAccumulator raw,
        CancellationToken ct)
    {
        var retained = 0;
        var reports = new List<SqlHarnessResultSetReport>();
        using var canonical = new CanonicalResultAccumulator();
        var rawResultSetOpen = false;
        try
        {
            do
            {
                if (reader.FieldCount == 0)
                    continue;

                var canonicalColumns = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => new CanonicalColumn(
                        index,
                        reader.GetName(index),
                        reader.GetFieldType(index).FullName ?? reader.GetFieldType(index).Name,
                        reader.GetAllowNull(index)))
                    .ToArray();
                var publicColumns = canonicalColumns
                    .Select(column => new SqlHarnessColumnReport(
                        column.Ordinal,
                        column.Name,
                        column.DataType,
                        column.AllowNull))
                    .ToArray();
                var rows = new List<IReadOnlyList<object?>>();
                long rowCount = 0;
                canonical.BeginResultSet(canonicalColumns);
                raw.BeginResultSet(canonicalColumns);
                rawResultSetOpen = true;
                while (await reader.ReadAsync(ct))
                {
                    var values = Enumerable.Range(0, reader.FieldCount)
                        .Select(index => NormalizeValue(reader.GetValue(index)))
                        .ToArray();
                    canonical.AddRow(values);
                    raw.AddRow(values);
                    rowCount++;
                    if (retained < maxRows)
                    {
                        rows.Add(values);
                        retained++;
                    }
                }
                canonical.EndResultSet();
                raw.EndResultSet();
                rawResultSetOpen = false;
                reports.Add(new SqlHarnessResultSetReport(
                    publicColumns,
                    rows,
                    rowCount,
                    rowCount - rows.Count));
            } while (await reader.NextResultAsync(ct));
        }
        catch
        {
            if (rawResultSetOpen)
                raw.EndResultSet();
            AppendSafeMessages(raw, messageSnapshot(), messageStart, secrets);
            throw;
        }

        var safeMessages = SafeMessageSlice(messageSnapshot(), messageStart, secrets);
        foreach (var message in safeMessages)
        {
            canonical.AddMessage("sql", message);
            raw.AddMessage("sql", message);
        }
        return new CollectedQuery(reports, safeMessages, canonical.Complete());
    }

    private static void AppendSafeMessages(
        CanonicalResultAccumulator raw,
        IReadOnlyList<string> messages,
        int messageStart,
        IReadOnlyList<string> secrets)
    {
        foreach (var message in SafeMessageSlice(messages, messageStart, secrets))
            raw.AddMessage("sql", message);
    }

    private static IReadOnlyList<string> SafeMessageSlice(
        IReadOnlyList<string> messages,
        int messageStart,
        IReadOnlyList<string> secrets) =>
        messages.Skip(messageStart)
            .Select(message => SecretRedactor.Redact(message, secrets))
            .ToArray();

    private static object? NormalizeValue(object value) => value is DBNull ? null : value;

    private static void ValidateBounds(SqlHarnessQueryOperation query)
    {
        if (query.TimeoutSeconds is < 1 or > 300)
            throw new SqlHarnessSafetyException("SQL timeout must be between 1 and 300 seconds.");
        if (query.MaxRows is < 0 or > 500)
            throw new SqlHarnessSafetyException("Maximum displayed rows must be between 0 and 500.");
    }

    private static SqlHarnessExitCode MapException(Exception exception, ExecutionPhase phase) => exception switch
    {
        SqlTargetMismatchException => SqlHarnessExitCode.TargetMismatch,
        SqlHarnessSafetyException => SqlHarnessExitCode.Safety,
        IOException or UnauthorizedAccessException when phase == ExecutionPhase.Validation => SqlHarnessExitCode.LocalStorage,
        AzureCliException => SqlHarnessExitCode.Authentication,
        SqlException when phase == ExecutionPhase.Authentication => SqlHarnessExitCode.Authentication,
        SqlException => SqlHarnessExitCode.SqlExecution,
        TimeoutException => SqlHarnessExitCode.SqlExecution,
        OperationCanceledException when phase == ExecutionPhase.Sql => SqlHarnessExitCode.SqlExecution,
        _ when phase == ExecutionPhase.Authentication => SqlHarnessExitCode.Authentication,
        _ => SqlHarnessExitCode.SqlExecution,
    };

    private sealed record CollectedQuery(
        IReadOnlyList<SqlHarnessResultSetReport> ResultSets,
        IReadOnlyList<string> Messages,
        CanonicalResult Canonical);

    private sealed record CollectedCompare(
        CanonicalResult Canonical,
        IReadOnlyList<string> PlanXmls);

    private sealed record CollectedCompareRun(
        CompareRunArtifact Artifact,
        IReadOnlyList<ExecutionPlan> Plans)
    {
        public string Variant => Artifact.Variant;
        public string ResultHash => Artifact.ResultHash;
    }

    private enum ExecutionPhase
    {
        Validation,
        Authentication,
        Sql,
        Artifact,
    }

    private SqlHarnessOutcome ExecutePlan(SqlHarnessPlanOperation operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var bytes = Encoding.UTF8.GetByteCount(operation.ShowplanXml);
        var lines = operation.ShowplanXml.Length == 0
            ? 0
            : operation.ShowplanXml.Count(character => character == '\n') + (operation.ShowplanXml[^1] == '\n' ? 0 : 1);
        var raw = new OutputFootprint(bytes, lines);
        try
        {
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Success, PlanDistiller.Distill(operation.ShowplanXml), null);
            return WithReceipt(outcome, stopwatch.ElapsedMilliseconds, raw, "plan");
        }
        catch (Exception exception)
        {
            var outcome = new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, SecretRedactor.Redact(exception, [operation.ShowplanXml]));
            return WithReceipt(outcome, stopwatch.ElapsedMilliseconds, raw, "plan");
        }
    }
}
