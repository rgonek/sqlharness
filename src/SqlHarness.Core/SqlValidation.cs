using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

/// <summary>
/// Shared static-analysis boundary for every offline safety verdict. The
/// classifier only sees effects visible in the SQL text
/// (<c>static-visible-effects</c>); object existence and permissions never
/// resolve offline (unknown), and hidden effects beyond the AST proof
/// (functions, views, operators) are not verified. Additive and versioned:
/// clients detect meaning changes via <see cref="ContractVersion"/>.
/// Classification labels keep their existing meaning.
/// </summary>
public static class SqlSafetyAnalysis
{
    public const string AnalysisKind = "static-visible-effects";
    public const int ContractVersion = 1;
    public const bool HiddenEffectsVerified = false;
    public const string ObjectAndPermissionStatus = "unknown";
}

/// <summary>
/// Additive, versioned safety-analysis disclosure shared by the
/// capabilities and validate surfaces. Values mirror
/// <see cref="SqlSafetyAnalysis"/>.
/// </summary>
public sealed record SqlHarnessSafetyAnalysis(
    string AnalysisKind,
    int AnalysisContractVersion,
    bool HiddenEffectsVerified,
    string ObjectAndPermissionStatus);

public sealed record SqlValidationLocation(int StartOffset, int StartLine, int StartColumn, string Kind);

public sealed record SqlValidationParameter(string Name, string Type);

/// <summary>
/// Offline validation verdict. Reason carries only safe codes (never SQL or parameter values);
/// AstLocations marks statement starts (SQL Server only). CheckedConditions names the check
/// program scoped by the caller usage, in evaluation order; evaluation short-circuits on the
/// first failure named by Reason. Object-existence and permission checks never run offline, so
/// ObjectAndPermissionStatus stays "unknown" and Executed stays false.
/// Additive AnalysisKind/AnalysisContractVersion/HiddenEffectsVerified
/// disclose the static-analysis boundary without changing classification.
/// </summary>
public sealed record SqlValidationReport(
    string Engine,
    string Classification,
    bool Allowed,
    string? Reason,
    IReadOnlyList<string> RequiredParameters,
    IReadOnlyList<string> MissingParameters,
    IReadOnlyList<SqlValidationParameter> Parameters,
    IReadOnlyList<SqlValidationLocation> AstLocations,
    bool AstLocationsAvailable,
    string ObjectAndPermissionStatus = "unknown",
    bool Executed = false,
    IReadOnlyList<string>? CheckedConditions = null,
    string AnalysisKind = SqlSafetyAnalysis.AnalysisKind,
    int AnalysisContractVersion = SqlSafetyAnalysis.ContractVersion,
    bool HiddenEffectsVerified = SqlSafetyAnalysis.HiddenEffectsVerified);

/// <summary>
/// Caller intent for offline validation. Query is the default read path;
/// Setup classifies session-local preparation batches; Benchmark adds the
/// execution-path measured-batch shape check of the resolved dialect.
/// </summary>
public enum ValidationUsage
{
    Query,
    Setup,
    Benchmark,
}

/// <summary>
/// Mode-aware validation options. SetupSql is the optional setup-batch
/// context for Query/Benchmark (classified as setup and its session temps
/// shared with the main batch, like the compare/measure preflight); it is
/// ignored for Setup, which classifies the main batch itself as setup.
/// </summary>
public sealed record ValidationOptions(
    ValidationUsage Usage = ValidationUsage.Query,
    string? SetupSql = null);

public static class SqlValidation
{
    public static SqlValidationReport Validate(
        SqlTargetRequest targetRequest,
        string sql,
        IReadOnlyList<string> parameterDeclarations,
        IReadOnlyDictionary<string, TargetProfile> profiles) =>
        Validate(targetRequest, sql, parameterDeclarations, profiles, options: null);

    public static SqlValidationReport Validate(
        SqlTargetRequest targetRequest,
        string sql,
        IReadOnlyList<string> parameterDeclarations,
        IReadOnlyDictionary<string, TargetProfile> profiles,
        ValidationOptions? options)
    {
        ArgumentNullException.ThrowIfNull(targetRequest);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameterDeclarations);
        ArgumentNullException.ThrowIfNull(profiles);

        var usage = options?.Usage ?? ValidationUsage.Query;
        var setupSql = usage == ValidationUsage.Setup ? null : options?.SetupSql;
        var target = TargetResolver.Resolve(targetRequest, profiles);
        var dialect = SqlDialects.For(target.Engine);
        var noTemps = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlySet<string> setupTemps = noTemps;
        string? setupReason = null;
        if (!string.IsNullOrWhiteSpace(setupSql))
        {
            var setupDecision = dialect.Classify(setupSql, SqlUsage.CompareSetup, target.Database, allowMutation: false, confirmDatabase: null, noTemps);
            if (setupDecision.Allowed)
                setupTemps = setupDecision.SessionTempTables;
            else
                setupReason = SafeReason(setupDecision.Reason);
        }

        var mainUsage = usage == ValidationUsage.Setup ? SqlUsage.CompareSetup : SqlUsage.Query;
        var decision = dialect.Classify(sql, mainUsage, target.Database, allowMutation: false, confirmDatabase: null, usage == ValidationUsage.Setup ? noTemps : setupTemps);
        var parsed = decision.Reason != SqlSafetyReason.ParseError;
        var requiredNames = parsed
            ? SqlParameterReferences.Collect(target.Engine, sql)
                .Select(CanonicalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var astLocations = parsed ? SqlParameterReferences.Locations(target.Engine, sql) : [];
        IReadOnlyList<SqlHarnessParameter> parsedParameters = [];
        var parameterValidationCompleted = false;
        string? reason = setupReason ?? (decision.Allowed ? null : SafeReason(decision.Reason));
        if (reason is null)
        {
            try
            {
                parsedParameters = dialect.ParseParameters(parameterDeclarations);
                dialect.ValidateParameterReferences(parsedParameters, setupSql, sql);
                parameterValidationCompleted = true;
            }
            catch (SqlHarnessSafetyException)
            {
                reason = "parameter_validation_failed";
            }
        }

        if (reason is null && usage == ValidationUsage.Benchmark)
        {
            try
            {
                dialect.ValidateMeasuredBatch(sql);
            }
            catch (SqlHarnessSafetyException)
            {
                reason = "benchmark_batch_not_supported";
            }
        }
        // 007/T4: explicit scope of checked conditions for this usage, in
        // evaluation order. Evaluation short-circuits on the first failure
        // named by Reason; the list still names the whole check program so
        // callers can see what the usage covers. Catalog/permission checks
        // never run offline (ObjectAndPermissionStatus stays "unknown").
        var checkedConditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(setupSql))
            checkedConditions.Add("setup_context_classification");
        checkedConditions.Add(usage == ValidationUsage.Setup ? "setup_classification" : "query_safety_classification");
        checkedConditions.Add("parameter_validation");
        checkedConditions.Add("missing_parameter_check");
        if (usage == ValidationUsage.Benchmark)
            checkedConditions.Add("measured_batch_shape");
        var suppliedNames = parsedParameters.Select(parameter => CanonicalName(parameter.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingNames = parameterValidationCompleted
            ? requiredNames.Where(name => !suppliedNames.Contains(name)).ToArray()
            : [];
        if (reason is null && missingNames.Length > 0)
            reason = "missing_parameters";
        var allowed = decision.Allowed && reason is null;
        var supplied = parsedParameters.Select(p => new SqlValidationParameter(CanonicalName(p.Name), p.UdtTypeName?.ToLowerInvariant() ?? p.Type.ToString().ToLowerInvariant())).ToArray();
        var classification = !allowed
            ? "rejected"
            : decision.HasMutation ? "mutation" : decision.HasSessionLocalWork ? "session-local" : "read-only";
        return new SqlValidationReport(
            SqlEngineNames.Format(target.Engine),
            classification,
            allowed,
            reason,
            requiredNames,
            missingNames,
            supplied,
            astLocations,
            AstLocationsAvailable: target.Engine == SqlEngine.SqlServer,
            Executed: false,
            CheckedConditions: checkedConditions);
    }

    private static string CanonicalName(string name) => name.TrimStart('@', ':');

    private static string SafeReason(SqlSafetyReason reason) => reason switch
    {
        SqlSafetyReason.ParseError => "sql_parse_error",
        SqlSafetyReason.UnsupportedStatement => "unsupported_statement",
        SqlSafetyReason.MutationNotAllowed => "mutation_not_allowed",
        SqlSafetyReason.DatabaseConfirmationRequired => "database_confirmation_required",
        SqlSafetyReason.DatabaseConfirmationMismatch => "database_confirmation_mismatch",
        SqlSafetyReason.CrossDatabaseReference => "cross_database_reference",
        SqlSafetyReason.SelectIntoNotAllowed => "select_into_not_allowed",
        SqlSafetyReason.NonTemporaryWrite => "non_temporary_write",
        _ => "validation_failed",
    };
}

internal static class SqlParameterReferences
{
    internal static IReadOnlyList<string> Collect(SqlEngine engine, string sql)
    {
        if (engine == SqlEngine.Postgres)
            return Postgres.PostgresParameterReferenceValidator.CollectReferences(sql);

        var document = SqlServerDocument.Parse(sql);
        if (document.HasErrors)
            return [];
        var collector = new SqlServerReferenceCollector();
        document.Fragment.Accept(collector);
        collector.Names.ExceptWith(collector.LocalNames);
        return collector.Names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<SqlValidationLocation> Locations(SqlEngine engine, string sql)
    {
        if (engine == SqlEngine.Postgres)
            return [];
        var document = SqlServerDocument.Parse(sql);
        if (document.HasErrors || document.Fragment is not TSqlScript script)
            return [];
        return script.Batches.SelectMany(batch => batch.Statements)
            .Select(statement => new SqlValidationLocation(statement.StartOffset, statement.StartLine, statement.StartColumn, statement.GetType().Name))
            .ToArray();
    }

    private sealed class SqlServerReferenceCollector : TSqlFragmentVisitor
    {
        internal HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> LocalNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public override void ExplicitVisit(VariableReference node)
        {
            if (!node.Name.StartsWith("@@", StringComparison.Ordinal))
                Names.Add(node.Name);
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
                LocalNames.Add(declaration.VariableName.Value);
            base.ExplicitVisit(node);
        }
    }
}
