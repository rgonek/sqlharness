using System.Data;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Journal;

public sealed class ConditionsModelBuilderTests
{
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

    private static SqlHarnessMeasureOperation Measure() => new(
        new SqlTargetRequest("profile", new Dictionary<string, string>()), "SELECT 1", "SELECT @N", [], 30, 5);

    internal static SqlHarnessParameter Parameter(string name, SqlDbType type, object value, int? size, byte? precision = null, byte? scale = null) =>
        new(name, type, value, size, precision, scale);
}