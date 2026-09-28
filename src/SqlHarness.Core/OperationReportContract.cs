namespace SqlHarness.Core;

/// <summary>
/// Internal typed-result contract evaluated for T2: every
/// <see cref="SqlHarnessOperation"/> maps to the report type(s) its success
/// path may carry. The public <see cref="SqlHarnessOutcome"/> keeps
/// <c>object? Report</c> as a compatible adapter (see ADR); this contract is
/// enforced on the internal dispatch so an operation can never be paired
/// with a mismatched report.
/// </summary>
internal static class OperationReportContract
{
    private static readonly IReadOnlyDictionary<Type, IReadOnlySet<Type>> ExpectedReports =
        new Dictionary<Type, IReadOnlySet<Type>>
        {
            [typeof(SqlHarnessQueryOperation)] = new HashSet<Type> { typeof(SqlHarnessQueryReport) },
            [typeof(SqlHarnessCompareOperation)] = new HashSet<Type> { typeof(SqlHarnessCompareReport) },
            [typeof(SqlHarnessCompareMatrixOperation)] = new HashSet<Type> { typeof(SqlHarnessCompareMatrixReport) },
            [typeof(SqlHarnessMeasureOperation)] = new HashSet<Type> { typeof(SqlHarnessMeasureReport), typeof(SqlHarnessMeasureSetReport) },
            [typeof(SqlHarnessGainOperation)] = new HashSet<Type> { typeof(SqlHarnessGainReport) },
            [typeof(SqlHarnessPlanOperation)] = new HashSet<Type> { typeof(DistilledPlan) },
            [typeof(SqlHarnessSchemaOperation)] = new HashSet<Type> { typeof(SqlHarnessSchemaReport) },
            [typeof(SqlHarnessPingOperation)] = new HashSet<Type> { typeof(SqlHarnessPingReport) },
            [typeof(SqlHarnessCountsOperation)] = new HashSet<Type> { typeof(SqlHarnessCountsReport) },
            [typeof(SqlHarnessSpaceOperation)] = new HashSet<Type> { typeof(SqlHarnessSpaceReport) },
            [typeof(SqlHarnessWatchOperation)] = new HashSet<Type> { typeof(SqlHarnessWatchReport) },
            [typeof(SqlHarnessSnapshotOperation)] = new HashSet<Type> { typeof(SqlHarnessSnapshotReport) },
            [typeof(SqlHarnessQueryStoreTopOperation)] = new HashSet<Type> { typeof(SqlHarnessQueryStoreTopReport) },
            [typeof(SqlHarnessIndexesOperation)] = new HashSet<Type> { typeof(SqlHarnessIndexesReport) },
        };

    /// <summary>
    /// All operation types the contract knows. Tests assert this stays in
    /// sync with the closed <see cref="SqlHarnessOperation"/> family.
    /// </summary>
    internal static IReadOnlyCollection<Type> KnownOperations => ExpectedReports.Keys.ToArray();

    internal static IReadOnlySet<Type> ReportsFor(SqlHarnessOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (ExpectedReports.TryGetValue(operation.GetType(), out var reports))
            return reports;
        throw new InvalidOperationException(
            $"No report contract is registered for operation '{operation.GetType().Name}'.");
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when a non-null report
    /// does not belong to <paramref name="operation"/>. Null reports mark
    /// failure outcomes and are always compatible.
    /// </summary>
    internal static void AssertCompatible(SqlHarnessOperation operation, object? report)
    {
        if (report is null)
            return;
        var expected = ReportsFor(operation);
        if (!expected.Contains(report.GetType()))
            throw new InvalidOperationException(
                $"Report '{report.GetType().Name}' does not belong to operation '{operation.GetType().Name}'. " +
                $"Expected one of: {string.Join(", ", expected.Select(type => type.Name).Order(StringComparer.Ordinal))}.");
    }
}
