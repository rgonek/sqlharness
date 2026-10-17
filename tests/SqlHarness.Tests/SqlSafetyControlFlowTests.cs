using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class SqlSafetyControlFlowTests
{
    private readonly SqlSafetyClassifier _classifier = new();

    private SqlSafetyDecision Query(string sql) =>
        _classifier.Classify(sql, SqlUsage.Query, "appdb", allowMutation: false);

    [Theory]
    [InlineData("SET NOCOUNT ON")]
    [InlineData("SET XACT_ABORT ON")]
    [InlineData("SET ARITHABORT ON")]
    [InlineData("SET ANSI_NULLS ON")]
    [InlineData("SET ANSI_WARNINGS ON")]
    [InlineData("SET ANSI_PADDING ON")]
    [InlineData("SET QUOTED_IDENTIFIER ON")]
    [InlineData("SET CONCAT_NULL_YIELDS_NULL ON")]
    [InlineData("SET NUMERIC_ROUNDABORT OFF")]
    [InlineData("SET DATEFIRST 1")]
    [InlineData("SET DATEFORMAT ymd")]
    [InlineData("SET LOCK_TIMEOUT 5000")]
    [InlineData("SET DEADLOCK_PRIORITY LOW")]
    [InlineData("SET DEADLOCK_PRIORITY NORMAL")]
    [InlineData("SET DEADLOCK_PRIORITY 0")]
    [InlineData("SET DEADLOCK_PRIORITY -5")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SNAPSHOT")]
    [InlineData("DECLARE @i int = 0; WHILE @i < 3 BEGIN SET @i += 1; IF @i = 2 CONTINUE; IF @i = 3 BREAK; END")]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT ERROR_NUMBER() END CATCH")]
    [InlineData("IF 1=0 RETURN; SELECT 1")]
    [InlineData("RETURN")]
    [InlineData("THROW 50000, 'x', 1")]
    [InlineData("RAISERROR('x', 10, 1) WITH NOWAIT")]
    [InlineData("PRINT 'x'")]
    [InlineData("CREATE TABLE #t (a int); BEGIN TRAN; INSERT #t VALUES (1); COMMIT")]
    [InlineData("BEGIN TRAN; SAVE TRAN s; ROLLBACK TRAN s; ROLLBACK TRAN")]
    [InlineData("DECLARE @a int; DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT a FROM dbo.T; OPEN c; FETCH NEXT FROM c INTO @a; CLOSE c; DEALLOCATE c")]
    [InlineData("DECLARE @t TABLE (a int); WITH x AS (SELECT a FROM @t) UPDATE x SET a = 2")]
    [InlineData("CREATE TABLE #t (a int); WITH x AS (SELECT a FROM #t) UPDATE x SET a = 2")]
    public void Read_only_control_flow_and_session_commands_are_admitted(string sql)
    {
        var decision = Query(sql);

        Assert.True(decision.Allowed, decision.RejectionDescription);
    }

    [Theory]
    [InlineData("SET STATISTICS IO ON")]
    [InlineData("SET STATISTICS TIME ON")]
    [InlineData("SET SHOWPLAN_XML ON")]
    [InlineData("SET SHOWPLAN_TEXT ON")]
    [InlineData("SET NOEXEC ON")]
    [InlineData("SET PARSEONLY ON")]
    [InlineData("SET FMTONLY ON")]
    [InlineData("SET ROWCOUNT 10")]
    [InlineData("SET IDENTITY_INSERT #t ON")]
    [InlineData("SET CONTEXT_INFO 0x01")]
    [InlineData("SET LANGUAGE us_english")]
    [InlineData("SET FORCEPLAN ON")]
    [InlineData("SET IMPLICIT_TRANSACTIONS ON")]
    [InlineData("SET REMOTE_PROC_TRANSACTIONS ON")]
    [InlineData("SET OFFSETS SELECT 1")]
    [InlineData("SET DEADLOCK_PRIORITY HIGH")]
    [InlineData("SET DEADLOCK_PRIORITY 5")]
    [InlineData("SET DEADLOCK_PRIORITY @v")]
    [InlineData("WAITFOR DELAY '00:00:01'")]
    [InlineData("BREAK")]
    [InlineData("CONTINUE")]
    [InlineData("GOTO x; x: SELECT 1")]
    [InlineData("RAISERROR('x', 10, 1) WITH LOG")]
    [InlineData("BEGIN DISTRIBUTED TRAN")]
    [InlineData("BEGIN TRAN t WITH MARK")]
    [InlineData("DECLARE c CURSOR SCROLL_LOCKS FOR SELECT a FROM dbo.T")]
    [InlineData("DECLARE c CURSOR FOR SELECT a FROM dbo.T FOR UPDATE")]
    [InlineData("DECLARE c CURSOR GLOBAL FOR SELECT a FROM dbo.T")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT 1")]
    [InlineData("EXEC dbo.P")]
    [InlineData("EXEC sp_executesql N'SELECT 1'")]
    [InlineData("RETURN 1")]
    [InlineData("DECLARE @t TABLE (a int); WITH x AS (SELECT a FROM @t) UPDATE dbo.x SET a = 2")]
    [InlineData("DECLARE @t TABLE (a int); WITH x AS (SELECT a FROM @t) UPDATE appdb.dbo.x SET a = 2")]
    [InlineData("DECLARE @t TABLE (a int); WITH y AS (SELECT a FROM @t), x AS (SELECT a FROM dbo.y) UPDATE x SET a = 2")]
    public void Read_only_execution_rejects_unsafe_session_and_control_commands(string sql)
    {
        var decision = Query(sql);
        var setupDecision = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", allowMutation: false);

        Assert.False(decision.Allowed);
        Assert.False(setupDecision.Allowed);
    }

    [Theory]
    [InlineData("SET NOCOUNT ON")]
    [InlineData("SET XACT_ABORT ON")]
    [InlineData("SET ARITHABORT ON")]
    [InlineData("SET ANSI_NULLS ON")]
    [InlineData("SET ANSI_WARNINGS ON")]
    [InlineData("SET ANSI_PADDING ON")]
    [InlineData("SET QUOTED_IDENTIFIER ON")]
    [InlineData("SET CONCAT_NULL_YIELDS_NULL ON")]
    [InlineData("SET NUMERIC_ROUNDABORT OFF")]
    [InlineData("SET DATEFIRST 1")]
    [InlineData("SET DATEFORMAT ymd")]
    [InlineData("SET LOCK_TIMEOUT 5000")]
    [InlineData("SET DEADLOCK_PRIORITY LOW")]
    [InlineData("SET DEADLOCK_PRIORITY NORMAL")]
    [InlineData("SET DEADLOCK_PRIORITY 0")]
    [InlineData("SET DEADLOCK_PRIORITY -5")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SNAPSHOT")]
    [InlineData("DECLARE @i int = 0; WHILE @i < 3 BEGIN SET @i += 1; IF @i = 2 CONTINUE; IF @i = 3 BREAK; END")]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT ERROR_NUMBER() END CATCH")]
    [InlineData("IF 1=0 RETURN; SELECT 1")]
    [InlineData("RETURN")]
    [InlineData("THROW 50000, 'x', 1")]
    [InlineData("RAISERROR('x', 10, 1) WITH NOWAIT")]
    [InlineData("PRINT 'x'")]
    [InlineData("CREATE TABLE #t (a int); BEGIN TRAN; INSERT #t VALUES (1); COMMIT")]
    [InlineData("BEGIN TRAN; COMMIT")]
    [InlineData("BEGIN TRAN; SAVE TRAN s; ROLLBACK TRAN s; ROLLBACK TRAN")]
    [InlineData("DECLARE @a int; DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT a FROM dbo.T; OPEN c; FETCH NEXT FROM c INTO @a; CLOSE c; DEALLOCATE c")]
    [InlineData("DECLARE @t TABLE (a int); WITH x AS (SELECT a FROM @t) UPDATE x SET a = 2")]
    [InlineData("CREATE TABLE #t (a int); WITH x AS (SELECT a FROM #t) UPDATE x SET a = 2")]
    public void Compare_setup_admits_read_only_control_flow_and_session_commands(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", allowMutation: false);

        Assert.True(decision.Allowed, decision.RejectionDescription);
    }

    [Theory]
    [InlineData("WHILE 1=0 UPDATE dbo.T SET a = 1")]
    [InlineData("BEGIN TRY UPDATE dbo.T SET a = 1 END TRY BEGIN CATCH SELECT 1 END CATCH")]
    public void Control_flow_containing_persistent_dml_is_denied(string sql)
    {
        var decision = Query(sql);

        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    [Theory]
    [InlineData("SERIALIZABLE", "isolation level SERIALIZABLE in effect")]
    [InlineData("REPEATABLE READ", "isolation level REPEATABLE READ in effect")]
    public void Isolation_levels_return_the_matching_notice(string level, string notice)
    {
        var decision = Query($"SET TRANSACTION ISOLATION LEVEL {level}");

        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Contains(notice, decision.Notices);
    }

    [Fact]
    public void New_read_only_admissions_do_not_change_mutation_classification()
    {
        var samples = new[]
        {
            "SET NOCOUNT ON",
            "SET XACT_ABORT ON",
            "SET ARITHABORT ON",
            "SET ANSI_NULLS ON",
            "SET ANSI_WARNINGS ON",
            "SET ANSI_PADDING ON",
            "SET QUOTED_IDENTIFIER ON",
            "SET CONCAT_NULL_YIELDS_NULL ON",
            "SET NUMERIC_ROUNDABORT OFF",
            "SET DATEFIRST 1",
            "SET DATEFORMAT ymd",
            "SET LOCK_TIMEOUT 5000",
            "SET DEADLOCK_PRIORITY LOW",
            "SET DEADLOCK_PRIORITY NORMAL",
            "SET DEADLOCK_PRIORITY 0",
            "SET DEADLOCK_PRIORITY -5",
            "SET TRANSACTION ISOLATION LEVEL SNAPSHOT",
            "DECLARE @i int = 0; WHILE @i < 3 BEGIN SET @i += 1; IF @i = 2 CONTINUE; IF @i = 3 BREAK; END",
            "BEGIN TRY SELECT 1 END TRY BEGIN CATCH SELECT ERROR_NUMBER() END CATCH",
            "IF 1=0 RETURN; SELECT 1",
            "THROW 50000, 'x', 1",
            "RAISERROR('x', 10, 1) WITH NOWAIT",
            "PRINT 'x'",
            "CREATE TABLE #t (a int); BEGIN TRAN; INSERT #t VALUES (1); COMMIT",
            "BEGIN TRAN; COMMIT",
            "BEGIN TRAN; SAVE TRAN s; ROLLBACK TRAN s; ROLLBACK TRAN",
            "DECLARE @a int; DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT a FROM dbo.T; OPEN c; FETCH NEXT FROM c INTO @a; CLOSE c; DEALLOCATE c",
            "DECLARE @t TABLE (a int); WITH x AS (SELECT a FROM @t) UPDATE x SET a = 2",
            "CREATE TABLE #t (a int); WITH x AS (SELECT a FROM #t) UPDATE x SET a = 2",
            "EXEC dbo.P",
        };
        foreach (var sql in samples)
        {
            var mutation = _classifier.Classify(sql, SqlUsage.Mutation, "appdb", allowMutation: false);
            var approvedQuery = _classifier.Classify(sql, SqlUsage.Query, "appdb", allowMutation: true, confirmDatabase: "appdb");

            if (sql.StartsWith("DECLARE @t TABLE", StringComparison.Ordinal) ||
                sql.StartsWith("CREATE TABLE #t (a int); WITH x", StringComparison.Ordinal))
            {
                Assert.True(
                    Fingerprint(mutation) == (true, SqlSafetyReason.Allowed, true, true, SqlRiskTier.Dml),
                    $"{sql} => Mutation: {Fingerprint(mutation)}");
                Assert.True(
                    Fingerprint(approvedQuery) == (true, SqlSafetyReason.Allowed, true, true, SqlRiskTier.Dml),
                    $"{sql} => Query allowMutation: {Fingerprint(approvedQuery)}");
            }
            else if (sql.StartsWith("EXEC ", StringComparison.Ordinal))
            {
                Assert.Equal((true, SqlSafetyReason.Allowed, true, false, SqlRiskTier.Opaque), Fingerprint(mutation));
                Assert.Equal((false, SqlSafetyReason.UnsupportedStatement, false, false, SqlRiskTier.ReadOnly), Fingerprint(approvedQuery));
            }
            else
            {
                Assert.Equal((false, SqlSafetyReason.UnsupportedStatement, false, false, SqlRiskTier.ReadOnly), Fingerprint(mutation));
                Assert.Equal((false, SqlSafetyReason.UnsupportedStatement, false, false, SqlRiskTier.ReadOnly), Fingerprint(approvedQuery));
            }
        }
    }

    private static (bool Allowed, SqlSafetyReason Reason, bool HasMutation, bool HasSessionLocalWork, SqlRiskTier Tier) Fingerprint(SqlSafetyDecision decision) =>
        (decision.Allowed, decision.Reason, decision.HasMutation, decision.HasSessionLocalWork, decision.Tier);
}
