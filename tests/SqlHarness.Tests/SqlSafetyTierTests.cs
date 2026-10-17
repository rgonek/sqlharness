using SqlHarness.Core;

namespace SqlHarness.Tests;

public class SqlSafetyTierTests
{
    private readonly SqlSafetyClassifier _classifier = new();

    private SqlSafetyDecision Mutation(string sql) => _classifier.Classify(sql, SqlUsage.Mutation, "appdb", false);
    private SqlSafetyDecision Query(string sql) => _classifier.Classify(sql, SqlUsage.Query, "appdb", false);

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int); INSERT #t VALUES (1);", SqlRiskTier.Session)]
    [InlineData("UPDATE dbo.Orders SET Total = 0 WHERE Id = 1", SqlRiskTier.Dml)]
    [InlineData("CREATE TABLE #t (Id int); DELETE dbo.Orders WHERE Id = 1;", SqlRiskTier.Dml)]
    public void Mutation_usage_reports_the_highest_tier(string sql, object expected)
    {
        var decision = Mutation(sql);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
        Assert.Equal((SqlRiskTier)expected >= SqlRiskTier.Dml, decision.HasMutation);
    }

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int);", SqlRiskTier.Session)]
    public void Query_usage_reports_tier_without_changing_decisions(string sql, object expected)
    {
        var decision = Query(sql);
        Assert.True(decision.Allowed);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
    }

    [Fact]
    public void Query_usage_still_rejects_unapproved_dml_with_the_same_reason() =>
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, Query("DELETE dbo.Orders WHERE Id = 1").Reason);

    [Theory]
    [InlineData("CREATE TABLE dbo.T (Id int)")]
    [InlineData("ALTER TABLE dbo.T ADD Name nvarchar(50)")]
    [InlineData("DROP TABLE dbo.T")]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    [InlineData("CREATE INDEX IX_T_Id ON dbo.T (Id)")]
    [InlineData("CREATE OR ALTER VIEW dbo.V AS SELECT 1 AS A")]
    [InlineData("CREATE PROCEDURE dbo.P AS SELECT NEWID() AS G")]
    [InlineData("SELECT Id INTO dbo.Copy FROM dbo.T")]
    [InlineData("UPDATE dbo.T SET Id = 1; DROP TABLE dbo.U;")]
    [InlineData("SELECT NEXT VALUE FOR dbo.OrderSequence")]
    public void Schema_statements_are_schema_tier(string sql)
    {
        var decision = Mutation(sql);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Schema, decision.Tier);
    }

    [Fact]
    public void Privilege_statements_report_tier_and_grantees()
    {
        var decision = Mutation("GRANT SELECT ON dbo.T TO [Reader]; ALTER ROLE db_owner ADD MEMBER Reader;");
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Privilege, decision.Tier);
        Assert.Equal(["Reader", "Reader"], decision.Grantees);
    }

    [Theory]
    [InlineData("REVOKE INSERT ON dbo.T FROM app")]
    [InlineData("DENY DELETE ON dbo.T TO app")]
    [InlineData("CREATE USER app WITHOUT LOGIN")]
    [InlineData("GRANT CONTROL TO app")]
    public void Other_privilege_statements_are_privilege_tier(string sql)
    {
        var decision = Mutation(sql);
        Assert.Equal(SqlRiskTier.Privilege, decision.Tier);
        Assert.Equal(["app"], decision.Grantees);
    }

    [Fact]
    public void Nested_privilege_statements_keep_grantees_in_source_order()
    {
        var decision = Mutation(
            "GRANT SELECT ON dbo.T TO first, [PUBLIC]; IF 1 = 1 ALTER ROLE db_owner ADD MEMBER second; ELSE CREATE USER third WITHOUT LOGIN;");

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Privilege, decision.Tier);
        Assert.Equal(["first", "public", "second", "third"], decision.Grantees);
    }

    [Fact]
    public void Query_usage_still_rejects_grant() =>
        Assert.False(Query("GRANT SELECT ON dbo.T TO app").Allowed);

    [Theory]
    [InlineData("DROP TABLE #t")]
    [InlineData("TRUNCATE TABLE #t")]
    public void Temp_ddl_stays_session_tier(string sql) =>
        Assert.Equal(SqlRiskTier.Session, Mutation("CREATE TABLE #t (Id int); " + sql).Tier);

    [Theory]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    [InlineData("DROP TABLE dbo.T")]
    public void Query_usage_still_rejects_schema_statements(string sql) =>
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, Query(sql).Reason);

    [Fact]
    public void Query_usage_still_rejects_persistent_select_into() =>
        Assert.Equal(SqlSafetyReason.SelectIntoNotAllowed, Query("SELECT Id INTO dbo.Copy FROM dbo.T").Reason);

    [Fact]
    public void Query_usage_still_rejects_sequence_advances() =>
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, Query("SELECT NEXT VALUE FOR dbo.OrderSequence").Reason);

    [Theory]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY SIMPLE")]
    [InlineData("CREATE LOGIN x WITH PASSWORD = 'p@ssw0rd!'")]
    [InlineData("BACKUP DATABASE appdb TO DISK = 'c:\\x.bak'")]
    [InlineData("DBCC FREEPROCCACHE")]
    [InlineData("CREATE TRIGGER t ON ALL SERVER FOR CREATE_DATABASE AS SELECT 1 AS A")]
    [InlineData("ALTER TRIGGER t ON ALL SERVER FOR CREATE_DATABASE AS SELECT 1 AS A")]
    [InlineData("CREATE OR ALTER TRIGGER t ON ALL SERVER FOR CREATE_DATABASE AS SELECT 1 AS A")]
    [InlineData("DROP TRIGGER audit_trigger ON ALL SERVER")]
    [InlineData("IF 1 = 1 DROP TRIGGER audit_trigger ON ALL SERVER")]
    [InlineData("BEGIN DROP TRIGGER audit_trigger ON ALL SERVER; END")]
    [InlineData("DROP TABLE otherdb.dbo.T")]
    [InlineData("USE otherdb")]
    public void Server_scope_and_cross_database_stay_rejected(string sql) =>
        Assert.False(Mutation(sql).Allowed);

    [Theory]
    [InlineData("DROP TRIGGER database_trigger ON DATABASE")]
    [InlineData("DROP TRIGGER table_trigger")]
    public void Database_and_table_trigger_drops_remain_schema_tier(string sql)
    {
        var decision = Mutation(sql);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Schema, decision.Tier);
    }

    [Fact]
    public void Routine_body_is_not_classified_as_a_separate_mutation() =>
        Assert.Equal(SqlRiskTier.Schema, Mutation("CREATE PROCEDURE dbo.P AS UPDATE dbo.T SET Id = 1").Tier);

    [Fact]
    public void Routine_body_table_variables_are_not_classified_as_separate_statements()
    {
        var decision = Mutation("CREATE PROCEDURE dbo.P AS SELECT * FROM @t");
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Schema, decision.Tier);
    }

    [Fact]
    public void Nested_schema_statement_is_combined_under_mutation() =>
        Assert.Equal(SqlRiskTier.Schema, Mutation("IF 1 = 1 DROP TABLE dbo.T").Tier);

    [Fact]
    public void Cross_database_reference_inside_routine_body_stays_rejected() =>
        Assert.Equal(
            SqlSafetyReason.CrossDatabaseReference,
            Mutation("CREATE PROCEDURE dbo.P AS SELECT * FROM otherdb.dbo.T").Reason);

    [Theory]
    [InlineData("EXEC dbo.RebuildTotals @From = 1")]
    [InlineData("EXEC sp_executesql N'UPDATE dbo.T SET Id = 1'")]
    [InlineData("DECLARE @s nvarchar(100) = N'SELECT 1'; EXEC(@s);")]
    [InlineData("IF 1 = 1 EXEC dbo.P")]
    [InlineData("BEGIN EXEC dbo.P; END")]
    public void Procedures_and_dynamic_sql_are_opaque(string sql)
    {
        var decision = Mutation(sql);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Opaque, decision.Tier);
    }

    [Theory]
    [InlineData("EXEC sp_configure 'show advanced options', 1")]
    [InlineData("EXEC master.dbo.xp_cmdshell 'dir'")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("EXEC otherdb.dbo.P")]
    [InlineData("EXEC server.otherdb.dbo.P")]
    [InlineData("EXEC ('SELECT 1') AT LinkedSrv")]
    [InlineData("DECLARE @p sysname = N'dbo.P'; EXEC @p;")]
    [InlineData("INSERT dbo.T EXEC dbo.P")]
    public void Dangerous_execs_stay_rejected(string sql) =>
        Assert.False(Mutation(sql).Allowed);

    [Fact]
    public void Query_usage_still_rejects_exec() =>
        Assert.False(Query("EXEC dbo.P").Allowed);

    [Theory]
    [InlineData("SELECT 1 AS A", SqlRiskTier.ReadOnly)]
    [InlineData("CREATE TABLE #t (Id int);", SqlRiskTier.Session)]
    public void Compare_setup_reports_tier(string sql, object expected)
    {
        var decision = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false);
        Assert.True(decision.Allowed);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
    }

    [Theory]
    [InlineData("SET NOCOUNT ON")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SNAPSHOT")]
    [InlineData("BEGIN TRAN; COMMIT")]
    [InlineData("DECLARE @a int; DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT a FROM dbo.T; OPEN c; FETCH NEXT FROM c INTO @a; CLOSE c; DEALLOCATE c")]
    public void Read_only_session_admissions_report_session_tier_and_flags(string sql)
    {
        AssertReadOnlySessionTier(Query(sql));
        AssertReadOnlySessionTier(_classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false));
    }

    [Theory]
    [InlineData("PRINT 'message'")]
    [InlineData("RETURN")]
    [InlineData("WHILE 1 = 0 BEGIN SELECT 1; END")]
    [InlineData("BEGIN TRY SELECT 1; END TRY BEGIN CATCH SELECT 2; END CATCH")]
    public void Read_only_output_and_control_flow_report_read_only_tier_and_flags(string sql)
    {
        AssertReadOnlyTier(Query(sql));
        AssertReadOnlyTier(_classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false));
    }

    [Theory]
    [InlineData("DECLARE @t TABLE (a int); WITH x AS (SELECT a FROM @t) UPDATE x SET a = 2")]
    [InlineData("CREATE TABLE #t (a int); WITH x AS (SELECT a FROM #t) UPDATE x SET a = 2")]
    public void Read_only_local_cte_dml_reports_session_tier_without_persistent_mutation(string sql)
    {
        AssertReadOnlySessionTier(Query(sql));
        AssertReadOnlySessionTier(_classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false));
    }

    private static void AssertReadOnlySessionTier(SqlSafetyDecision decision)
    {
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Session, decision.Tier);
        Assert.False(decision.HasMutation);
        Assert.True(decision.HasSessionLocalWork);
    }

    private static void AssertReadOnlyTier(SqlSafetyDecision decision)
    {
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.ReadOnly, decision.Tier);
        Assert.False(decision.HasMutation);
        Assert.False(decision.HasSessionLocalWork);
    }

    [Theory]
    [InlineData(SqlRiskTier.ReadOnly, "read-only")]
    [InlineData(SqlRiskTier.Session, "session")]
    [InlineData(SqlRiskTier.Dml, "dml")]
    [InlineData(SqlRiskTier.Schema, "schema")]
    [InlineData(SqlRiskTier.Privilege, "privilege")]
    [InlineData(SqlRiskTier.Opaque, "opaque")]
    public void Labels_are_stable(object tier, string label) =>
        Assert.Equal(label, SqlRiskTiers.Label((SqlRiskTier)tier));
}
