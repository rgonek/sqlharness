using System.Globalization;

using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests;

// 018: every concrete ScriptDom table source and external call is classified.
// ParserType is the highest TSql<digits>Parser in the ScriptDom package.
public class SqlSafetyNodeCoverageTests
{
    // 018: concrete TableReference types absent from AllowedTableReferenceTypes.
    // Denied by omission. Not added to the allow-set.
    private static readonly IReadOnlySet<Type> KnownDeniedTableReferenceTypes = new HashSet<Type>
    {
        typeof(AdHocTableReference),
        typeof(AIGenerateChunksTableReference),
        typeof(AIGenerateFixedChunksTableReference),
        typeof(BulkOpenRowset),
        typeof(InternalOpenRowset),
        typeof(OpenQueryTableReference),
        typeof(OpenRowsetCosmos),
        typeof(OpenRowsetTableReference),
        typeof(OpenXmlTableReference),
        typeof(PredictTableReference),
        typeof(VectorSearchTableReference),
    };

    [Fact]
    public void Every_concrete_TableReference_type_is_classified()
    {
        var concrete = ConcreteSubclasses(typeof(TableReference));
        Assert.NotEmpty(concrete);

        var unclassified = concrete
            .Where(type => !SqlSafetyClassifier.AllowedTableReferenceTypes.Contains(type)
                && !KnownDeniedTableReferenceTypes.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            "Unclassified TableReference types: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void Every_AI_or_external_call_expression_type_is_denied()
    {
        // 018: Visit(PrimaryExpression) is the Step 4 hook, so the scan uses that base.
        var reflected = ConcreteSubclasses(typeof(PrimaryExpression))
            .Where(type => type.Name.StartsWith("AI", StringComparison.Ordinal)
                || type.Name.Contains("External", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(reflected);

        var missing = reflected
            .Where(type => !SqlSafetyClassifier.DeniedExpressionTypes.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "AI or external call expression types not in DeniedExpressionTypes: " + string.Join(", ", missing));
    }

    // 018: ParserType must be the highest TSql<digits>Parser in the package.
    [Fact]
    public void The_parser_is_the_newest_in_the_package()
    {
        var parsers = typeof(TSqlFragment).Assembly
            .GetTypes()
            .Select(type => (Type: type, Version: ParserVersion(type.Name)))
            .Where(item => item.Version is not null)
            .Select(item => (item.Type, Version: item.Version!.Value))
            .ToArray();
        Assert.NotEmpty(parsers);

        var newest = parsers.MaxBy(item => item.Version).Type;
        Assert.Equal(newest, SqlServerDocument.ParserType);
    }

    private static Type[] ConcreteSubclasses(Type baseType) =>
        typeof(TSqlFragment).Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && baseType.IsAssignableFrom(type))
            .ToArray();

    // 018: type names matching TSql\d+Parser. The digits are the parser generation.
    private static int? ParserVersion(string name)
    {
        const string prefix = "TSql";
        const string suffix = "Parser";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal)
            || name.Length <= prefix.Length + suffix.Length)
        {
            return null;
        }

        var digits = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }
}