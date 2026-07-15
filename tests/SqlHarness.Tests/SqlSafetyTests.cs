using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlSafetyTests
{
    private readonly SqlSafetyClassifier _classifier = new();

    [Theory]
    [InlineData("SELECT Id FROM dbo.Clients")]
    [InlineData("WITH ActiveClients AS (SELECT Id FROM dbo.Clients WHERE Active = 1) SELECT Id FROM ActiveClients")]
    [InlineData("SELECT name FROM sys.tables")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES")]
    public void Query_allows_recognized_reads(string sql) =>
        Assert.True(ClassifyQuery(sql).Allowed);

    [Theory]
    [InlineData("CREATE TABLE #ids(Id int)")]
    [InlineData("INSERT #ids(Id) VALUES (1)")]
    [InlineData("UPDATE #ids SET Id = 2")]
    [InlineData("DELETE #ids WHERE Id = 1")]
    [InlineData("CREATE INDEX IX_ids ON #ids(Id)")]
    [InlineData("DROP TABLE #ids")]
    [InlineData("SELECT Id INTO #ids FROM dbo.Clients")]
    public void Query_allows_session_only_temp_table_work_without_mutation_confirmation(string sql) =>
        Assert.True(ClassifyQuery(sql).Allowed);

    [Theory]
    [InlineData("INSERT dbo.Clients(Id) VALUES (1)")]
    [InlineData("UPDATE dbo.Clients SET Active = 0")]
    [InlineData("DELETE dbo.Clients WHERE Id = 1")]
    [InlineData("MERGE dbo.Clients AS target USING dbo.Source AS source ON target.Id = source.Id WHEN MATCHED THEN UPDATE SET target.Active = source.Active;")]
    public void Query_denies_DML_without_mutation_confirmation(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    [Theory]
    [InlineData("CREATE TABLE dbo.NewTable(Id int)")]
    [InlineData("ALTER TABLE dbo.Clients ADD Marker int NULL")]
    [InlineData("DROP TABLE dbo.Clients")]
    [InlineData("TRUNCATE TABLE dbo.Clients")]
    public void Query_denies_DDL_even_with_mutation_confirmations(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.Query, "db", true, "db").Allowed);

    [Theory]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("COMMIT TRANSACTION")]
    [InlineData("ROLLBACK TRANSACTION")]
    public void Query_denies_transaction_control(string sql) =>
        Assert.False(ClassifyQuery(sql).Allowed);

    [Theory]
    [InlineData("EXEC dbo.DoWork")]
    [InlineData("EXEC sp_executesql N'SELECT 1'")]
    [InlineData("DECLARE @sql nvarchar(100) = N'SELECT 1'; EXEC(@sql)")]
    public void Query_denies_EXEC_and_dynamic_SQL(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.Query, "db", true, "db").Allowed);

    [Fact]
    public void Query_denies_SELECT_INTO() =>
        Assert.False(ClassifyQuery("SELECT Id INTO dbo.Ids FROM dbo.Clients").Allowed);

    [Fact]
    public void Query_denies_USE() =>
        Assert.False(ClassifyQuery("USE otherdb; SELECT 1").Allowed);

    [Fact]
    public void Query_denies_stateful_NEXT_VALUE_FOR_expression() =>
        Assert.False(ClassifyQuery("SELECT NEXT VALUE FOR dbo.Seq").Allowed);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classifier_denies_OPENDATASOURCE(bool perfSetup)
    {
        const string sql = "SELECT * FROM OPENDATASOURCE('MSOLEDBSQL', 'Server=other;Trusted_Connection=yes;').db.dbo.Clients";
        var usage = perfSetup ? SqlUsage.PerfSetup : SqlUsage.Query;

        Assert.False(_classifier.Classify(sql, usage, "db", false, null).Allowed);
    }

    [Fact]
    public void Query_denies_unallowlisted_nested_fragment() =>
        Assert.False(ClassifyQuery("SELECT Id FROM OPENXML(@handle, '/root/item') WITH (Id int '@id')").Allowed);

    [Theory]
    [InlineData("SELECT * FROM otherdb.dbo.Clients")]
    [InlineData("SELECT * FROM server.otherdb.dbo.Clients")]
    [InlineData("UPDATE otherdb.dbo.Clients SET Active = 0")]
    public void Classifier_denies_three_and_four_part_names(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "db", true, "db");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.CrossDatabaseReference, decision.Reason);
    }

    [Theory]
    [InlineData("SELEC FROM")]
    [InlineData("SELECT * FROM [unterminated")]
    public void Classifier_fails_closed_on_parse_errors(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.ParseError, decision.Reason);
    }

    [Theory]
    [InlineData("UPDATE dbo.Clients SET Active = 0")]
    [InlineData("INSERT dbo.Clients(Id) VALUES (1)")]
    [InlineData("DELETE dbo.Clients WHERE Id = 1")]
    [InlineData("MERGE dbo.Clients AS target USING dbo.Source AS source ON target.Id = source.Id WHEN MATCHED THEN UPDATE SET target.Active = source.Active;")]
    public void Direct_mutation_allows_recognized_DML_with_both_exact_confirmations(string sql) =>
        Assert.True(_classifier.Classify(sql, SqlUsage.Query, "db", true, "db").Allowed);

    [Fact]
    public void Mutation_requires_allow_mutation_confirmation()
    {
        var decision = _classifier.Classify("UPDATE dbo.Clients SET Active = 0", SqlUsage.Query,
            "db", allowMutation: false, confirmDatabase: "db");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    [Fact]
    public void Mutation_requires_database_confirmation()
    {
        var decision = _classifier.Classify("UPDATE dbo.Clients SET Active = 0", SqlUsage.Query,
            "db", allowMutation: true, confirmDatabase: null);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.DatabaseConfirmationRequired, decision.Reason);
    }

    [Fact]
    public void Mutation_requires_both_exact_confirmations()
    {
        var decision = _classifier.Classify("UPDATE dbo.Clients SET Active = 0", SqlUsage.Query,
            "contoso-uat", allowMutation: true, confirmDatabase: "wrong");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.DatabaseConfirmationMismatch, decision.Reason);
    }

    [Fact]
    public void Mutation_database_confirmation_is_ordinal_and_case_sensitive()
    {
        var decision = _classifier.Classify("UPDATE dbo.Clients SET Active = 0", SqlUsage.Query,
            "contoso-uat", allowMutation: true, confirmDatabase: "CONTOSO-UAT");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.DatabaseConfirmationMismatch, decision.Reason);
    }

    [Fact]
    public void Perf_setup_allows_select_into_local_temp_only()
    {
        Assert.True(_classifier.Classify("SELECT Id INTO #ids FROM dbo.Clients", SqlUsage.PerfSetup,
            "db", false, null).Allowed);
        Assert.False(_classifier.Classify("SELECT Id INTO dbo.Ids FROM dbo.Clients", SqlUsage.PerfSetup,
            "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("CREATE TABLE #ids(Id int)")]
    [InlineData("INSERT #ids(Id) VALUES (1)")]
    [InlineData("UPDATE #ids SET Id = 2")]
    [InlineData("DELETE #ids WHERE Id = 1")]
    [InlineData("CREATE INDEX IX_ids ON #ids(Id)")]
    [InlineData("DROP TABLE #ids")]
    [InlineData("DECLARE @id int = 1")]
    [InlineData("SELECT c.Id INTO #ids FROM dbo.Clients c; CREATE INDEX IX_ids ON #ids(Id); UPDATE #ids SET Id = Id + 1; SELECT Id FROM #ids")]
    public void Perf_setup_allows_session_only_work(string sql) =>
        Assert.True(_classifier.Classify(sql, SqlUsage.PerfSetup, "db", false, null).Allowed);

    [Theory]
    [InlineData("CREATE TABLE dbo.Ids(Id int)")]
    [InlineData("INSERT dbo.Ids(Id) VALUES (1)")]
    [InlineData("UPDATE dbo.Clients SET Active = 0")]
    [InlineData("DELETE dbo.Clients WHERE Id = 1")]
    [InlineData("CREATE INDEX IX_Clients ON dbo.Clients(Id)")]
    [InlineData("DROP TABLE dbo.Clients")]
    [InlineData("SELECT Id INTO ##ids FROM dbo.Clients")]
    [InlineData("INSERT ##ids(Id) VALUES (1)")]
    public void Perf_setup_denies_writes_outside_local_temp_objects(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.PerfSetup, "db", false, null).Allowed);

    [Fact]
    public void Perf_setup_denies_INSERT_EXEC_even_when_destination_is_local_temp() =>
        Assert.False(_classifier.Classify("INSERT #t EXEC dbo.DoWork", SqlUsage.PerfSetup, "db", false, null).Allowed);

    [Theory]
    [InlineData("UPDATE #t SET Id = 2 OUTPUT inserted.Id INTO dbo.PersistentAudit")]
    [InlineData("INSERT #t(Id) OUTPUT inserted.Id INTO dbo.PersistentAudit VALUES (1)")]
    [InlineData("DELETE #t OUTPUT deleted.Id INTO dbo.PersistentAudit WHERE Id = 1")]
    public void Perf_setup_denies_persistent_OUTPUT_INTO_destinations(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.PerfSetup, "db", false, null).Allowed);

    [Theory]
    [InlineData("EXEC dbo.DoWork")]
    [InlineData("USE otherdb")]
    [InlineData("SELECT * FROM otherdb.dbo.Clients")]
    [InlineData("BEGIN TRANSACTION")]
    public void Perf_setup_keeps_always_forbidden_constructs_denied(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.PerfSetup, "db", false, null).Allowed);

    [Fact]
    public void Multiple_statement_batch_is_denied_if_any_statement_is_unsafe() =>
        Assert.False(ClassifyQuery("SELECT 1; EXEC dbo.DoWork").Allowed);

    private SqlSafetyDecision ClassifyQuery(string sql) =>
        _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: false, confirmDatabase: null);
}
