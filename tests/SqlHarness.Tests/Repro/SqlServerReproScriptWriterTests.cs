using System.Text.Json;

using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests.Repro;

public sealed class SqlServerReproScriptWriterTests
{
    [Fact]
    public void As_executed_matches_bound_definitions_and_documents_client_session()
    {
        var model = Model(
            Parameter("NClients", "int", "0"),
            Parameter("NGroups", "int", "0"),
            Parameter("NCountries", "int", "1"),
            Parameter("ClassType", "nvarchar(4000)", "\"ClientConfigurationModel2\""));

        var result = ReproScripts.Create(model, "SELECT @NClients, @ClassType", "candidate", "0", ReproScriptForm.AsExecuted);

        Assert.Equal(ReproCompleteness.Complete, result.Completeness);
        Assert.Equal("As executed (sp_executesql)", result.Label);
        Assert.Contains("SET ARITHABORT OFF;", result.Text);
        Assert.Contains("EXEC sys.sp_executesql N'", result.Text);
        Assert.Contains("N'@NClients int, @NGroups int, @NCountries int, @ClassType nvarchar(4000)'", result.Text);
        Assert.Contains("@ClassType = N'ClientConfigurationModel2'", result.Text);
        Assert.Contains("side: candidate", result.Text);
        Assert.Contains("case: Case 1 of 1", result.Text);
        Assert.Contains("new connection; setup runs per case", result.Text);
        Assert.Contains("SQLHarness discards one warm-up and reports medians; this script runs once", result.Text);
        Assert.Contains("ARITHABORT OFF reflects SqlClient's documented client default; this session setting was not journaled", result.Text);
        Assert.DoesNotContain("SET NOCOUNT ON", result.Text);
        Assert.Empty(ParseErrors(result.Text!));
    }

    [Fact]
    public void Local_variables_form_declares_each_parameter_and_warns_about_estimates()
    {
        var model = Model(Parameter("NClients", "int", "0"), Parameter("ClassType", "nvarchar(4000)", "\"ClientConfigurationModel2\""));

        var result = ReproScripts.Create(model, "SELECT @NClients, @ClassType", "query", "0", ReproScriptForm.LocalVariables);

        Assert.Equal("Local variables — estimates may differ", result.Label);
        Assert.Contains("DECLARE @NClients int = 0;", result.Text);
        Assert.Contains("DECLARE @ClassType nvarchar(4000) = N'ClientConfigurationModel2';", result.Text);
        Assert.Contains("SELECT @NClients, @ClassType", result.Text);
        Assert.Empty(ParseErrors(result.Text!));
    }

    [Fact]
    public void Recompile_everywhere_is_labelled_equivalent()
    {
        var model = Model();
        var result = ReproScripts.Create(model, "SELECT 1 OPTION (RECOMPILE)", "query", "0", ReproScriptForm.LocalVariables);
        Assert.Equal("Local variables — equivalent (every statement uses OPTION (RECOMPILE))", result.Label);
    }

    [Theory]
    [InlineData("SELECT N'OPTION (RECOMPILE)'")]
    [InlineData("SELECT 1 /* OPTION (RECOMPILE) */")]
    [InlineData("SELECT [OPTION (RECOMPILE)] FROM dbo.T")]
    public void Recompile_text_without_an_ast_hint_does_not_claim_equivalence(string sql)
    {
        var result = ReproScripts.Create(Model(), sql, "query", "0", ReproScriptForm.LocalVariables);
        Assert.Equal("Local variables — estimates may differ", result.Label);
    }

    [Fact]
    public void Every_query_statement_must_have_an_ast_recompile_hint()
    {
        var result = ReproScripts.Create(Model(), "SELECT 1 OPTION (RECOMPILE); SELECT 2", "query", "0", ReproScriptForm.LocalVariables);
        Assert.Equal("Local variables — estimates may differ", result.Label);
    }

    [Fact]
    public void Setup_split_matches_execution_commands()
    {
        const string setup = "CREATE TABLE #t(i int); INSERT #t SELECT @N;";
        var model = Model(Parameter("N", "int", "7")) with
        {
            Setup = new ConditionsSetup("hash", setup),
        };

        var result = ReproScripts.Create(model, "SELECT * FROM #t", "query", "0", ReproScriptForm.AsExecuted);
        var commands = SetupSqlExecution.PrepareCommands(setup, [Bound("N", System.Data.SqlDbType.Int, 7)], 30);

        Assert.Equal(2, commands.Count);
        Assert.Contains("CREATE TABLE #t(i int);", result.Text);
        Assert.Contains("GO", result.Text);
        Assert.Contains("INSERT #t SELECT @N", result.Text);
        Assert.Contains("@N = 7", result.Text);
        Assert.Empty(ParseErrors(result.Text!));
    }

    [Theory]
    [InlineData(ReproScriptForm.AsExecuted, "EXEC sys.sp_executesql")]
    [InlineData(ReproScriptForm.LocalVariables, "SELECT * FROM dbo.MeasuredTable")]
    public void Statistics_start_after_reading_setup_and_immediately_before_measured_sql(
        ReproScriptForm form,
        string measuredSql)
    {
        const string setup = "SELECT * FROM dbo.SetupTable;";
        var model = Model() with { Setup = new ConditionsSetup("hash", setup) };

        var result = ReproScripts.Create(model, "SELECT * FROM dbo.MeasuredTable", "query", "0", form);
        var text = result.Text!;

        var arithAbort = text.IndexOf("SET ARITHABORT OFF;", StringComparison.Ordinal);
        var setupRead = text.IndexOf(setup, StringComparison.Ordinal);
        var statistics = text.IndexOf("SET STATISTICS IO, TIME ON;", StringComparison.Ordinal);
        var measured = text.IndexOf(measuredSql, StringComparison.Ordinal);
        Assert.True(arithAbort >= 0 && arithAbort < setupRead);
        Assert.True(setupRead < statistics);
        Assert.True(statistics < measured);
        var linesAfterStatistics = text[(statistics)..].Split('\n');
        Assert.StartsWith(measuredSql, linesAfterStatistics[1].Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void Literals_round_trip_for_supported_values()
    {
        Assert.Equal("N'O''Brien'", SqlServerLiteral.Format(Parameter("s", "nvarchar(20)", "\"O'Brien\"")));
        Assert.Equal("N'Zażółć'", SqlServerLiteral.Format(Parameter("s", "nvarchar(20)", "\"Zażółć\"")));
        Assert.Equal("NULL", SqlServerLiteral.Format(Parameter("n", "int", "null")));
        Assert.Equal("'2026-10-16T12:34:56.997'", SqlServerLiteral.Format(Parameter("d", "datetime", "\"2026-10-16T12:34:56.997\"")));
        Assert.Equal("'2026-10-16T12:34:56.1234567'", SqlServerLiteral.Format(Parameter("d", "datetime2(7)", "\"2026-10-16T12:34:56.1234567\"")));
        Assert.Equal("0x0A0B", SqlServerLiteral.Format(Parameter("b", "varbinary(2)", "\"Cgs=\"")));
        Assert.Equal("1234567890123456789012345678.1234567890", SqlServerLiteral.Format(Parameter("d", "decimal(38,10)", "1234567890123456789012345678.1234567890")));
        Assert.Equal("1", SqlServerLiteral.Format(Parameter("b", "bit", "true")));
    }

    [Fact]
    public void Missing_values_render_typed_placeholders()
    {
        foreach (var missingValue in new JsonElement?[] { null, default(JsonElement) })
        {
            var model = Model(new ConditionsParameter("@N", "int", missingValue));
            foreach (var form in new[] { ReproScriptForm.AsExecuted, ReproScriptForm.LocalVariables })
            {
                var result = ReproScripts.Create(model, "SELECT @N", "query", "0", form);
                Assert.Equal(ReproCompleteness.ValuesMissing, result.Completeness);
                Assert.Equal("Incomplete — values not stored", result.Label);
                Assert.Contains(form == ReproScriptForm.AsExecuted
                    ? "@N = /* value not stored */"
                    : "DECLARE @N int = /* value not stored */;", result.Text);
                if (form == ReproScriptForm.AsExecuted)
                    Assert.Contains("N'@N int'", result.Text);
                Assert.DoesNotContain("@N = NULL", result.Text);
            }
        }
    }

    [Fact]
    public void Null_values_keep_declared_type_without_an_exec_expression()
    {
        var model = Model(Parameter("N", "int", "null"));

        var asExecuted = ReproScripts.Create(model, "SELECT @N", "query", "0", ReproScriptForm.AsExecuted);
        var localVariables = ReproScripts.Create(model, "SELECT @N", "query", "0", ReproScriptForm.LocalVariables);

        Assert.Contains("N'@N int'", asExecuted.Text);
        Assert.Contains("@N = NULL", asExecuted.Text);
        Assert.DoesNotContain("CAST(NULL AS int)", asExecuted.Text);
        Assert.Contains("DECLARE @N int = NULL;", localVariables.Text);
    }

    [Fact]
    public void Without_stored_sql_script_is_unavailable()
    {
        var result = ReproScripts.Create(Model(), null, "query", "0", ReproScriptForm.AsExecuted);
        Assert.Equal(ReproCompleteness.Unavailable, result.Completeness);
        Assert.Null(result.Text);
        Assert.Equal("Script not available — enable storeSensitive", result.Label);
    }

    [Fact]
    public void Postgres_is_unavailable()
    {
        var model = Model() with { Engine = "postgres" };
        var result = ReproScripts.Create(model, "SELECT 1", "query", "0", ReproScriptForm.AsExecuted);
        Assert.Equal(ReproCompleteness.Unavailable, result.Completeness);
        Assert.Null(result.Text);
        Assert.Equal("Script not available for this engine", result.Label);
    }

    private static ConditionsModel Model(params ConditionsParameter[] fixedParameters) => new(
        1, "sqlserver", "server", "database",
        new ConditionsExecution("measure", "per-case", "per-case", 1, 5, 30, null, null),
        null, fixedParameters, [new ConditionsCase("0", 0, "Case 1 of 1", null, [], "fingerprint")], "fingerprint", []);

    private static ConditionsParameter Parameter(string name, string type, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new ConditionsParameter("@" + name, type, document.RootElement.Clone());
    }

    private static SqlHarnessParameter Bound(string name, System.Data.SqlDbType type, object value) =>
        new("@" + name, type, value, null);

    private static IReadOnlyList<ParseError> ParseErrors(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        _ = parser.Parse(reader, out var errors);
        return errors.ToArray();
    }
}
