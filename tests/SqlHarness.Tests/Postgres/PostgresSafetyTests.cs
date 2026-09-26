using System.Data;

using Npgsql;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

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

    [Theory]
    [InlineData("SELECT set_config('search_path', 'public', false)")]
    [InlineData("SELECT pg_catalog.set_config('search_path', 'public', false)")]
    [InlineData("""SELECT "set_config"('search_path', 'public', false)""")]
    [InlineData("SELECT pg_cancel_backend(12345)")]
    [InlineData("SELECT pg_terminate_backend(12345)")]
    [InlineData("SELECT pg_advisory_lock(42)")]
    [InlineData("SELECT pg_advisory_xact_lock(1, 2)")]
    [InlineData("SELECT pg_try_advisory_lock(42)")]
    [InlineData("SELECT PG_ADVISORY_LOCK(1)")]
    [InlineData("SELECT pg_catalog.pg_advisory_lock(42)")]
    [InlineData("SELECT setval('s', 1)")]
    [InlineData("SELECT lo_custom_readonly()")]
    [InlineData("SELECT * FROM (SELECT set_config('search_path', 'public', false)) s")]
    [InlineData("SELECT * FROM t JOIN (SELECT pg_cancel_backend(1)) s ON true")]
    [InlineData("CREATE TEMP TABLE x AS SELECT set_config('search_path', 'public', false)")]
    [InlineData("DELETE FROM t WHERE id IN (SELECT set_config('search_path', 'public', false))")]
    public void Visible_admin_and_lo_calls_are_denied_without_echoing_sql(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.Equal("UnsupportedStatement.", decision.RejectionDescription);
        Assert.DoesNotContain("search_path", decision.RejectionDescription, StringComparison.Ordinal);
        Assert.DoesNotContain(sql, decision.RejectionDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Query", "SELECT coalesce(set_config('search_path', 'public', false), 'x')")]
    [InlineData("CompareSetup", "SELECT coalesce(set_config('search_path', 'public', false), 'x')")]
    [InlineData("Query", "SELECT * FROM public.items WHERE length(set_config('search_path', 'public', false)) > 0")]
    [InlineData("CompareSetup", "SELECT * FROM public.items WHERE length(set_config('search_path', 'public', false)) > 0")]
    [InlineData("Query", "CREATE TEMP TABLE t (note text DEFAULT lower(set_config('search_path', 'public', false)))")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (note text DEFAULT lower(set_config('search_path', 'public', false)))")]
    [InlineData("Query", "SELECT coalesce(nextval('s'), 0)")]
    [InlineData("CompareSetup", "SELECT length(setval('s', 1)::text)")]
    [InlineData("Query", "SELECT * FROM public.items WHERE length(pg_cancel_backend(1)::text) > 0")]
    [InlineData("CompareSetup", "SELECT coalesce(pg_advisory_lock(1), 1)")]
    [InlineData("Query", "SELECT coalesce(pg_try_advisory_lock(1), false)")]
    public void Denied_calls_nested_in_other_calls_are_rejected(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.Equal("UnsupportedStatement.", decision.RejectionDescription);
        Assert.DoesNotContain("search_path", decision.RejectionDescription, StringComparison.Ordinal);
        Assert.DoesNotContain(sql, decision.RejectionDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Query", """CREATE TEMP TABLE t (note text DEFAULT set_config('search_path', 'public', false)); INSERT INTO t DEFAULT VALUES""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (note text DEFAULT pg_catalog.set_config('search_path', 'public', false)); INSERT INTO t DEFAULT VALUES""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int DEFAULT nextval('s')); INSERT INTO t DEFAULT VALUES""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int DEFAULT nextval('s')); INSERT INTO t DEFAULT VALUES""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int DEFAULT "setval"('s', 1)); INSERT INTO t DEFAULT VALUES""")]
    [InlineData("CompareSetup", """CREATE TEMPORARY TABLE t (id int DEFAULT "setval"('s', 1)); INSERT INTO t DEFAULT VALUES""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int CHECK (set_config('search_path', 'public', false) IS NOT NULL))""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int, CONSTRAINT c CHECK (set_config('search_path', 'public', false) IS NOT NULL))""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int GENERATED ALWAYS AS (set_config('search_path', 'public', false)::int) STORED)""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int GENERATED ALWAYS AS (set_config('search_path', 'public', false)::int) STORED)""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int); CREATE INDEX i ON t (id) WHERE set_config('search_path', 'public', false) IS NOT NULL""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int); CREATE INDEX i ON t ((set_config('search_path', 'public', false)))""")]
    public void Temp_ddl_expressions_cannot_hide_matrix_calls(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.Equal("UnsupportedStatement.", decision.RejectionDescription);
        Assert.DoesNotContain("search_path", decision.RejectionDescription, StringComparison.Ordinal);
        Assert.DoesNotContain(sql, decision.RejectionDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("CompareSetup")]
    public void Ordinary_temp_default_and_index_stay_session_local(string usage)
    {
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE t (id int DEFAULT 1);
            INSERT INTO t DEFAULT VALUES;
            CREATE INDEX i ON t (id);
            """, ParseUsage(usage), "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
        Assert.Contains("t", decision.SessionTempTables);
    }

    [Theory]
    [InlineData("Query", """CREATE TEMP TABLE t (id int) WITH (fillfactor = set_config('search_path', 'public', false))""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int) WITH (fillfactor = set_config('search_path', 'public', false))""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int) OPTIONS (fillfactor = set_config('search_path', 'public', false))""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int) OPTIONS (fillfactor = set_config('search_path', 'public', false))""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int) TBLPROPERTIES (x = set_config('search_path', 'public', false))""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int) TBLPROPERTIES (x = set_config('search_path', 'public', false))""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int) ORDER BY set_config('search_path', 'public', false)""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int) ORDER BY set_config('search_path', 'public', false)""")]
    [InlineData("Query", """CREATE TEMP TABLE t (id int); CREATE INDEX i ON t (id) WITH (set_config('search_path', 'public', false))""")]
    [InlineData("CompareSetup", """CREATE TEMP TABLE t (id int); CREATE INDEX i ON t (id) WITH (set_config('search_path', 'public', false))""")]
    public void Temp_option_and_order_expressions_cannot_hide_matrix_calls(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
        Assert.Equal("UnsupportedStatement.", decision.RejectionDescription);
        Assert.DoesNotContain("search_path", decision.RejectionDescription, StringComparison.Ordinal);
        Assert.DoesNotContain(sql, decision.RejectionDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("CompareSetup")]
    public void Ordinary_temp_with_and_order_by_stay_session_local(string usage)
    {
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE t (id int) WITH (fillfactor=10) ORDER BY id;
            CREATE INDEX i ON t (id) WITH (fillfactor=10);
            """, ParseUsage(usage), "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
        Assert.Contains("t", decision.SessionTempTables);
    }

    [Theory]
    [InlineData("SELECT app.write_something()")]
    [InlineData("SELECT * FROM app.hidden_view")]
    [InlineData("SELECT a + b FROM public.items")]
    [InlineData("SELECT currval('s')")]
    [InlineData("SELECT lastval()")]
    [InlineData("SELECT 'set_config'")]
    [InlineData("SELECT count(*) FROM public.items")]
    [InlineData("SELECT coalesce(lower('X'), 'x')")]
    [InlineData("SELECT * FROM public.items WHERE length(name) > 0")]
    public void Unresolved_calls_views_and_operators_stay_allowed_and_are_not_a_guarantee(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
        Assert.False(decision.HasSessionLocalWork);
    }

    [Theory]
    [InlineData("UPDATE public.items SET id = 1")]
    [InlineData("DELETE FROM public.items WHERE id = 1")]
    [InlineData("MERGE INTO public.items AS t USING (SELECT 1 AS id) AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET id = s.id")]
    public void Persistent_dml_still_requires_mutation_approval(string sql)
    {
        var denied = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);

        var approved = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.True(approved.Allowed, approved.RejectionDescription);
        Assert.True(approved.HasMutation);
    }

    [Fact]
    public void Server_valid_top_level_modifying_cte_is_a_persistent_write()
    {
        var denied = _classifier.Classify(
            "WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id) SELECT id FROM changed",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);

        var approved = _classifier.Classify(
            "WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id) SELECT id FROM changed",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.True(approved.Allowed, approved.RejectionDescription);
        Assert.True(approved.HasMutation);
    }

    [Fact]
    public void Library_accepted_insert_source_cte_is_denied_and_is_not_a_server_exploit()
    {
        // SqlParserCS accepts WITH between INSERT and its source. PostgreSQL attaches
        // a data-modifying WITH only to the top-level statement. This is not a server exploit.
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE x (id int);
            INSERT INTO x WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id)
            SELECT id FROM changed
            """, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    [Fact]
    public void Library_accepted_ctas_modifying_cte_is_denied_and_is_not_a_server_exploit()
    {
        // CREATE TABLE AS is not a top-level statement that PostgreSQL documents as
        // the attachment point of a data-modifying WITH. Library acceptance is not execution.
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE x AS WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING *) SELECT * FROM changed",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    [Fact]
    public void Library_rejected_ctas_delete_cte_is_a_parse_error_not_a_server_exploit()
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE x AS WITH changed AS (DELETE FROM public.items RETURNING *) SELECT * FROM changed",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.Equal(SqlSafetyReason.ParseError, decision.Reason);
        Assert.DoesNotContain("public.items", decision.RejectionDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Derived_subquery_write_is_not_hidden_by_a_temp_insert()
    {
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE x (id int);
            INSERT INTO x SELECT id FROM (
                WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id)
                SELECT id FROM changed) s
            """, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    [Fact]
    public void Update_from_library_accepted_cte_is_denied_and_is_not_a_server_exploit()
    {
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE t (id int);
            UPDATE t SET id = 1 FROM (
                WITH c AS (INSERT INTO public.items VALUES (1) RETURNING id)
                SELECT id FROM c) s
            """, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    [Fact]
    public void Temp_only_modifying_cte_stays_session_local()
    {
        var decision = _classifier.Classify("""
            CREATE TEMP TABLE x (id int);
            WITH changed AS (INSERT INTO x VALUES (1) RETURNING id)
            SELECT id FROM changed
            """, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
        Assert.Contains("x", decision.SessionTempTables);
    }

    [Fact]
    public void Plain_ctas_stays_session_local()
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE x AS SELECT * FROM public.items",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
        Assert.Contains("x", decision.SessionTempTables);
    }

    [Fact]
    public void Compare_setup_rejects_visible_persistent_write_inside_temp_ctas()
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE x AS WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING *) SELECT * FROM changed",
            SqlUsage.CompareSetup, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Fact]
    public void Compare_setup_still_allows_temp_ddl_and_denies_set_config()
    {
        var temp = _classifier.Classify(
            "CREATE TEMP TABLE t (id int); INSERT INTO t VALUES (1)",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(temp.Allowed, temp.RejectionDescription);
        Assert.True(temp.HasSessionLocalWork);
        Assert.Contains("t", temp.SessionTempTables);

        var config = _classifier.Classify(
            "SELECT set_config('search_path', 'public', false)",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, config.Reason);
        Assert.Equal("UnsupportedStatement.", config.RejectionDescription);
        Assert.DoesNotContain("search_path", config.RejectionDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_scope_does_not_wrap_sql_or_change_role_or_transaction_mode()
    {
        using var command = new NpgsqlCommand();
        const string sql = "CREATE TEMP TABLE t (id int)";
        NpgsqlSession.BindCommand(command, new SqlExecutionCommand(sql, [], 30));
        Assert.Equal(sql, command.CommandText);
        Assert.Equal(CommandType.Text, command.CommandType);
        Assert.Null(command.Transaction);

        Environment.SetEnvironmentVariable("SQLHARNESS_PG_PASSWORD", "secret");
        try
        {
            var target = new ResolvedTarget(
                "localhost,5432", "appdb",
                AuthSpec.Parse("sql", "sqlharness", "SQLHARNESS_PG_PASSWORD", true),
                "profile", SqlEngine.Postgres);
            var connectionString = PostgresConnectionString.Build(target, 15);
            Assert.DoesNotContain("default_transaction_read_only", connectionString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("search_path", connectionString, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Options=", connectionString, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_PG_PASSWORD", null);
        }
    }

    private static SqlUsage ParseUsage(string usage) => usage switch
    {
        "Query" => SqlUsage.Query,
        "CompareSetup" => SqlUsage.CompareSetup,
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "Expected Query or CompareSetup."),
    };

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
}