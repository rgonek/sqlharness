using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Core;

public sealed record SqlValidationLocation(int StartOffset, int StartLine, int StartColumn, string Kind);

public sealed record SqlValidationParameter(string Name, string Type);

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
    bool Executed = false);

public static class SqlValidation
{
    public static SqlValidationReport Validate(
        SqlTargetRequest targetRequest,
        string sql,
        IReadOnlyList<string> parameterDeclarations,
        IReadOnlyDictionary<string, TargetProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(targetRequest);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameterDeclarations);
        ArgumentNullException.ThrowIfNull(profiles);

        var target = TargetResolver.Resolve(targetRequest, profiles);
        var dialect = SqlDialects.For(target.Engine);
        var decision = dialect.Classify(sql, SqlUsage.Query, target.Database, allowMutation: false, confirmDatabase: null, new HashSet<string>(StringComparer.Ordinal));
        var requiredNames = SqlParameterReferences.Collect(target.Engine, sql)
            .Select(CanonicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var astLocations = SqlParameterReferences.Locations(target.Engine, sql);
        IReadOnlyList<SqlHarnessParameter> parsedParameters = [];
        string? reason = decision.Allowed ? null : SafeReason(decision.Reason);
        if (decision.Allowed)
        {
            try
            {
                parsedParameters = dialect.ParseParameters(parameterDeclarations);
                dialect.ValidateParameterReferences(parsedParameters, sql);
            }
            catch (SqlHarnessSafetyException)
            {
                reason = "parameter_validation_failed";
            }
        }
        var suppliedNames = parsedParameters.Select(parameter => CanonicalName(parameter.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingNames = requiredNames.Where(name => !suppliedNames.Contains(name)).ToArray();
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
            Executed: false);
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

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
            return [];
        var collector = new SqlServerReferenceCollector();
        fragment.Accept(collector);
        collector.Names.ExceptWith(collector.LocalNames);
        return collector.Names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<SqlValidationLocation> Locations(SqlEngine engine, string sql)
    {
        if (engine == SqlEngine.Postgres)
            return [];
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
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
