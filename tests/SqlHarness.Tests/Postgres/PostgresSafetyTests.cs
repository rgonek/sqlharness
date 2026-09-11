using SqlHarness.Core;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresSafetyTests
{
    private readonly PostgresSafetyClassifier _classifier = new();

    [Fact]
    public void Select_with_cte_is_read_only()
    {
        var decision = _classifier.Classify(
            "WITH x AS (SELECT 1 AS n) SELECT n FROM x",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void Create_temp_and_insert_are_session_local()
    {
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE t (id int);
            INSERT INTO t VALUES (1);
            SELECT * FROM t;
            """, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
        Assert.Contains("t", decision.SessionTempTables);
    }

    [Fact]
    public void Insert_into_setup_temp_is_session_local()
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE t (id int)",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        var query = _classifier.Classify(
            "INSERT INTO t SELECT 1; SELECT * FROM t",
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(query.Allowed);
        Assert.False(query.HasMutation);
    }

    [Fact]
    public void Persistent_insert_requires_mutation_flags()
    {
        var denied = _classifier.Classify(
            "INSERT INTO public.items SELECT 1",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);

        var allowed = _classifier.Classify(
            "INSERT INTO public.items SELECT 1",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.True(allowed.Allowed);
        Assert.True(allowed.HasMutation);

        var mismatch = _classifier.Classify(
            "INSERT INTO public.items SELECT 1",
            SqlUsage.Query, "appdb", true, "otherdb", Empty);
        Assert.False(mismatch.Allowed);
        Assert.Equal(SqlSafetyReason.DatabaseConfirmationMismatch, mismatch.Reason);
    }

    [Fact]
    public void Select_into_is_rejected()
    {
        var decision = _classifier.Classify(
            "SELECT 1 INTO persistent_copy",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.SelectIntoNotAllowed, decision.Reason);
    }

    [Fact]
    public void Persistent_create_table_is_unsupported()
    {
        var decision = _classifier.Classify(
            "CREATE TABLE t (id int)",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    [InlineData("BEGIN")]
    [InlineData("COMMIT")]
    [InlineData("SET search_path TO public")]
    [InlineData("COPY t FROM STDIN")]
    [InlineData("DO $$ BEGIN NULL; END $$")]
    [InlineData("PREPARE x AS SELECT 1")]
    [InlineData("SELECT nextval('s')")]
    [InlineData("SELECT dblink('dbname=other','select 1')")]
    [InlineData("SELECT * FROM dblink('dbname=other','select 1')")]
    [InlineData("SELECT * FROM pg_ls_dir('.')")]
    [InlineData("SELECT * FROM pg_catalog.pg_ls_dir('.')")]
    [InlineData("SELECT pg_read_binary_file('/etc/passwd')")]
    [InlineData("SELECT * FROM pg_ls_logdir()")]
    [InlineData("SELECT lo_export(123::oid, '/tmp/out')")]
    public void Denied_constructs(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData("SELECT pg_read_binary_file('/secret-token-path')")]
    [InlineData("SELECT * FROM pg_ls_logdir()")]
    [InlineData("SELECT lo_export(123::oid, '/secret-token-path')")]
    public void File_access_prefix_denials_do_not_echo_sql(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.DoesNotContain("secret-token-path", decision.RejectionDescription);
        Assert.DoesNotContain(sql, decision.RejectionDescription);
    }

    [Fact]
    public void Parse_error_is_denied_without_sql_echo()
    {
        const string sql = "SELECTnotvalid secret-token";
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.Equal(SqlSafetyReason.ParseError, decision.Reason);
        Assert.DoesNotContain("secret-token", decision.RejectionDescription);
    }

    [Fact]
    public void Cross_schema_select_is_allowed()
    {
        var decision = _classifier.Classify(
            "SELECT * FROM other.contracts",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed);
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
}
