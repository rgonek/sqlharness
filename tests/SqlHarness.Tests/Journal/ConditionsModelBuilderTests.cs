using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Journal;

public sealed class ConditionsModelBuilderTests
{
    [Fact]
    public void Varying_fingerprint_ignores_fixed_parameters_and_tracks_values()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var salt = new byte[32];
        ConditionsModel Build(string fixedName, int value) => ActivityJournal.SealConditions(
            ConditionsModelBuilder.Build(Measure(), target, new SqlServerDialect(),
                [Parameter(fixedName, SqlDbType.Int, 1, null)],
                [new ConditionsCaseInput("0", null, [Parameter("clientId", SqlDbType.Int, value, null)])], true), salt);
        var first = Build("Now", 6795);
        var second = Build("Today", 6795);
        var changed = Build("Now", 18);
        Assert.Equal(first.Cases[0].VaryingFingerprint, second.Cases[0].VaryingFingerprint);
        Assert.NotEqual(first.Cases[0].Fingerprint, second.Cases[0].Fingerprint);
        Assert.NotEqual(first.Cases[0].VaryingFingerprint, changed.Cases[0].VaryingFingerprint);
    }

    [Fact]
    public void Effective_types_are_bound_types()
    {
        Assert.Equal("nvarchar(4000)", SqlTypes.Effective(Parameter("S", SqlDbType.NVarChar, "abcdefghij", null)));
        Assert.Equal("nvarchar(max)", SqlTypes.Effective(Parameter("S", SqlDbType.NVarChar, new string('x', 5000), -1)));
        Assert.Equal("decimal(18,4)", SqlTypes.Effective(Parameter("D", SqlDbType.Decimal, 1.25m, null, 18, 4)));
        Assert.Equal("datetime", SqlTypes.Effective(Parameter("D", SqlDbType.DateTime, DateTime.UnixEpoch, null)));
        Assert.Equal("decimal(3,2)", SqlTypes.Effective(Parameter("D", SqlDbType.Decimal, 1.25m, null)));
    }

    [Fact]
    public void Matrix_and_equivalent_named_sets_have_identical_case_fingerprints()
    {
        var dialect = new SqlServerDialect();
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var operation = new SqlHarnessCompareMatrixOperation(
            new SqlTargetRequest("profile", new Dictionary<string, string>()), null,
            "SELECT @ClassType", "SELECT @ClassType", [], 30, 5, "");
        var matrix = ParameterSetResolver.Resolve(dialect, [],
            new SqlHarnessParameterMatrixInput("ClassType", "nvarchar(4000)", ["A", "B"]), null);
        var namedSets = ParameterSetResolver.Resolve(dialect, [], null,
        [
            new SqlHarnessParameterSetInput("a", []) { TypedParameters = [new("ClassType", "nvarchar(4000)", "A")] },
            new SqlHarnessParameterSetInput("b", []) { TypedParameters = [new("ClassType", "nvarchar(4000)", "B")] },
        ]);

        var matrixModel = ConditionsModelBuilder.Build(operation, target, dialect, [],
            matrix.Select(set => new ConditionsCaseInput(set.Name, null, set.Parameters)).ToArray(), true);
        var setModel = ConditionsModelBuilder.Build(operation, target, dialect, [],
            namedSets.Select(set => new ConditionsCaseInput(set.Name, set.Name, set.Parameters)).ToArray(), true);
        var salt = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        matrixModel = ActivityJournal.SealConditions(matrixModel, salt);
        setModel = ActivityJournal.SealConditions(setModel, salt);

        Assert.Equal(["0", "1"], matrixModel.Cases.Select(item => item.Id));
        Assert.Equal(["a", "b"], setModel.Cases.Select(item => item.Id));
        Assert.Equal(matrixModel.Fixed, setModel.Fixed);
        Assert.Equal(matrixModel.Cases.Select(item => item.Varying.Single().Value!.Value.GetString()),
            setModel.Cases.Select(item => item.Varying.Single().Value!.Value.GetString()));
        Assert.Equal(matrixModel.Cases.Select(item => item.Fingerprint), setModel.Cases.Select(item => item.Fingerprint));
        Assert.Equal(matrixModel.Fingerprint, setModel.Fingerprint);
    }

    [Fact]
    public void Fingerprints_are_keyed_value_sensitive_and_ignore_repeat()
    {
        var operation = Measure();
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var parameters = new[] { Parameter("N", SqlDbType.Int, 0, null) };
        var first = ConditionsModelBuilder.Build(operation, target, new SqlServerDialect(), parameters, [], true);
        var changedRepeat = ConditionsModelBuilder.Build(operation with { Repeat = 10 }, target, new SqlServerDialect(), parameters, [], true);
        var changedValue = ConditionsModelBuilder.Build(operation, target, new SqlServerDialect(), [Parameter("N", SqlDbType.Int, 1, null)], [], true);

        var saltA = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var saltB = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
        Assert.NotEqual(ActivityJournal.SealConditions(first, saltA).Fingerprint, ActivityJournal.SealConditions(first, saltB).Fingerprint);
        Assert.Equal(ActivityJournal.SealConditions(first, saltA).Fingerprint, ActivityJournal.SealConditions(changedRepeat, saltA).Fingerprint);
        Assert.NotEqual(ActivityJournal.SealConditions(first, saltA).Fingerprint, ActivityJournal.SealConditions(changedValue, saltA).Fingerprint);
    }

    [Fact]
    public void Measurement_fingerprint_groups_compare_and_measure_without_changing_operation_fingerprint()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var parameters = new[] { Parameter("N", SqlDbType.Int, 7, null) };
        var cases = new[] { new ConditionsCaseInput("0", null, []) };
        var measure = ConditionsModelBuilder.Build(Measure(), target, new SqlServerDialect(), parameters, cases, true);
        var compare = ConditionsModelBuilder.Build(new SqlHarnessCompareOperation(
            new SqlTargetRequest("profile", new Dictionary<string, string>()), "SELECT 1",
            "SELECT @N", "SELECT @N", ["N:int=7"], 30, 5, ResultComparisonMode.Ordered, true),
            target, new SqlServerDialect(), parameters, cases, true);
        var salt = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        var sealedMeasure = ActivityJournal.SealConditions(measure, salt);
        var sealedCompare = ActivityJournal.SealConditions(compare, salt);

        Assert.NotEqual(sealedMeasure.Fingerprint, sealedCompare.Fingerprint);
        Assert.Equal(sealedMeasure.MeasurementFingerprint, sealedCompare.MeasurementFingerprint);
        Assert.Equal(sealedMeasure.Cases[0].MeasurementFingerprint, sealedCompare.Cases[0].MeasurementFingerprint);
    }

    [Fact]
    public void Measurement_fingerprint_ignores_comparison_mode_but_tracks_values_and_salt()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("profile", new Dictionary<string, string>()), null,
            "SELECT @N", "SELECT @N", ["N:int=7"], 30, 5, ResultComparisonMode.Ordered, true);
        var cases = new[] { new ConditionsCaseInput("0", null, [Parameter("N", SqlDbType.Int, 7, null)]) };
        var ordered = ConditionsModelBuilder.Build(operation, target, new SqlServerDialect(), [], cases, true);
        var multiset = ConditionsModelBuilder.Build(operation with { CompareResults = ResultComparisonMode.Multiset },
            target, new SqlServerDialect(), [], cases, true);
        var changedValue = ConditionsModelBuilder.Build(operation, target, new SqlServerDialect(),
            [], [new ConditionsCaseInput("0", null, [Parameter("N", SqlDbType.Int, 8, null)])], true);
        var saltA = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var saltB = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();

        var orderedA = ActivityJournal.SealConditions(ordered, saltA);
        Assert.Equal(orderedA.MeasurementFingerprint, ActivityJournal.SealConditions(multiset, saltA).MeasurementFingerprint);
        Assert.NotEqual(orderedA.MeasurementFingerprint, ActivityJournal.SealConditions(changedValue, saltA).MeasurementFingerprint);
        Assert.NotEqual(orderedA.Cases[0].MeasurementFingerprint,
            ActivityJournal.SealConditions(changedValue, saltA).Cases[0].MeasurementFingerprint);
        Assert.NotEqual(orderedA.MeasurementFingerprint, ActivityJournal.SealConditions(ordered, saltB).MeasurementFingerprint);
        Assert.NotEqual(orderedA.Fingerprint, ActivityJournal.SealConditions(multiset, saltA).Fingerprint);
    }

    [Fact]
    public void Measurement_fingerprint_ignores_compare_nullability()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var operation = new SqlHarnessCompareOperation(
            new SqlTargetRequest("profile", new Dictionary<string, string>()), null,
            "SELECT @N", "SELECT @N", ["N:int=7"], 30, 5, ResultComparisonMode.Ordered, true);
        var cases = new[] { new ConditionsCaseInput("0", null, [Parameter("N", SqlDbType.Int, 7, null)]) };
        var compareNullability = ConditionsModelBuilder.Build(operation, target, new SqlServerDialect(), [], cases, true);
        var ignoreNullability = ConditionsModelBuilder.Build(operation with { CompareNullability = false },
            target, new SqlServerDialect(), [], cases, true);
        var salt = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        var includeNullability = ActivityJournal.SealConditions(compareNullability, salt);
        var excludeNullability = ActivityJournal.SealConditions(ignoreNullability, salt);

        Assert.Equal(includeNullability.MeasurementFingerprint, excludeNullability.MeasurementFingerprint);
        Assert.Equal(includeNullability.Cases[0].MeasurementFingerprint, excludeNullability.Cases[0].MeasurementFingerprint);
    }

    [Fact]
    public void Legacy_rows_fall_back_to_operation_fingerprint()
    {
        var model = new ConditionsModel(1, "sqlserver", "server", "database",
            new ConditionsExecution("measure", "per-case", "per-case", 1, 1, 30, null, null), null,
            [], [], "legacy-operation-fingerprint", []);

        Assert.Equal("legacy-operation-fingerprint", model.EffectiveMeasurementFingerprint);
    }

    [Fact]
    public void Values_and_setup_text_are_absent_without_sensitive_storage()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var model = ConditionsModelBuilder.Build(Measure() with { SetupSql = "CREATE TABLE #t(i int);" }, target,
            new SqlServerDialect(), [Parameter("N", SqlDbType.Int, 0, null)], [], false);

        Assert.Null(model.Fixed[0].Value);
        Assert.Null(model.Setup!.Text);
        var sealedModel = ActivityJournal.SealConditions(model, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        Assert.NotEmpty(sealedModel.Setup!.Hash);
        Assert.NotEmpty(sealedModel.Fingerprint);
    }

    [Fact]
    public void Materialized_table_and_list_conditions_store_typed_shape_and_journal_keyed_content_hash()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var operation = Measure();
        var model = ConditionsModelBuilder.Build(operation, target, new SqlServerDialect(), [],
            [new ConditionsCaseInput("0", null, [])], storeSensitive: true);
        var details = new MaterializedParameterInfo[]
        {
            new("Ids", "tvp", "IdList", "dbo.IdList",
                [new TableTypeColumn("Id", SqlDbType.Int, 4, 10, 0, false)], 2, null,
                [["11"], ["12"]], []),
            new("Types", "list", "int", null, [], 3, null, [], ["1", "2", "3"]),
        };
        var enriched = ConditionsModelBuilder.WithMaterializedParameters(model, 0, details, storeSensitive: true);
        var salt = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        Assert.Equal(2, enriched.Version);
        Assert.Equal("dbo.IdList READONLY", enriched.Cases[0].Varying[0].EffectiveType);
        Assert.Equal("list of int", enriched.Cases[0].Varying[1].EffectiveType);
        Assert.Equal(2, enriched.Cases[0].Varying[0].Count);
        Assert.Equal(["Id"], enriched.Cases[0].Varying[0].Columns);
        Assert.Equal("[[\"11\"],[\"12\"]]", enriched.Cases[0].Varying[0].Rows!.Value.GetRawText());
        Assert.Equal("[\"1\",\"2\",\"3\"]", enriched.Cases[0].Varying[1].Values!.Value.GetRawText());

        var sealedA = ActivityJournal.SealConditions(enriched, salt);
        var changedContent = ConditionsModelBuilder.WithMaterializedParameters(model, 0,
            [details[0] with { Rows = [["99"], ["12"]] }, details[1]], storeSensitive: true);
        var sealedChanged = ActivityJournal.SealConditions(changedContent, salt);
        Assert.NotNull(sealedA.Cases[0].Varying[0].ContentHash);
        Assert.NotEqual(sealedA.Cases[0].Varying[0].ContentHash, sealedChanged.Cases[0].Varying[0].ContentHash);
        Assert.NotEqual(sealedA.Cases[0].Fingerprint, sealedChanged.Cases[0].Fingerprint);
    }

    [Fact]
    public void Materialized_sensitive_content_and_source_text_are_omitted_when_storage_is_disabled()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var model = ConditionsModelBuilder.Build(Measure(), target, new SqlServerDialect(), [],
            [new ConditionsCaseInput("0", null, [])], storeSensitive: false);
        var details = new[]
        {
            new MaterializedParameterInfo("Ids", "tvp", "dbo.IdList", "dbo.IdList",
                [new TableTypeColumn("Id", SqlDbType.Int, 4, 10, 0, false)], 1,
                "SELECT SECRET_SOURCE", [["SECRET_ROW"]], []),
        };
        var enriched = ConditionsModelBuilder.WithMaterializedParameters(model, 0, details, storeSensitive: false);
        var sealedModel = ActivityJournal.SealConditions(enriched, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var bounded = ActivityJournal.BoundConditions(sealedModel, storeSensitive: false);
        var serialized = System.Text.Json.JsonSerializer.Serialize(bounded);

        Assert.Null(bounded.Cases[0].Varying[0].Rows);
        Assert.Null(bounded.Cases[0].Varying[0].Values);
        Assert.Null(bounded.Cases[0].Varying[0].SourceText);
        Assert.Equal(1, bounded.Cases[0].Varying[0].Count);
        Assert.Equal("dbo.IdList READONLY", bounded.Cases[0].Varying[0].EffectiveType);
        Assert.NotEmpty(bounded.Cases[0].Varying[0].ContentHash!);
        Assert.Contains("ContentHash", typeof(ConditionsParameter).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("SECRET_ROW", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_SOURCE", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_materialized_content_changes_keyed_operation_fingerprint()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var model = ConditionsModelBuilder.Build(Measure(), target, new SqlServerDialect(), [],
            [new ConditionsCaseInput("0", null, [])], storeSensitive: true);
        var first = new MaterializedParameterInfo("Ids", "tvp", "dbo.IdList", "dbo.IdList",
            [new TableTypeColumn("Id", SqlDbType.Int, 4, 10, 0, false)], 1, "SELECT Id FROM dbo.Source", [["11"]], []);
        var second = first with { Rows = [["12"]] };
        var fixedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ids" };
        var salt = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        var firstModel = ConditionsModelBuilder.WithMaterializedParameters(model, 0, [first], true, fixedNames);
        var secondModel = ConditionsModelBuilder.WithMaterializedParameters(model, 0, [second], true, fixedNames);

        Assert.NotEqual(ActivityJournal.SealConditions(firstModel, salt).Fingerprint,
            ActivityJournal.SealConditions(secondModel, salt).Fingerprint);
    }

    [Fact]
    public void Oversized_materialized_rows_are_omitted_with_evidence()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var model = ConditionsModelBuilder.Build(Measure(), target, new SqlServerDialect(), [],
            [new ConditionsCaseInput("0", null, [])], storeSensitive: true);
        var details = new[]
        {
            new MaterializedParameterInfo("Ids", "tvp", "dbo.IdList", "dbo.IdList",
                [new TableTypeColumn("Id", SqlDbType.NVarChar, -1, 0, 0, true)], 1,
                "SELECT SECRET_SOURCE", [[new string('x', 1_100_000)]], []),
        };
        var enriched = ConditionsModelBuilder.WithMaterializedParameters(model, 0, details, storeSensitive: true);
        var bounded = ActivityJournal.OmitOversizedConditions(enriched);

        Assert.Null(bounded.Cases[0].Varying[0].Rows);
        Assert.NotNull(bounded.Cases[0].Varying[0].ContentForFingerprint);
        Assert.Contains("cases.0.@Ids.rows", bounded.Omitted);
    }

    [Fact]
    public void Oversized_fixed_list_values_are_omitted_and_case_label_is_rebuilt_within_conditions_bound()
    {
        var target = new ResolvedTarget("server", "database", null!, "integrated");
        var model = ConditionsModelBuilder.Build(Measure(), target, new SqlServerDialect(), [],
            [new ConditionsCaseInput("0", null, [])], storeSensitive: true);
        var largeValue = new string('x', 400_000);
        var details = new[]
        {
            new MaterializedParameterInfo("Types", "list", "nvarchar(max)", null, [], 3, null, [],
                [largeValue, largeValue, largeValue]),
        };
        var fixedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Types" };
        var enriched = ConditionsModelBuilder.WithMaterializedParameters(model, 0, details, true, fixedNames);
        Assert.True(enriched.Cases[0].Label.Length > 1_000_000);

        var bounded = ActivityJournal.OmitOversizedConditions(enriched);
        var serialized = System.Text.Json.JsonSerializer.Serialize(bounded);

        Assert.Null(bounded.Fixed[0].Values);
        Assert.Contains("fixed.@Types.values", bounded.Omitted);
        Assert.Equal("@Types IN (3 values)", bounded.Cases[0].Label);
        Assert.DoesNotContain(largeValue, serialized, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(serialized) <= 1_048_576);
    }

    private static SqlHarnessMeasureOperation Measure() => new(
        new SqlTargetRequest("profile", new Dictionary<string, string>()), "SELECT 1", "SELECT @N", [], 30, 5);

    internal static SqlHarnessParameter Parameter(string name, SqlDbType type, object value, int? size, byte? precision = null, byte? scale = null) =>
        new(name, type, value, size, precision, scale);
}
