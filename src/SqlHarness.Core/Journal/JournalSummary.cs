using System.Text.Json;

namespace SqlHarness.Core;

/// <summary>
/// Bounded, value-free operation summary for the journal: verdicts, counts, and
/// flagged operators (physical op, object, flags). Never parameter or matrix
/// values, predicates, statement text, or result data.
/// </summary>
internal static class JournalSummary
{
    internal const int MaximumOperators = 10;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static string? Build(object? report) => report switch
    {
        SqlHarnessMeasureReport measure => Serialize(new
        {
            kind = "measure",
            resultsStable = measure.ResultsStable,
            operators = Operators(measure.Query),
        }),
        SqlHarnessMeasureSetReport sets => Serialize(new
        {
            kind = "measure-sets",
            sets = sets.Sets.Count,
            stableSets = sets.Sets.Count(set => set.ResultsStable),
        }),
        SqlHarnessCompareReport compare => Serialize(new
        {
            kind = "compare",
            comparison = compare.Equivalence.Mode.ToString(),
            resultsEquivalent = compare.ResultsEquivalent,
            baselineOperators = Operators(compare.Baseline),
            candidateOperators = Operators(compare.Candidate),
        }),
        SqlHarnessCompareMatrixReport matrix => Serialize(new
        {
            kind = "compare-matrix",
            parameterName = matrix.ParameterName,
            parameterType = matrix.ParameterType,
            cells = matrix.Cells.Count,
            equivalentCells = matrix.Cells.Count(cell => cell.Compare.ResultsEquivalent == true),
        }),
        _ => null,
    };

    private static object[] Operators(CompareVariantReport variant) =>
        variant.Operators
            .Where(op => op.HasWarnings || op.HasSpill || op.HasImplicitConversion)
            .Take(MaximumOperators)
            .Select(op => (object)new
            {
                nodeId = op.NodeId,
                physicalOp = op.PhysicalOp,
                @object = op.Object,
                warnings = op.HasWarnings,
                spill = op.HasSpill,
                implicitConversion = op.HasImplicitConversion,
            })
            .ToArray();

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}