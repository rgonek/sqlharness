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
