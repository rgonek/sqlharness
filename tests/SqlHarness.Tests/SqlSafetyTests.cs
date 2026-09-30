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
    public void Classifier_denies_OPENDATASOURCE(bool compareSetup)
    {
        const string sql = "SELECT * FROM OPENDATASOURCE('MSOLEDBSQL', 'Server=other;Trusted_Connection=yes;').db.dbo.Clients";
        var usage = compareSetup ? SqlUsage.CompareSetup : SqlUsage.Query;

        Assert.False(_classifier.Classify(sql, usage, "db", false, null).Allowed);
    }

    [Fact]
    public void Query_denies_stateful_OPENXML_table_source()
    {
        var decision = ClassifyQuery(
            "SELECT Id FROM OPENXML(@handle, '/root/item') WITH (Id int '@id')");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

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
    public void Compare_setup_allows_select_into_local_temp_only()
    {
        Assert.True(_classifier.Classify("SELECT Id INTO #ids FROM dbo.Clients", SqlUsage.CompareSetup,
            "db", false, null).Allowed);
        Assert.False(_classifier.Classify("SELECT Id INTO dbo.Ids FROM dbo.Clients", SqlUsage.CompareSetup,
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
    public void Compare_setup_allows_session_only_work(string sql) =>
        Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);

    [Theory]
    [InlineData("CREATE TABLE dbo.Ids(Id int)")]
    [InlineData("INSERT dbo.Ids(Id) VALUES (1)")]
    [InlineData("UPDATE dbo.Clients SET Active = 0")]
    [InlineData("DELETE dbo.Clients WHERE Id = 1")]
    [InlineData("CREATE INDEX IX_Clients ON dbo.Clients(Id)")]
    [InlineData("DROP TABLE dbo.Clients")]
    [InlineData("SELECT Id INTO ##ids FROM dbo.Clients")]
    [InlineData("INSERT ##ids(Id) VALUES (1)")]
    public void Compare_setup_denies_writes_outside_local_temp_objects(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);

    [Fact]
    public void Compare_setup_denies_INSERT_EXEC_even_when_destination_is_local_temp() =>
        Assert.False(_classifier.Classify("INSERT #t EXEC dbo.DoWork", SqlUsage.CompareSetup, "db", false, null).Allowed);

    [Theory]
    [InlineData("UPDATE #t SET Id = 2 OUTPUT inserted.Id INTO dbo.PersistentAudit")]
    [InlineData("INSERT #t(Id) OUTPUT inserted.Id INTO dbo.PersistentAudit VALUES (1)")]
    [InlineData("DELETE #t OUTPUT deleted.Id INTO dbo.PersistentAudit WHERE Id = 1")]
    public void Compare_setup_denies_persistent_OUTPUT_INTO_destinations(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);

    [Fact]
    public void TempInsertWithPersistentOutputRequiresApproval()
    {
        const string sql =
            "CREATE TABLE #t(id int); INSERT INTO #t OUTPUT inserted.id INTO dbo.audit_sink VALUES (1);";

        var denied = ClassifyQuery(sql);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);
        Assert.False(denied.HasMutation);

        var allowed = _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: true, confirmDatabase: "db");
        Assert.True(allowed.Allowed, allowed.RejectionDescription);
        Assert.True(allowed.HasMutation);
        Assert.True(allowed.HasSessionLocalWork);

        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("UPDATE #t SET id = 42 FROM dbo.items AS #t")]
    [InlineData("DELETE #t FROM dbo.items AS #t")]
    [InlineData("UPDATE [#t] SET id = 42 FROM dbo.items AS [#t]")]
    [InlineData("DELETE [#t] FROM dbo.items AS [#t]")]
    public void HashAliasOfPersistentTableRequiresApproval(string sql)
    {
        var denied = ClassifyQuery(sql);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);
        Assert.False(denied.HasSessionLocalWork);

        var allowed = _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: true, confirmDatabase: "db");
        Assert.True(allowed.Allowed, allowed.RejectionDescription);
        Assert.True(allowed.HasMutation);
        Assert.False(allowed.HasSessionLocalWork);

        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("UPDATE t SET id = 1 FROM #real AS t")]
    [InlineData("DELETE t FROM #real AS t")]
    [InlineData("UPDATE #alias SET id = 1 FROM #real AS #alias")]
    [InlineData("DELETE #alias FROM #real AS #alias")]
    [InlineData("UPDATE [#alias] SET id = 1 FROM #real AS [#alias]")]
    public void AliasOfActualTempRemainsLocal(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
        Assert.True(decision.HasSessionLocalWork);
        Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Fact]
    public void Ambiguous_hash_alias_target_is_rejected()
    {
        const string sql = "UPDATE #t SET id = 42 FROM dbo.items AS #t, dbo.other AS #t";

        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    [InlineData("UPDATE #t SET id = 1 FROM OPENJSON(@j) WITH (id int) AS #t")]
    [InlineData("DELETE #t FROM OPENJSON(@j) WITH (id int) AS #t")]
    [InlineData("UPDATE #t SET id = 1 FROM (SELECT 1 AS id) AS #t")]
    [InlineData("UPDATE #t SET id = 1 FROM dbo.SomeTvf() AS #t")]
    public void Unprovable_non_named_FROM_alias_is_rejected(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.False(decision.HasSessionLocalWork);
    }

    [Theory]
    [InlineData("INSERT #t(Id) OUTPUT inserted.Id INTO dbo.PersistentAudit VALUES (1)")]
    [InlineData("UPDATE #t SET Id = 2 OUTPUT inserted.Id INTO dbo.PersistentAudit")]
    [InlineData("DELETE #t OUTPUT deleted.Id INTO dbo.PersistentAudit WHERE Id = 1")]
    [InlineData("MERGE #t AS target USING (SELECT 1 AS Id) AS source ON target.Id = source.Id WHEN MATCHED THEN UPDATE SET target.Id = source.Id OUTPUT inserted.Id INTO dbo.PersistentAudit;")]
    public void Query_persistent_OUTPUT_INTO_requires_approval_for_all_DML_shapes(string sql)
    {
        var denied = ClassifyQuery(sql);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);

        var allowed = _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: true, confirmDatabase: "db");
        Assert.True(allowed.Allowed, allowed.RejectionDescription);
        Assert.True(allowed.HasMutation);

        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Fact]
    public void Query_local_OUTPUT_INTO_temp_remains_session_local()
    {
        const string sql = "INSERT #t(Id) OUTPUT inserted.Id INTO #audit VALUES (1)";

        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
        Assert.True(decision.HasSessionLocalWork);
    }

    [Fact]
    public void Classifier_still_denies_cross_database_OUTPUT_even_with_mutation_approval()
    {
        const string sql = "INSERT #t(Id) OUTPUT inserted.Id INTO otherdb.dbo.PersistentAudit VALUES (1)";

        var decision = _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: true, confirmDatabase: "db");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.CrossDatabaseReference, decision.Reason);
    }

    [Theory]
    [InlineData("""
        INSERT INTO #t(id)
        SELECT id FROM (
          UPDATE dbo.items SET flag = 1
          OUTPUT inserted.id
        ) AS src
        """)]
    [InlineData("""
        INSERT INTO #t(id)
        SELECT id FROM (
          DELETE FROM dbo.items
          OUTPUT deleted.id
        ) AS src
        """)]
    [InlineData("""
        INSERT INTO #t(id)
        SELECT id FROM (
          INSERT INTO dbo.items(id)
          OUTPUT inserted.id
          VALUES (1)
        ) AS src
        """)]
    [InlineData("""
        INSERT INTO #t(id)
        SELECT id FROM (
          MERGE dbo.items AS target
          USING (SELECT 1 AS id) AS source
          ON target.id = source.id
          WHEN MATCHED THEN UPDATE SET target.flag = 1
          OUTPUT inserted.id
        ) AS src
        """)]
    [InlineData("""
        INSERT INTO #t(id)
        SELECT id FROM (
          UPDATE #items SET flag = 1
          OUTPUT inserted.id INTO dbo.audit
        ) AS src
        """)]
    public void Nested_dml_in_insert_source_is_a_persistent_write(string sql)
    {
        var denied = ClassifyQuery(sql);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);
        Assert.False(denied.HasMutation);

        var allowed = _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: true, confirmDatabase: "db");
        Assert.True(allowed.Allowed, allowed.RejectionDescription);
        Assert.True(allowed.HasMutation);
        Assert.True(allowed.HasSessionLocalWork);

        var setup = _classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null);
        Assert.False(setup.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, setup.Reason);
    }

    [Theory]
    [InlineData("""
        INSERT INTO #t(id)
        SELECT id FROM (
          UPDATE #items SET flag = 1
          OUTPUT inserted.id
        ) AS src
        """)]
    [InlineData("""
        INSERT INTO #t(id)
        SELECT id FROM (
          DELETE FROM #items
          OUTPUT deleted.id INTO #audit
        ) AS src
        """)]
    public void Nested_temp_dml_in_insert_source_stays_session_local(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
        Assert.True(decision.HasSessionLocalWork);
        Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Fact]
    public void Nested_three_part_update_in_insert_source_stays_cross_database()
    {
        const string sql = """
            INSERT INTO #t(id)
            SELECT id FROM (
              UPDATE otherdb.dbo.items SET flag = 1
              OUTPUT inserted.id
            ) AS src
            """;

        var decision = _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: true, confirmDatabase: "db");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.CrossDatabaseReference, decision.Reason);
        Assert.Equal(
            SqlSafetyReason.CrossDatabaseReference,
            _classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Reason);
    }

    [Theory]
    [InlineData("""
        UPDATE #t SET id = 42
        FROM dbo.items AS #t
        PIVOT (COUNT(id) FOR id IN ([1])) AS p
        """)]
    [InlineData("""
        UPDATE #t SET id = 42
        FROM dbo.items AS #t
        UNPIVOT (val FOR col IN (id)) AS p
        """)]
    [InlineData("""
        DELETE #t
        FROM dbo.items AS #t
        PIVOT (COUNT(id) FOR id IN ([1])) AS p
        """)]
    public void Pivot_inner_hash_alias_of_persistent_table_requires_approval(string sql)
    {
        var denied = ClassifyQuery(sql);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);
        Assert.False(denied.HasSessionLocalWork);

        var allowed = _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: true, confirmDatabase: "db");
        Assert.True(allowed.Allowed, allowed.RejectionDescription);
        Assert.True(allowed.HasMutation);
        Assert.False(allowed.HasSessionLocalWork);

        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("""
        UPDATE #t SET id = 42
        FROM (SELECT 1 AS id) AS #t
        PIVOT (COUNT(id) FOR id IN ([1])) AS p
        """)]
    [InlineData("""
        UPDATE #t SET id = 42
        FROM (SELECT 1 AS id) AS #t
        UNPIVOT (val FOR col IN (id)) AS p
        """)]
    [InlineData("""
        UPDATE #t SET id = 42
        FROM dbo.items AS src
        PIVOT (COUNT(id) FOR id IN ([1])) AS #t
        """)]
    public void Unprovable_pivot_alias_does_not_fall_through_to_the_target_token(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.False(decision.HasSessionLocalWork);
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Fact]
    public void Pivot_inner_alias_of_actual_temp_remains_local()
    {
        const string sql = """
            UPDATE #t SET id = 42
            FROM #real AS #t
            PIVOT (COUNT(id) FOR id IN ([1])) AS p
            """;

        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
        Assert.True(decision.HasSessionLocalWork);
        Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("EXEC dbo.DoWork")]
    [InlineData("USE otherdb")]
    [InlineData("SELECT * FROM otherdb.dbo.Clients")]
    [InlineData("BEGIN TRANSACTION")]
    public void Compare_setup_keeps_always_forbidden_constructs_denied(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);

    [Fact]
    public void Multiple_statement_batch_is_denied_if_any_statement_is_unsafe() =>
        Assert.False(ClassifyQuery("SELECT 1; EXEC dbo.DoWork").Allowed);

    [Fact]
    public void Rejection_names_unsupported_statement_without_echoing_SQL()
    {
        const string secret = "SECRET_PROC";
        var decision = ClassifyQuery($"EXEC dbo.{secret}");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.Contains(nameof(SqlSafetyReason.UnsupportedStatement), decision.RejectionDescription);
        Assert.DoesNotContain(secret, decision.RejectionDescription);
    }

    [Theory]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY Id) FROM dbo.Clients")]
    [InlineData("SELECT SUM(Amount) OVER (PARTITION BY ClientId ORDER BY Id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) FROM dbo.Orders")]
    public void Query_allows_safe_window_syntax(string sql) =>
        Assert.True(ClassifyQuery(sql).Allowed);

    [Fact]
    public void Query_allows_complete_read_only_SELECT_syntax_without_fragment_registration()
    {
        const string sql = """
            WITH RecentOrders AS
            (
                SELECT o.ClientId, RIGHT(o.Reference, 4) AS ReferenceSuffix
                FROM dbo.Orders AS o WITH (INDEX(IX_Orders_ClientId), FORCESEEK)
            )
            SELECT ClientId, ReferenceSuffix
            FROM RecentOrders
            OPTION (RECOMPILE, MAXDOP 1);
            """;

        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
    }

    [Theory]
    [InlineData("SELECT DISTINCT TOP (10) PERCENT WITH TIES Id FROM dbo.Clients ORDER BY Id")]
    [InlineData("SELECT IIF(Active = 1, 'active', 'inactive') FROM dbo.Clients")]
    [InlineData("SELECT LAG(Amount, 1, 0) IGNORE NULLS OVER (PARTITION BY ClientId ORDER BY Id) FROM dbo.Orders")]
    [InlineData("SELECT p.Id FROM dbo.Parent AS p CROSS APPLY (SELECT TOP (1) c.Id FROM dbo.Child AS c WHERE c.ParentId = p.Id ORDER BY c.Id DESC) AS latest")]
    [InlineData("SELECT Id FROM dbo.Clients TABLESAMPLE (10 PERCENT) OPTION (OPTIMIZE FOR UNKNOWN)")]
    public void Query_allows_representative_parsed_SELECT_syntax(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
    }

    [Theory]
    [InlineData("CREATE TABLE #Req(Id int NULL)")]
    [InlineData("CREATE TABLE #Req(Id int NOT NULL PRIMARY KEY)")]
    [InlineData("CREATE TABLE #Req(Id int NOT NULL, Code int, CONSTRAINT UQ_Req UNIQUE(Code))")]
    [InlineData("CREATE TABLE #Req(Id int NOT NULL); CREATE UNIQUE INDEX IX_Req ON #Req(Id)")]
    public void Setup_allows_constraints_and_indexes_on_local_temp_tables(string sql) =>
        Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false).Allowed);

    [Theory]
    [InlineData("CREATE TABLE ##Req(Id int NOT NULL PRIMARY KEY)")]
    [InlineData("CREATE TABLE dbo.Req(Id int NOT NULL PRIMARY KEY)")]
    [InlineData("CREATE UNIQUE INDEX IX_Req ON dbo.Req(Id)")]
    public void Setup_denies_equivalent_persistent_or_global_temp_work(string sql) =>
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false).Allowed);

    [Theory]
    [InlineData("DECLARE @id int = 1; SELECT @id")]
    [InlineData("DECLARE @id int; SELECT @id")]
    [InlineData("DECLARE @a int = 1, @b nvarchar(10) = N'x'; SELECT @a, @b")]
    [InlineData("DECLARE @top int = (SELECT MAX(Id) FROM dbo.Clients); SELECT TOP (@top) Id FROM dbo.Clients")]
    public void Query_allows_scalar_declare_with_analyzed_initializer(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void Query_denies_declare_table_variable()
    {
        var decision = ClassifyQuery("DECLARE @t TABLE (Id int); SELECT Id FROM @t");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void Query_denies_declare_with_stateful_initializer()
    {
        var decision = ClassifyQuery("DECLARE @id int = NEXT VALUE FOR dbo.Seq; SELECT @id");

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    [InlineData("TRUNCATE TABLE #ids")]
    [InlineData("CREATE TABLE #ids (Id int); TRUNCATE TABLE #ids; SELECT Id FROM #ids")]
    public void Query_and_setup_allow_truncate_of_unambiguous_local_temp(string sql)
    {
        var query = ClassifyQuery(sql);

        Assert.True(query.Allowed, query.RejectionDescription);
        Assert.False(query.HasMutation);
        Assert.True(query.HasSessionLocalWork);
        Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("TRUNCATE TABLE ##ids")]
    [InlineData("TRUNCATE TABLE dbo.Clients")]
    [InlineData("TRUNCATE TABLE otherdb.dbo.Clients")]
    public void Truncate_outside_unambiguous_local_temp_stays_denied(string sql)
    {
        Assert.False(_classifier.Classify(sql, SqlUsage.Query, "db", true, "db").Allowed);
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("ALTER TABLE #ids ADD Marker int NULL")]
    [InlineData("ALTER TABLE #ids DROP COLUMN Marker")]
    [InlineData("ALTER TABLE #ids ADD CONSTRAINT CK_Marker CHECK (Marker > 0)")]
    [InlineData("ALTER TABLE #ids DROP CONSTRAINT CK_Marker")]
    public void Query_and_setup_allow_supported_alter_on_local_temp(string sql)
    {
        var query = ClassifyQuery(sql);

        Assert.True(query.Allowed, query.RejectionDescription);
        Assert.False(query.HasMutation);
        Assert.True(query.HasSessionLocalWork);
        Assert.True(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("ALTER TABLE #ids ALTER COLUMN Marker bigint NOT NULL")]
    [InlineData("ALTER TABLE #ids ADD CONSTRAINT FK_Other FOREIGN KEY (Id) REFERENCES dbo.Clients(Id)")]
    [InlineData("ALTER TABLE dbo.Clients ADD Marker int NULL")]
    [InlineData("ALTER TABLE ##ids ADD Marker int NULL")]
    public void Alter_outside_add_drop_column_or_local_constraint_stays_denied(string sql)
    {
        Assert.False(_classifier.Classify(sql, SqlUsage.Query, "db", true, "db").Allowed);
        Assert.False(_classifier.Classify(sql, SqlUsage.CompareSetup, "db", false, null).Allowed);
    }

    [Theory]
    [InlineData("DECLARE @n int; SET @n = 5; SELECT @n")]
    [InlineData("DECLARE @label nvarchar(20); SET @label = N'x'; SELECT @label")]
    [InlineData("DECLARE @a int = 1; DECLARE @b int; SET @b = @a + 2; SELECT @b")]
    [InlineData("declare @n INT; set @n = 5; select @n")]
    public void T2_Query_allows_SET_local_scalar_assignment(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void T2_Query_allows_SET_assignment_with_subquery_RHS()
    {
        const string sql = "DECLARE @m int; SET @m = (SELECT MAX(Id) FROM dbo.Clients); SELECT @m";

        var decision = ClassifyQuery(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    [InlineData("SET @undeclared = 1; SELECT @undeclared")]
    [InlineData("DECLARE @n int; SET @other = 1; SELECT @n")]
    public void T2_Query_denies_SET_to_undeclared_variable(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void T2_Query_denies_SET_to_table_variable_target()
    {
        const string sql = "DECLARE @t TABLE (Id int); SET @t = 1; SELECT 1";

        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void T2_Query_denies_SET_assignment_with_external_RHS()
    {
        const string sql = "DECLARE @n int; SET @n = (SELECT COUNT(*) FROM OPENROWSET('MSOLEDBSQL', 'Server=other;Trusted_Connection=yes;', 'SELECT 1') AS r); SELECT @n";

        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void T2_Query_denies_SET_assignment_with_stateful_RHS()
    {
        const string sql = "DECLARE @id int; SET @id = NEXT VALUE FOR dbo.Seq; SELECT @id";

        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void T2_Query_denies_SET_assignment_with_cross_db_RHS()
    {
        const string sql = "DECLARE @m int; SET @m = (SELECT MAX(Id) FROM otherdb.dbo.Clients); SELECT @m";

        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.CrossDatabaseReference, decision.Reason);
    }

    [Theory]
    [InlineData("SET NOCOUNT ON")]
    [InlineData("SET ANSI_NULLS ON")]
    [InlineData("SET QUOTED_IDENTIFIER ON")]
    [InlineData("SET XACT_ABORT ON")]
    [InlineData("SET DATEFORMAT dmy")]
    [InlineData("SET DEADLOCK_PRIORITY LOW")]
    [InlineData("SET LOCK_TIMEOUT 1000")]
    [InlineData("SET LANGUAGE British")]
    [InlineData("SET ROWCOUNT 10")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ COMMITTED")]
    [InlineData("SET IDENTITY_INSERT dbo.Clients ON")]
    [InlineData("SET STATISTICS IO ON")]
    [InlineData("SET TEXTSIZE 100")]
    public void T2_Query_denies_SET_session_and_transaction_options(string sql)
    {
        var decision = ClassifyQuery(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    private SqlSafetyDecision ClassifyQuery(string sql) =>
        _classifier.Classify(sql, SqlUsage.Query, "db", allowMutation: false, confirmDatabase: null);
}