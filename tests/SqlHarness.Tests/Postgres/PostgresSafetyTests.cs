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

    [Theory]
    [InlineData("EXPLAIN SELECT * FROM public.items")]
    [InlineData("EXPLAIN (COSTS FALSE) SELECT 1")]
    [InlineData("EXPLAIN (ANALYZE FALSE) SELECT * FROM public.items")]
    public void Plan_only_explain_over_safe_select_is_read_only(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
        Assert.False(decision.HasSessionLocalWork);
    }

    [Theory]
    [InlineData("EXPLAIN ANALYZE SELECT * FROM public.items")]
    [InlineData("EXPLAIN (ANALYZE) SELECT * FROM public.items")]
    [InlineData("EXPLAIN (ANALYZE TRUE) SELECT * FROM public.items")]
    public void Explain_analyze_over_pure_reads_executes_without_mutation(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void Explain_analyze_over_persistent_write_needs_mutation_approval()
    {
        const string sql = "EXPLAIN ANALYZE INSERT INTO public.items SELECT 1";
        var denied = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);

        var approved = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.True(approved.Allowed, approved.RejectionDescription);
        Assert.True(approved.HasMutation);
    }

    [Theory]
    [InlineData("EXPLAIN INSERT INTO public.items SELECT 1")]
    [InlineData("EXPLAIN SELECT pg_sleep(1)")]
    public void Explain_without_analyze_stays_denied_outside_safe_select(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void Explain_over_persistent_select_into_reports_select_into()
    {
        var decision = _classifier.Classify(
            "EXPLAIN SELECT 1 INTO persistent_copy", SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.SelectIntoNotAllowed, decision.Reason);
    }

    [Fact]
    public void Select_into_temp_registers_session_locality()
    {
        var decision = _classifier.Classify(
            "SELECT a INTO TEMP TABLE t FROM s",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
        Assert.Contains("t", decision.SessionTempTables);
    }

    [Fact]
    public void Select_into_temp_chain_stays_session_local_in_setup()
    {
        var setup = _classifier.Classify(
            "SELECT a INTO TEMP TABLE t FROM s",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        var query = _classifier.Classify(
            "INSERT INTO t SELECT 1; SELECT * FROM t",
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(query.Allowed, query.RejectionDescription);
        Assert.False(query.HasMutation);
    }

    [Theory]
    [InlineData("SELECT 1 INTO persistent_copy")]
    [InlineData("SELECT 1 INTO TEMP TABLE public.t")]
    public void Select_into_without_unambiguous_temp_locality_stays_denied(string sql)
    {
        var query = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(query.Allowed);
        Assert.Equal(SqlSafetyReason.SelectIntoNotAllowed, query.Reason);

        var setup = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.False(setup.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, setup.Reason);
    }

    [Fact]
    public void Select_into_temp_with_persistent_modifying_cte_needs_approval()
    {
        const string sql = """
            WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id)
            SELECT id INTO TEMP TABLE t FROM changed
            """;
        var denied = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, denied.Reason);

        var approved = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.True(approved.Allowed, approved.RejectionDescription);
        Assert.True(approved.HasMutation);
        Assert.True(approved.HasSessionLocalWork);
    }

    [Fact]
    public void Canonical_analyze_is_a_parse_error_until_the_parser_supports_it()
    {
        // SqlParserCS 0.6.5 only accepts the Hive-style ANALYZE TABLE form and
        // rejects canonical PostgreSQL ANALYZE [VERBOSE] tbl: fail closed, no regex.
        var decision = _classifier.Classify("ANALYZE t", SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.ParseError, decision.Reason);
    }

    // 011/T5 parser spike (plans/011-analyze-parser-spike.md): probe-verified on
    // SqlParserCS 0.6.5 PostgreSqlDialect. Every canonical PostgreSQL ANALYZE
    // spelling the parser is asked to accept a TABLE keyword next and throws
    // SqlParser.ParserException when it is missing, so each form below is a
    // parse error -> PostgresDocument.TryParse returns false -> ParseError.
    // These forms never reach the classifier's Unsupported switch at all.
    [Theory]
    [InlineData("ANALYZE VERBOSE t")]
    [InlineData("ANALYZE (VERBOSE) t")]
    [InlineData("ANALYZE (SKIP_LOCKED) t")]
    [InlineData("ANALYZE")]
    public void T5_Analyze_additional_canonical_forms_are_parse_errors(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.ParseError, decision.Reason);
    }

    // 011/T5: the ONLY "ANALYZE ..." spelling this parser version actually
    // parses is the Hive-style ANALYZE TABLE form, which yields Statement.Analyze
    // (a Hive-shaped node: Name/Partitions/ForColumns/Columns/CacheMetadata/
    // NoScan/ComputeStatistics -- no VERBOSE, no PG column list). It must stay
    // denied and must never be reinterpreted as canonical PostgreSQL ANALYZE.
    [Fact]
    public void T5_Analyze_hive_form_stays_unsupported()
    {
        var decision = _classifier.Classify("ANALYZE TABLE t", SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    // 011/T4: TRUNCATE. A target is a current-session temp only when its
    // single-part name is in the session temp set (recorded from CREATE TEMP /
    // SELECT INTO TEMP). A name prefix or a schema qualifier is never proof.
    [Theory]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE TABLE t")]
    [InlineData("Query", "CREATE TEMPORARY TABLE t (id int); TRUNCATE ONLY t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE t RESTRICT")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); CREATE TEMP TABLE u (id int); TRUNCATE TABLE t, u")]
    [InlineData("Query", "SELECT 1 AS id INTO TEMP TABLE t; TRUNCATE t")]
    [InlineData("Query", "CREATE TEMP TABLE \"T\" (id int); TRUNCATE \"T\"")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); INSERT INTO t VALUES (1); TRUNCATE t")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); CREATE TEMP TABLE u (id int); TRUNCATE t, u")]
    public void T4_Truncate_all_session_temps_is_session_local(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void T4_Truncate_of_setup_temp_is_session_local_and_keeps_provenance()
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE t (id int)",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        var query = _classifier.Classify(
            "TRUNCATE t; INSERT INTO t VALUES (1)",
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(query.Allowed, query.RejectionDescription);
        Assert.True(query.HasSessionLocalWork);
        Assert.False(query.HasMutation);
        Assert.Contains("t", query.SessionTempTables);
    }

    [Theory]
    [InlineData("TRUNCATE items")]
    [InlineData("TRUNCATE TABLE public.items")]
    [InlineData("TRUNCATE ONLY items")]
    public void T4_Truncate_persistent_target_is_denied(string sql)
    {
        var denied = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(denied.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, denied.Reason);

        // Not approvable either: persistent TRUNCATE never joins the mutation path.
        var approved = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(approved.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, approved.Reason);

        var setup = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.False(setup.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, setup.Reason);
    }

    [Theory]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE t, items")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE items, t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE TABLE t, public.items")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); TRUNCATE t, items")]
    public void T4_Truncate_mixed_targets_are_denied(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Theory]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE t CASCADE")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE t CONTINUE IDENTITY CASCADE")]
    [InlineData("Query", "TRUNCATE items CASCADE")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); TRUNCATE t CASCADE")]
    public void T4_Truncate_cascade_is_denied(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE t RESTART IDENTITY")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE TABLE t RESTART IDENTITY RESTRICT")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE t RESTART IDENTITY CASCADE")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); TRUNCATE t RESTART IDENTITY")]
    public void T4_Truncate_restart_identity_is_denied_R4(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("CompareSetup")]
    public void T4_Truncate_continue_identity_over_temps_is_session_local(string usage)
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE t (id int); TRUNCATE t CONTINUE IDENTITY",
            ParseUsage(usage), "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    [InlineData("TRUNCATE pg_temp_3.t")]
    [InlineData("TRUNCATE pg_temp.t")]
    [InlineData("TRUNCATE pg_temp_items")]
    [InlineData("TRUNCATE \"pg_temp_3\".\"t\"")]
    public void T4_Truncate_pg_temp_prefix_without_provenance_is_denied(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", true, "appdb", Empty);
            Assert.False(decision.Allowed);
            Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
        }
    }

    [Theory]
    // A qualifier is never proof, even when the relation name matches a tracked temp.
    // 011/final (I2): the exact pg_temp alias over a proven temp is the one
    // exception; see Final_Truncate_of_pg_temp_qualified_proven_temp_is_session_local.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp_3.t")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE public.t")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE appdb.public.t")]
    public void T4_Truncate_schema_qualified_name_is_denied_even_when_relation_name_is_a_tracked_temp(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Theory]
    [InlineData("Query")]
    [InlineData("CompareSetup")]
    public void T4_Truncate_on_cluster_is_denied(string usage)
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE t (id int); TRUNCATE TABLE t ON CLUSTER c",
            ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void T4_Truncate_partition_clause_is_denied()
    {
        // Hive-only shape the library accepts; PostgreSQL has no such clause.
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE t (id int); TRUNCATE TABLE t PARTITION (id = 1)",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void T4_Truncate_after_drop_loses_provenance()
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE t (id int); DROP TABLE t; TRUNCATE t",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Theory]
    // Identifiers match as stored: quoted "T" and folded t are different relations.
    [InlineData("CREATE TEMP TABLE \"T\" (id int); TRUNCATE T")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE \"T\"")]
    public void T4_Truncate_matches_temp_names_case_sensitively(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Fact]
    public void T4_Truncate_before_create_temp_is_denied()
    {
        var decision = _classifier.Classify(
            "TRUNCATE t; CREATE TEMP TABLE t (id int)",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Theory]
    // Guard: the TRUNCATE rule is local to TRUNCATE. The shared session-local
    // helper behind DROP TABLE / CREATE INDEX / DML keeps its verdicts.
    [InlineData("Query", "CREATE TEMP TABLE t (id int); CREATE INDEX ix ON t (id); DROP TABLE t")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); CREATE INDEX ix ON t (id); DROP TABLE t")]
    [InlineData("Query", "CREATE INDEX ix ON pg_temp.t (id)")]
    [InlineData("Query", "DROP TABLE pg_temp.t")]
    [InlineData("Query", "INSERT INTO pg_temp.t VALUES (1)")]
    public void T4_Non_truncate_session_local_verdicts_are_unchanged(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    [InlineData("DROP TABLE items")]
    [InlineData("CREATE INDEX ix ON items (id)")]
    public void T4_Non_truncate_persistent_ddl_verdicts_are_unchanged(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Theory]
    // PostgreSQL folds only ASCII A-Z in an unquoted identifier; the offline
    // parser accepts non-ASCII unquoted identifiers and keeps them as written.
    // U+00C9 is not folded to U+00E9 by the server, and U+212A (Kelvin sign) is
    // not folded to k, so none of these targets is the temp the batch created.
    [InlineData("CREATE TEMP TABLE \"é\" (id int); TRUNCATE É")]
    [InlineData("CREATE TEMP TABLE k (id int); TRUNCATE K")]
    [InlineData("CREATE TEMP TABLE É (id int); TRUNCATE \"é\"")]
    [InlineData("CREATE TEMP TABLE K (id int); TRUNCATE k")]
    [InlineData("CREATE TEMP TABLE É (id int); TRUNCATE É")]
    [InlineData("SELECT 1 AS id INTO TEMP TABLE K; TRUNCATE k")]
    public void T4_Truncate_proof_does_not_depend_on_unicode_case_folding(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", true, "appdb", Empty);
            Assert.False(decision.Allowed);
            Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
        }
    }

    [Fact]
    public void T4_Truncate_non_ascii_unquoted_target_parses_and_is_denied_without_provenance()
    {
        // Pins the parser behaviour the rule above relies on: an identifier, not a parse error.
        var decision = _classifier.Classify("TRUNCATE É", SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Theory]
    // Quoted identifiers are exact, so a quoted non-ASCII temp stays provable;
    // ASCII unquoted names still fold the way the server folds them.
    [InlineData("CREATE TEMP TABLE \"é\" (id int); TRUNCATE \"é\"")]
    [InlineData("CREATE TEMP TABLE \"É\" (id int); TRUNCATE \"É\"")]
    [InlineData("CREATE TEMP TABLE Items (id int); TRUNCATE ITEMS")]
    [InlineData("CREATE TEMP TABLE Items (id int); TRUNCATE \"items\"")]
    public void T4_Truncate_exact_identifier_match_is_session_local(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // 011/T4b: flipped. This guard used to pin the Unicode fold for DML / DROP /
    // CREATE INDEX as session-local; the server would address a persistent
    // relation, so the same identifier rule as TRUNCATE now applies.
    [InlineData("CREATE TEMP TABLE \"é\" (id int); INSERT INTO É VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); CREATE INDEX ix ON É (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE É", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE É (id int); DELETE FROM \"é\"", "MutationNotAllowed")]
    public void T4_Non_truncate_non_ascii_fold_is_denied(string sql, string expected)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());
    }

    [Fact]
    public void T4_Truncate_proof_travels_only_in_a_classifier_produced_temp_set()
    {
        // A bare name set carries no record of how each temp was declared, so it
        // proves no TRUNCATE target; other statements read it as before.
        var plain = new HashSet<string>(StringComparer.Ordinal) { "t" };
        var truncate = _classifier.Classify("TRUNCATE t", SqlUsage.Query, "appdb", true, "appdb", plain);
        Assert.False(truncate.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, truncate.Reason);

        var insert = _classifier.Classify("INSERT INTO t VALUES (1)", SqlUsage.Query, "appdb", false, null, plain);
        Assert.True(insert.Allowed, insert.RejectionDescription);
        Assert.False(insert.HasMutation);

        // The proof survives any number of classifier-produced hops.
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE t (id int)", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        var first = _classifier.Classify(
            "INSERT INTO t VALUES (1)", SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        var second = _classifier.Classify(
            "TRUNCATE t", SqlUsage.Query, "appdb", false, null, first.SessionTempTables);
        Assert.True(second.Allowed, second.RejectionDescription);
        Assert.False(second.HasMutation);
    }

    [Fact]
    public void T4_Truncate_of_carried_temp_declared_with_non_ascii_unquoted_name_is_denied()
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE K (id int)", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        var query = _classifier.Classify(
            "TRUNCATE k", SqlUsage.Query, "appdb", true, "appdb", setup.SessionTempTables);
        Assert.False(query.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, query.Reason);
    }

    [Theory]
    // An ON COMMIT DROP temp is gone once the creating transaction ends, and an
    // unqualified TRUNCATE of its name would then reach a persistent table.
    [InlineData("CREATE TEMP TABLE items (id int) ON COMMIT DROP")]
    [InlineData("CREATE TEMP TABLE items ON COMMIT DROP AS SELECT 1 AS id")]
    public void T4_Truncate_of_setup_temp_created_on_commit_drop_is_denied(string setupSql)
    {
        var setup = _classifier.Classify(setupSql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        // 011/T4b: the name is no longer reported as a session temp at all.
        Assert.DoesNotContain("items", setup.SessionTempTables);

        var truncate = _classifier.Classify(
            "TRUNCATE items", SqlUsage.Query, "appdb", true, "appdb", setup.SessionTempTables);
        Assert.False(truncate.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, truncate.Reason);

        // 011/T4b: flipped. A write to the same carried name is a persistent write.
        var insert = _classifier.Classify(
            "INSERT INTO items VALUES (1)", SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(insert.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, insert.Reason);
    }

    [Theory]
    [InlineData("Query", "CREATE TEMP TABLE t (id int) ON COMMIT DROP; TRUNCATE t")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int) ON COMMIT DROP; TRUNCATE t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); DROP TABLE t; CREATE TEMP TABLE t (id int) ON COMMIT DROP; TRUNCATE t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); CREATE TEMP TABLE IF NOT EXISTS t (id int) ON COMMIT DROP; TRUNCATE t")]
    public void T4_Truncate_of_on_commit_drop_temp_in_the_same_batch_is_denied(string usage, string sql)
    {
        var decision = _classifier.Classify(sql, ParseUsage(usage), "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
    }

    [Theory]
    // These ON COMMIT actions keep the table itself, so the proof holds.
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DELETE ROWS; TRUNCATE t")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT PRESERVE ROWS; TRUNCATE t")]
    public void T4_Truncate_of_temp_that_survives_commit_is_session_local(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // The offline parser accepts EXPLAIN over TRUNCATE (the server does not).
    // EXPLAIN ANALYZE executes its inner statement, so it inherits the TRUNCATE
    // verdict and the same target proof; it is never an extra way in.
    [InlineData("CREATE TEMP TABLE t (id int); EXPLAIN ANALYZE TRUNCATE t")]
    [InlineData("CREATE TEMP TABLE t (id int); EXPLAIN (ANALYZE) TRUNCATE t")]
    public void T4_Explain_analyze_truncate_of_session_temp_inherits_session_local_verdict(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    [InlineData("EXPLAIN ANALYZE TRUNCATE items")]
    [InlineData("EXPLAIN (ANALYZE) TRUNCATE items")]
    [InlineData("CREATE TEMP TABLE t (id int); EXPLAIN ANALYZE TRUNCATE t, items")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; EXPLAIN ANALYZE TRUNCATE t")]
    public void T4_Explain_analyze_truncate_of_unproven_target_is_denied(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", true, "appdb", Empty);
            Assert.False(decision.Allowed);
            Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
        }
    }

    [Theory]
    // Plan-only EXPLAIN is allowed over a safe SELECT only.
    [InlineData("CREATE TEMP TABLE t (id int); EXPLAIN TRUNCATE t")]
    [InlineData("EXPLAIN TRUNCATE items")]
    public void T4_Plan_only_explain_truncate_is_denied(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    // 011/T4b: the session-temp proof is the same for every statement kind.
    // PostgreSQL folds an unquoted identifier itself (ASCII only under UTF-8,
    // encoding-dependent otherwise), so an unquoted name with a non-ASCII
    // character never proves a target and never records a provable temp.
    // \u00C9 / \u00E9 are E-acute upper / lower; \u212A is the Kelvin sign.
    [Theory]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); INSERT INTO \u00C9 VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); UPDATE \u00C9 SET id = 1", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); DELETE FROM \u00C9", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); MERGE INTO \u00C9 USING (SELECT 1 AS id) s ON \u00C9.id = s.id WHEN MATCHED THEN DELETE", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); DROP TABLE \u00C9", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); CREATE INDEX ix ON \u00C9 (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); INSERT INTO \"\u00E9\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); UPDATE \"\u00E9\" SET id = 1", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); DELETE FROM \"\u00E9\"", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); DROP TABLE \"\u00E9\"", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); CREATE INDEX ix ON \"\u00E9\" (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE k (id int); INSERT INTO \u212A VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \u212A (id int); DROP TABLE k", "UnsupportedStatement")]
    [InlineData("SELECT 1 AS id INTO TEMP TABLE \u212A; DELETE FROM k", "MutationNotAllowed")]
    // Quoted and unquoted spellings of the same non-ASCII text: equal under a
    // UTF-8 server, different under an encoding whose fold changes the letter.
    [InlineData("CREATE TEMP TABLE \"\u00C9\" (id int); INSERT INTO \u00C9 VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); DROP TABLE \"\u00C9\"", "UnsupportedStatement")]
    // Unquoted on both sides: stricter than needed, same decision as TRUNCATE.
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); INSERT INTO \u00C9 VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); CREATE INDEX ix ON \u00C9 (id)", "UnsupportedStatement")]
    public void T4b_Non_ascii_name_never_proves_a_target_across_quoting(string sql, string expected)
    {
        var query = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());

        var setup = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.False(setup.Allowed);
        Assert.Equal(
            expected == "MutationNotAllowed" ? "NonTemporaryWrite" : expected,
            setup.Reason.ToString());
    }

    [Theory]
    [InlineData("INSERT INTO k VALUES (1)", "MutationNotAllowed")]
    [InlineData("UPDATE k SET id = 1", "MutationNotAllowed")]
    [InlineData("DELETE FROM k", "MutationNotAllowed")]
    [InlineData("DROP TABLE k", "UnsupportedStatement")]
    [InlineData("CREATE INDEX ix ON k (id)", "UnsupportedStatement")]
    public void T4b_Carried_temp_declared_with_non_ascii_unquoted_name_proves_no_target(
        string querySql, string expected)
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE \u212A (id int)", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        Assert.DoesNotContain("k", setup.SessionTempTables);

        var query = _classifier.Classify(
            querySql, SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());
    }

    [Theory]
    // ASCII behaviour is exactly as before: an unquoted name folds to lower
    // case, a quoted name is taken as written; a quoted non-ASCII name matches
    // only the same quoted spelling.
    [InlineData("CREATE TEMP TABLE Items (id int); INSERT INTO items VALUES (1)")]
    [InlineData("CREATE TEMP TABLE Items (id int); INSERT INTO ITEMS VALUES (1)")]
    [InlineData("CREATE TEMP TABLE Items (id int); UPDATE \"items\" SET id = 1")]
    [InlineData("CREATE TEMP TABLE \"items\" (id int); DELETE FROM Items")]
    [InlineData("CREATE TEMP TABLE Items (id int); CREATE INDEX ix ON ITEMS (id); DROP TABLE iTeMs")]
    [InlineData("CREATE TEMP TABLE \"Items\" (id int); INSERT INTO \"Items\" VALUES (1); DROP TABLE \"Items\"")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); INSERT INTO \"\u00E9\" VALUES (1); CREATE INDEX ix ON \"\u00E9\" (id); DROP TABLE \"\u00E9\"")]
    [InlineData("SELECT 1 AS id INTO TEMP TABLE \"\u00C9\"; DELETE FROM \"\u00C9\"")]
    public void T4b_Ascii_fold_and_exact_quoted_match_stay_session_local(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", false, null, Empty);
            Assert.True(decision.Allowed, decision.RejectionDescription);
            Assert.True(decision.HasSessionLocalWork);
            Assert.False(decision.HasMutation);
        }
    }

    [Theory]
    [InlineData("CREATE TEMP TABLE Items (id int); INSERT INTO \"Items\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"Items\" (id int); INSERT INTO Items VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"Items\" (id int); DROP TABLE items", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); DELETE FROM \"\u00C9\"", "MutationNotAllowed")]
    public void T4b_Ascii_quoted_case_mismatch_stays_denied(string sql, string expected)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());
    }

    [Theory]
    // An ON COMMIT DROP temp is gone when its transaction ends, so its name must
    // not prove any later write or DDL target.
    [InlineData("CREATE TEMP TABLE items (id int) ON COMMIT DROP", "INSERT INTO items VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE items (id int) ON COMMIT DROP", "UPDATE items SET id = 1", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE items (id int) ON COMMIT DROP", "DELETE FROM items", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE items (id int) ON COMMIT DROP", "DROP TABLE items", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE items (id int) ON COMMIT DROP", "CREATE INDEX ix ON items (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE items ON COMMIT DROP AS SELECT 1 AS id", "DELETE FROM items", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE items (id int); CREATE TEMP TABLE IF NOT EXISTS items (id int) ON COMMIT DROP", "DELETE FROM items", "MutationNotAllowed")]
    public void T4b_Carried_on_commit_drop_temp_proves_no_target(
        string setupSql, string querySql, string expected)
    {
        var setup = _classifier.Classify(setupSql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        Assert.DoesNotContain("items", setup.SessionTempTables);

        var query = _classifier.Classify(
            querySql, SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());
    }

    [Theory]
    // Same batch: denied as well, the same decision as TRUNCATE. The classifier
    // does not know where the transaction that owns the temp ends.
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; UPDATE t SET id = 1", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; DELETE FROM t", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; DROP TABLE t", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; CREATE INDEX ix ON t (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; EXPLAIN ANALYZE INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE t (id int); DROP TABLE t; CREATE TEMP TABLE t (id int) ON COMMIT DROP; INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    public void T4b_On_commit_drop_temp_in_the_same_batch_proves_no_target(string sql, string expected)
    {
        var query = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());

        var setup = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.False(setup.Allowed);
        Assert.Equal(
            expected == "MutationNotAllowed" ? "NonTemporaryWrite" : expected,
            setup.Reason.ToString());
    }

    [Theory]
    // These ON COMMIT actions keep the table itself, so the proof holds.
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DELETE ROWS; INSERT INTO t VALUES (1); DROP TABLE t")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT PRESERVE ROWS; DELETE FROM t; CREATE INDEX ix ON t (id)")]
    public void T4b_Temp_that_survives_commit_stays_session_local(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // N1: when the name already exists as an ON COMMIT DROP temp, the server
    // treats CREATE TEMP TABLE IF NOT EXISTS as a no-op. It must not restore proof.
    [InlineData("TRUNCATE items", "NonTemporaryWrite")]
    [InlineData("INSERT INTO items VALUES (1)", "MutationNotAllowed")]
    [InlineData("DELETE FROM items", "MutationNotAllowed")]
    [InlineData("DROP TABLE items", "UnsupportedStatement")]
    [InlineData("CREATE INDEX ix ON items (id)", "UnsupportedStatement")]
    public void T4b_If_not_exists_does_not_restore_proof_for_an_on_commit_drop_temp(
        string querySql, string expected)
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE items (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS items (id int)",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        Assert.DoesNotContain("items", setup.SessionTempTables);

        var carried = _classifier.Classify(
            querySql, SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(carried.Allowed);
        Assert.Equal(expected, carried.Reason.ToString());

        // The same sequence inside one batch.
        var sameBatch = _classifier.Classify(
            "CREATE TEMP TABLE items (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS items (id int); " + querySql,
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(sameBatch.Allowed);
        Assert.Equal(expected, sameBatch.Reason.ToString());

        // The ON COMMIT DROP record is carried even when nothing else was created.
        var first = _classifier.Classify(
            "CREATE TEMP TABLE items (id int) ON COMMIT DROP",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        var second = _classifier.Classify(
            "CREATE TEMP TABLE IF NOT EXISTS \"items\" (id int); " + querySql,
            SqlUsage.Query, "appdb", false, null, first.SessionTempTables);
        Assert.False(second.Allowed);
        Assert.Equal(expected, second.Reason.ToString());
    }

    [Theory]
    // An unquoted non-ASCII ON COMMIT DROP name has an unknown server spelling,
    // so afterwards IF NOT EXISTS proves no non-ASCII name at all.
    [InlineData("CREATE TEMP TABLE \u00C9 (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS \"\u00E9\" (id int); TRUNCATE \"\u00E9\"", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS \"\u00E9\" (id int); INSERT INTO \"\u00E9\" VALUES (1)", "MutationNotAllowed")]
    public void T4b_If_not_exists_after_non_ascii_on_commit_drop_proves_no_non_ascii_name(
        string sql, string expected)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());
    }

    [Fact]
    public void T4b_If_not_exists_without_an_on_commit_drop_record_still_proves_the_temp()
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE IF NOT EXISTS items (id int); INSERT INTO items VALUES (1); TRUNCATE items",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Theory]
    // Pins the offline parser (SqlParserCS, PostgreSqlDialect): a Unicode-escape
    // identifier U&"..." is not accepted as a relation name, so no batch that
    // declares or targets one reaches the session-temp proof.
    [InlineData("CREATE TEMP TABLE U&\"\\0074\" (id int)")]
    [InlineData("CREATE TEMP TABLE U&\"\\0074\" (id int); TRUNCATE \"\\0074\"")]
    [InlineData("CREATE TEMP TABLE U&\"\\0074\" (id int); INSERT INTO \"\\0074\" VALUES (1)")]
    [InlineData("CREATE TEMP TABLE U&\"\\0074\" (id int); DELETE FROM \"\\0074\"")]
    [InlineData("CREATE TEMP TABLE U&\"\\0074\" (id int); DROP TABLE \"\\0074\"")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE U&\"\\0074\"")]
    [InlineData("CREATE TEMP TABLE t (id int); INSERT INTO U&\"\\0074\" VALUES (1)")]
    [InlineData("CREATE TEMP TABLE u&\"\\0074\" (id int)")]
    [InlineData("CREATE TEMP TABLE U&\"d!0061t\" UESCAPE '!' (id int)")]
    public void T4b_Unicode_escape_identifier_is_a_parse_error(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", true, "appdb", Empty);
            Assert.False(decision.Allowed);
            Assert.Equal(SqlSafetyReason.ParseError, decision.Reason);
        }
    }

    [Theory]
    // A quoted name that only looks like an escape is an ordinary identifier
    // spelled with a backslash; it is not the temp t.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE \"\\0074\"", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); INSERT INTO \"\\0074\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE t (id int); DROP TABLE \"\\0074\"", "UnsupportedStatement")]
    public void T4b_Quoted_backslash_name_is_not_decoded(string sql, string expected)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());
    }

    // 011/T4b fix round 1 (F1): a DROP revokes proof for every tracked temp it
    // may address. `pg_temp.<unquoted non-ASCII>` has a server spelling that is
    // unknown offline, so it may have dropped any tracked temp.
    [Theory]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; INSERT INTO \"é\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; UPDATE \"é\" SET id = 1", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; DELETE FROM \"é\"", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; MERGE INTO \"é\" USING (SELECT 1 AS id) s ON \"é\".id = s.id WHEN MATCHED THEN DELETE", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; CREATE INDEX ix ON \"é\" (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; DROP TABLE \"é\"", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; TRUNCATE \"é\"", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE IF EXISTS pg_temp.É; EXPLAIN ANALYZE INSERT INTO \"é\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE pg_temp.É; INSERT INTO \"é\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); CREATE TEMP TABLE other (id int); DROP TABLE other, pg_temp.É; INSERT INTO \"é\" VALUES (1)", "MutationNotAllowed")]
    // Kelvin sign: Unicode lower-casing maps it to k; the server never does.
    [InlineData("CREATE TEMP TABLE k (id int); DROP TABLE pg_temp.K; INSERT INTO k VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE k (id int); DROP TABLE pg_temp.K; TRUNCATE k", "NonTemporaryWrite")]
    // The unqualified shape of the same sequence: the DROP itself is unproven.
    [InlineData("CREATE TEMP TABLE \"pg_temp_é\" (id int); DROP TABLE PG_TEMP_É; INSERT INTO \"pg_temp_é\" VALUES (1)", "UnsupportedStatement")]
    // When in doubt every proof goes, including an unrelated ASCII temp.
    [InlineData("CREATE TEMP TABLE items (id int); DROP TABLE IF EXISTS pg_temp.É; INSERT INTO items VALUES (1)", "MutationNotAllowed")]
    public void T4b_Drop_with_unknown_stored_name_revokes_every_tracked_temp(string sql, string expected) =>
        AssertDeniedInBothUsages(sql, expected);

    [Theory]
    [InlineData("INSERT INTO \"é\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE INDEX ix ON \"é\" (id)", "UnsupportedStatement")]
    [InlineData("TRUNCATE \"é\"", "NonTemporaryWrite")]
    public void T4b_Drop_with_unknown_stored_name_revokes_a_carried_temp(string tail, string expected)
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE \"é\" (id int)", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);

        var query = _classifier.Classify(
            "DROP TABLE IF EXISTS pg_temp.É; " + tail,
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());

        // The revocation travels: the DROP in one batch, the write in the next.
        var drop = _classifier.Classify(
            "DROP TABLE IF EXISTS pg_temp.É", SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(drop.Allowed, drop.RejectionDescription);
        Assert.Empty(drop.SessionTempTables);
        var later = _classifier.Classify(tail, SqlUsage.Query, "appdb", false, null, drop.SessionTempTables);
        Assert.False(later.Allowed);
        Assert.Equal(expected, later.Reason.ToString());
    }

    [Theory]
    // A DROP whose stored name is known revokes exactly that temp, as before.
    [InlineData("CREATE TEMP TABLE items (id int); CREATE TEMP TABLE other (id int); DROP TABLE pg_temp.other; INSERT INTO items VALUES (1); TRUNCATE items")]
    [InlineData("CREATE TEMP TABLE items (id int); CREATE TEMP TABLE \"é\" (id int); DROP TABLE pg_temp.\"é\"; DELETE FROM items")]
    [InlineData("CREATE TEMP TABLE items (id int); CREATE TEMP TABLE other (id int); DROP TABLE other; UPDATE items SET id = 1")]
    public void T4b_Drop_with_known_stored_name_keeps_the_other_temps(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", false, null, Empty);
            Assert.True(decision.Allowed, decision.RejectionDescription);
            Assert.True(decision.HasSessionLocalWork);
            Assert.False(decision.HasMutation);
        }
    }

    // 011/T4b fix round 1 (F2): the server truncates an identifier to 63 bytes,
    // so two spellings longer than that can address one relation. Such a name
    // never proves and is never proven. {A63} is 63 ASCII letters; {E32} is 32
    // two-byte letters (64 bytes in UTF-8); {E31} is 31 of them (62 bytes).
    [Theory]
    [InlineData("CREATE TEMP TABLE {A63}x (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS {A63}y (id int); INSERT INTO {A63}y VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS {A63}y (id int); TRUNCATE {A63}y", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS {A63} (id int); INSERT INTO {A63} VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS {A63} (id int); TRUNCATE {A63}", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS \"{A63}\" (id int); DROP TABLE \"{A63}\"", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE {A63} (id int); CREATE TEMP TABLE IF NOT EXISTS {A63}x (id int) ON COMMIT DROP; INSERT INTO {A63} VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); DROP TABLE pg_temp.{A63}y; INSERT INTO {A63}x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63} (id int); DROP TABLE pg_temp.{A63}y; INSERT INTO {A63} VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63} (id int); DROP TABLE pg_temp.{A63}y; TRUNCATE {A63}", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE {A63} (id int); DROP TABLE IF EXISTS pg_temp.\"{A63}y\"; CREATE INDEX ix ON {A63} (id)", "UnsupportedStatement")]
    // The same long spelling on both sides: safe on the server, denied by the rule.
    [InlineData("CREATE TEMP TABLE {A63}x (id int); INSERT INTO {A63}x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); UPDATE {A63}x SET id = 1", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); DELETE FROM {A63}x", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); DROP TABLE {A63}x", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); CREATE INDEX ix ON {A63}x (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); TRUNCATE {A63}x", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \"{A63}x\" (id int); DELETE FROM \"{A63}x\"", "MutationNotAllowed")]
    [InlineData("SELECT 1 AS id INTO TEMP TABLE {A63}x; DELETE FROM {A63}x", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"{E32}\" (id int); INSERT INTO \"{E32}\" VALUES (1)", "MutationNotAllowed")]
    // A long declaration or target never matches its own 63-byte prefix either.
    [InlineData("CREATE TEMP TABLE {A63}x (id int); INSERT INTO {A63} VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63} (id int); INSERT INTO {A63}x VALUES (1)", "MutationNotAllowed")]
    public void T4b_Name_longer_than_63_bytes_never_proves_and_is_never_proven(string sql, string expected) =>
        AssertDeniedInBothUsages(ExpandNames(sql), expected);

    [Theory]
    [InlineData("CREATE TEMP TABLE IF NOT EXISTS {A63} (id int); INSERT INTO {A63} VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE IF NOT EXISTS items (id int); TRUNCATE items", "NonTemporaryWrite")]
    public void T4b_Carried_long_on_commit_drop_name_blocks_if_not_exists_proof(string querySql, string expected)
    {
        // The stored name of the ON COMMIT DROP temp is not known offline, so the
        // record that travels with the set withholds proof from every later
        // IF NOT EXISTS, whatever its name.
        var setup = _classifier.Classify(
            ExpandNames("CREATE TEMP TABLE \"{E32}\" (id int) ON COMMIT DROP"),
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);

        var query = _classifier.Classify(
            ExpandNames(querySql), SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());
    }

    [Theory]
    // Exactly 63 bytes is not truncated and is proven like any other name.
    [InlineData("CREATE TEMP TABLE {A63} (id int); INSERT INTO {A63} VALUES (1); TRUNCATE {A63}; CREATE INDEX ix ON {A63} (id); DROP TABLE {A63}")]
    [InlineData("CREATE TEMP TABLE \"{E31}a\" (id int); INSERT INTO \"{E31}a\" VALUES (1); TRUNCATE \"{E31}a\"")]
    public void T4b_Name_of_exactly_63_bytes_stays_session_local(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(ExpandNames(sql), usage, "appdb", false, null, Empty);
            Assert.True(decision.Allowed, decision.RejectionDescription);
            Assert.True(decision.HasSessionLocalWork);
            Assert.False(decision.HasMutation);
        }
    }

    // 011/T4b fix round 1 (Ruling R12): a pg_temp_ prefix is not proof of the
    // current session. An unqualified relation merely named pg_temp_stuff is an
    // ordinary name, and pg_temp_<N> can be another session's temp schema.
    [Theory]
    [InlineData("INSERT INTO pg_temp_stuff VALUES (1)", "MutationNotAllowed")]
    [InlineData("UPDATE pg_temp_stuff SET id = 1", "MutationNotAllowed")]
    [InlineData("DELETE FROM pg_temp_stuff", "MutationNotAllowed")]
    [InlineData("MERGE INTO pg_temp_stuff USING (SELECT 1 AS id) s ON pg_temp_stuff.id = s.id WHEN MATCHED THEN DELETE", "MutationNotAllowed")]
    [InlineData("DROP TABLE pg_temp_stuff", "UnsupportedStatement")]
    [InlineData("DROP TABLE IF EXISTS pg_temp_stuff", "UnsupportedStatement")]
    [InlineData("CREATE INDEX ix ON pg_temp_stuff (id)", "UnsupportedStatement")]
    [InlineData("EXPLAIN ANALYZE INSERT INTO pg_temp_stuff VALUES (1)", "MutationNotAllowed")]
    [InlineData("WITH changed AS (INSERT INTO pg_temp_stuff VALUES (1) RETURNING id) SELECT id FROM changed", "MutationNotAllowed")]
    [InlineData("INSERT INTO \"pg_temp_stuff\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("INSERT INTO PG_TEMP_STUFF VALUES (1)", "MutationNotAllowed")]
    // An unqualified relation named exactly pg_temp is not the schema alias.
    [InlineData("INSERT INTO pg_temp VALUES (1)", "MutationNotAllowed")]
    [InlineData("DROP TABLE pg_temp", "UnsupportedStatement")]
    [InlineData("CREATE INDEX ix ON pg_temp (id)", "UnsupportedStatement")]
    // pg_temp_<N> as a schema qualifier.
    [InlineData("INSERT INTO pg_temp_3.x VALUES (1)", "MutationNotAllowed")]
    [InlineData("UPDATE pg_temp_3.x SET id = 1", "MutationNotAllowed")]
    [InlineData("DELETE FROM pg_temp_3.x", "MutationNotAllowed")]
    [InlineData("MERGE INTO pg_temp_3.x USING (SELECT 1 AS id) s ON x.id = s.id WHEN MATCHED THEN DELETE", "MutationNotAllowed")]
    [InlineData("DROP TABLE pg_temp_3.x", "UnsupportedStatement")]
    [InlineData("CREATE INDEX ix ON pg_temp_3.x (id)", "UnsupportedStatement")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM pg_temp_3.x", "MutationNotAllowed")]
    [InlineData("INSERT INTO \"pg_temp_3\".\"x\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("DELETE FROM pg_temp_fake.items", "MutationNotAllowed")]
    // A tracked temp of the same relation name does not lend proof to the qualifier.
    [InlineData("CREATE TEMP TABLE x (id int); INSERT INTO pg_temp_3.x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE x (id int); DROP TABLE pg_temp_3.x", "UnsupportedStatement")]
    // pg_temp in any position other than the schema of a two-part name.
    [InlineData("INSERT INTO pg_temp.x.y VALUES (1)", "MutationNotAllowed")]
    [InlineData("DROP TABLE pg_temp.x.y", "UnsupportedStatement")]
    [InlineData("INSERT INTO \"PG_TEMP\".x VALUES (1)", "MutationNotAllowed")]
    public void T4b_Pg_temp_prefix_is_not_proof_for_any_statement(string sql, string expected) =>
        AssertDeniedInBothUsages(sql, expected);

    [Fact]
    public void T4b_Write_to_a_pg_temp_prefixed_name_is_an_ordinary_approvable_mutation()
    {
        foreach (var sql in new[] { "INSERT INTO pg_temp_stuff VALUES (1)", "DELETE FROM pg_temp_3.x" })
        {
            var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", Empty);
            Assert.True(decision.Allowed, decision.RejectionDescription);
            Assert.True(decision.HasMutation);
            Assert.False(decision.HasSessionLocalWork);
        }
    }

    [Theory]
    // An unqualified pg_temp_-prefixed name is session-local only when proven
    // like any other temp; the plain pg_temp schema alias keeps its treatment.
    [InlineData("CREATE TEMP TABLE pg_temp_stuff (id int); INSERT INTO pg_temp_stuff VALUES (1); CREATE INDEX ix ON pg_temp_stuff (id); TRUNCATE pg_temp_stuff; DROP TABLE pg_temp_stuff")]
    [InlineData("INSERT INTO pg_temp.x VALUES (1)")]
    [InlineData("UPDATE PG_TEMP.x SET id = 1")]
    [InlineData("DELETE FROM \"pg_temp\".x")]
    [InlineData("CREATE INDEX ix ON pg_temp.x (id)")]
    [InlineData("DROP TABLE IF EXISTS pg_temp.x")]
    public void T4b_Proven_pg_temp_prefixed_temp_and_pg_temp_alias_stay_session_local(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", false, null, Empty);
            Assert.True(decision.Allowed, decision.RejectionDescription);
            Assert.True(decision.HasSessionLocalWork);
            Assert.False(decision.HasMutation);
        }
    }

    [Fact]
    public void T4b_Dropped_pg_temp_prefixed_temp_loses_proof()
    {
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE pg_temp_stuff (id int); DROP TABLE pg_temp_stuff; INSERT INTO pg_temp_stuff VALUES (1)",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, decision.Reason);
    }

    // 011/T4b fix round 1 (F3): the "unknown stored name" record of an unquoted
    // non-ASCII ON COMMIT DROP declaration travels with the set from setup.
    [Theory]
    [InlineData("INSERT INTO \"é\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("TRUNCATE \"é\"", "NonTemporaryWrite")]
    [InlineData("DROP TABLE \"é\"", "UnsupportedStatement")]
    public void T4b_Carried_non_ascii_on_commit_drop_record_blocks_if_not_exists_proof(
        string tail, string expected)
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE É (id int) ON COMMIT DROP", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        Assert.Empty(setup.SessionTempTables);

        var query = _classifier.Classify(
            "CREATE TEMP TABLE IF NOT EXISTS \"é\" (id int); " + tail,
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());

        // The record survives a hop through a batch that declares nothing.
        var hop = _classifier.Classify(
            "SELECT 1", SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(hop.Allowed, hop.RejectionDescription);
        var later = _classifier.Classify(
            "CREATE TEMP TABLE IF NOT EXISTS \"é\" (id int); " + tail,
            SqlUsage.Query, "appdb", false, null, hop.SessionTempTables);
        Assert.False(later.Allowed);
        Assert.Equal(expected, later.Reason.ToString());
    }

    // 011/final (M2): flipped. This test used to pin that an all-ASCII name is
    // still proven by IF NOT EXISTS after an unquoted non-ASCII ON COMMIT DROP
    // declaration, on the claim that the unknown stored name is non-ASCII under
    // every server fold. That does not hold: a single-byte Turkish locale folds
    // U+0130 to ASCII i, so `İtems` can be stored as `items`. After such a
    // declaration IF NOT EXISTS proves no name at all.
    [Theory]
    [InlineData("İtems", "INSERT INTO items VALUES (1)", "MutationNotAllowed")]
    [InlineData("İtems", "TRUNCATE items", "NonTemporaryWrite")]
    [InlineData("İtems", "TRUNCATE pg_temp.items", "NonTemporaryWrite")]
    [InlineData("İtems", "DROP TABLE items", "UnsupportedStatement")]
    [InlineData("İtems", "CREATE INDEX ix ON items (id)", "UnsupportedStatement")]
    [InlineData("É", "INSERT INTO items VALUES (1)", "MutationNotAllowed")]
    [InlineData("É", "TRUNCATE items", "NonTemporaryWrite")]
    public void Final_If_not_exists_after_non_ascii_on_commit_drop_proves_no_name_at_all(
        string declared, string tail, string expected)
    {
        var sameBatch = _classifier.Classify(
            $"CREATE TEMP TABLE {declared} (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS items (id int); {tail}",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(sameBatch.Allowed);
        Assert.Equal(expected, sameBatch.Reason.ToString());

        var setup = _classifier.Classify(
            $"CREATE TEMP TABLE {declared} (id int) ON COMMIT DROP", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        var carried = _classifier.Classify(
            "CREATE TEMP TABLE IF NOT EXISTS items (id int); " + tail,
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(carried.Allowed);
        Assert.Equal(expected, carried.Reason.ToString());
    }

    [Fact]
    public void Final_Plain_create_is_still_proven_after_a_non_ascii_on_commit_drop_declaration()
    {
        // A plain CREATE TEMP TABLE fails while the name is taken, so reaching
        // the next statement means a new temp exists (contract row 011-T4b-D3).
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE İtems (id int) ON COMMIT DROP; CREATE TEMP TABLE items (id int); INSERT INTO items VALUES (1); TRUNCATE items",
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    // 011/final (M4): a CREATE TEMP TABLE whose name carries any qualifier other
    // than the pg_temp alias records no proof under its relation name. The
    // server rejects `public.x`; `pg_temp_3.x` may be another session's schema.
    [Theory]
    [InlineData("CREATE TEMP TABLE public.x (id int); INSERT INTO x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE public.x (id int); DELETE FROM x", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE public.x (id int); TRUNCATE x", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE public.x (id int); TRUNCATE pg_temp.x", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE public.x (id int); DROP TABLE x", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE public.x (id int); CREATE INDEX ix ON x (id)", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE IF NOT EXISTS public.x (id int); INSERT INTO x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE pg_temp_3.x (id int); INSERT INTO x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE pg_temp_3.x (id int); TRUNCATE x", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \"pg_temp_3\".x (id int); DROP TABLE x", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE \"PG_TEMP\".x (id int); INSERT INTO x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE appdb.pg_temp.x (id int); INSERT INTO x VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE appdb.public.x (id int); TRUNCATE x", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE public.x AS SELECT 1 AS id; INSERT INTO x VALUES (1)", "MutationNotAllowed")]
    // The withholding side of a qualified declaration is unchanged.
    [InlineData("CREATE TEMP TABLE x (id int); CREATE TEMP TABLE public.x (id int) ON COMMIT DROP; INSERT INTO x VALUES (1)", "MutationNotAllowed")]
    public void Final_Qualified_create_temp_records_no_proof_under_its_relation_name(string sql, string expected) =>
        AssertDeniedInBothUsages(sql, expected);

    [Theory]
    [InlineData("CREATE TEMP TABLE public.x (id int)")]
    [InlineData("CREATE TEMP TABLE pg_temp_3.x (id int)")]
    [InlineData("CREATE TEMP TABLE appdb.pg_temp.x (id int)")]
    public void Final_Qualified_create_temp_is_not_reported_as_a_session_temp(string sql)
    {
        // The declaration itself keeps its verdict; it only stops proving.
        var setup = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        Assert.Empty(setup.SessionTempTables);

        var carried = _classifier.Classify(
            "INSERT INTO x VALUES (1)", SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(carried.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, carried.Reason);
    }

    [Theory]
    // Decision: the two-part pg_temp alias keeps recording proof. The server
    // resolves pg_temp to the current session's temp schema, so the relation
    // created is exactly the session temp of that name.
    [InlineData("CREATE TEMP TABLE pg_temp.x (id int); INSERT INTO x VALUES (1); TRUNCATE x; DROP TABLE x")]
    [InlineData("CREATE TEMP TABLE PG_TEMP.x (id int); DELETE FROM x; TRUNCATE pg_temp.x")]
    [InlineData("CREATE TEMP TABLE \"pg_temp\".x (id int); CREATE INDEX ix ON x (id)")]
    public void Final_Pg_temp_qualified_create_temp_still_records_proof(string sql)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            var decision = _classifier.Classify(sql, usage, "appdb", false, null, Empty);
            Assert.True(decision.Allowed, decision.RejectionDescription);
            Assert.True(decision.HasSessionLocalWork);
            Assert.False(decision.HasMutation);
        }
    }

    // 011/T4b fix round 1 (F4, contract row 011-T4b-D3): a plain CREATE TEMP
    // TABLE (no IF NOT EXISTS) fails on the server while the name is taken, and
    // a failed statement ends the batch. Reaching the next statement therefore
    // means a new temp without ON COMMIT DROP exists, so proof is recorded again.
    [Theory]
    [InlineData("INSERT INTO items VALUES (1)")]
    [InlineData("UPDATE items SET id = 1")]
    [InlineData("DELETE FROM items")]
    [InlineData("CREATE INDEX ix ON items (id)")]
    [InlineData("TRUNCATE items")]
    [InlineData("DROP TABLE items")]
    [InlineData("CREATE TEMP TABLE IF NOT EXISTS items (id int); INSERT INTO items VALUES (1)")]
    public void T4b_Plain_create_after_on_commit_drop_of_the_same_name_records_proof_again(string tail)
    {
        var sameBatch = _classifier.Classify(
            "CREATE TEMP TABLE items (id int) ON COMMIT DROP; CREATE TEMP TABLE items (id int); " + tail,
            SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(sameBatch.Allowed, sameBatch.RejectionDescription);
        Assert.True(sameBatch.HasSessionLocalWork);
        Assert.False(sameBatch.HasMutation);

        var setup = _classifier.Classify(
            "CREATE TEMP TABLE items (id int) ON COMMIT DROP", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);
        var carried = _classifier.Classify(
            "CREATE TEMP TABLE items (id int); " + tail,
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(carried.Allowed, carried.RejectionDescription);
        Assert.True(carried.HasSessionLocalWork);
        Assert.False(carried.HasMutation);

        // Declared in setup, used in the query.
        var setupBoth = _classifier.Classify(
            "CREATE TEMP TABLE items (id int) ON COMMIT DROP; CREATE TEMP TABLE items (id int)",
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setupBoth.Allowed, setupBoth.RejectionDescription);
        Assert.Contains("items", setupBoth.SessionTempTables);
        var query = _classifier.Classify(tail, SqlUsage.Query, "appdb", false, null, setupBoth.SessionTempTables);
        Assert.True(query.Allowed, query.RejectionDescription);
        Assert.False(query.HasMutation);
    }

    [Fact]
    public void T4b_Plain_create_is_proven_after_a_long_on_commit_drop_declaration()
    {
        // Whatever name the truncated ON COMMIT DROP temp has, a plain create
        // that succeeds made a new temp.
        var setup = _classifier.Classify(
            ExpandNames("CREATE TEMP TABLE {A63}x (id int) ON COMMIT DROP"),
            SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);

        var query = _classifier.Classify(
            "CREATE TEMP TABLE items (id int); INSERT INTO items VALUES (1); TRUNCATE items",
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(query.Allowed, query.RejectionDescription);
        Assert.False(query.HasMutation);
    }

    // 011/final (I2): `pg_temp.<name>` always addresses the current session's
    // temp schema, whatever search_path says. TRUNCATE accepts that spelling
    // when <name> is a proven temp under the same proof as the unqualified form.
    [Theory]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE TABLE pg_temp.t")]
    [InlineData("Query", "CREATE TEMPORARY TABLE t (id int); TRUNCATE ONLY pg_temp.t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE PG_TEMP.t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE \"pg_temp\".t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t CONTINUE IDENTITY RESTRICT")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); CREATE TEMP TABLE u (id int); TRUNCATE pg_temp.t, u")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); CREATE TEMP TABLE u (id int); TRUNCATE pg_temp.t, pg_temp.u")]
    [InlineData("Query", "CREATE TEMP TABLE Items (id int); TRUNCATE pg_temp.ITEMS")]
    [InlineData("Query", "CREATE TEMP TABLE \"\u00E9\" (id int); TRUNCATE pg_temp.\"\u00E9\"")]
    [InlineData("Query", "CREATE TEMP TABLE {A63} (id int); TRUNCATE pg_temp.{A63}")]
    [InlineData("Query", "SELECT 1 AS id INTO TEMP TABLE t; TRUNCATE pg_temp.t")]
    [InlineData("Query", "CREATE TEMP TABLE pg_temp.t (id int); TRUNCATE pg_temp.t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int) ON COMMIT DROP; CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t")]
    [InlineData("Query", "CREATE TEMP TABLE t (id int); EXPLAIN ANALYZE TRUNCATE pg_temp.t")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); INSERT INTO t VALUES (1); TRUNCATE pg_temp.t")]
    [InlineData("CompareSetup", "CREATE TEMP TABLE t (id int); TRUNCATE ONLY pg_temp.t")]
    public void Final_Truncate_of_pg_temp_qualified_proven_temp_is_session_local(string usage, string sql)
    {
        var decision = _classifier.Classify(ExpandNames(sql), ParseUsage(usage), "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
    }

    [Fact]
    public void Final_Truncate_of_pg_temp_qualified_setup_temp_is_session_local()
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE t (id int)", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);

        var query = _classifier.Classify(
            "TRUNCATE pg_temp.t; INSERT INTO pg_temp.t VALUES (1)",
            SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(query.Allowed, query.RejectionDescription);
        Assert.True(query.HasSessionLocalWork);
        Assert.False(query.HasMutation);
        Assert.Contains("t", query.SessionTempTables);
    }

    // 011/final (I2): the widening is exactly one spelling. Every case below
    // was denied before the change and stays denied, with or without approval.
    [Theory]
    // pg_temp.<name> where <name> is not a proven temp.
    [InlineData("TRUNCATE pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.u", "NonTemporaryWrite")]
    [InlineData("TRUNCATE pg_temp.t; CREATE TEMP TABLE t (id int)", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; TRUNCATE pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS t (id int); TRUNCATE pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); DROP TABLE t; TRUNCATE pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); DROP TABLE pg_temp.\u00C9; TRUNCATE pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.\"T\"", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \"T\" (id int); TRUNCATE pg_temp.T", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); TRUNCATE pg_temp.\u00C9", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); TRUNCATE pg_temp.\u00C9", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); TRUNCATE pg_temp.\"\u00C9\"", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); TRUNCATE pg_temp.{A63}x", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE {A63} (id int); TRUNCATE pg_temp.{A63}x", "NonTemporaryWrite")]
    // Any other qualifier, also over a proven temp of that relation name.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE public.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp_3.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE \"pg_temp_3\".\"t\"", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE \"PG_TEMP\".t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp_.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_catalog.t", "NonTemporaryWrite")]
    // Three-part names, wherever pg_temp sits.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE appdb.pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE appdb.public.t", "NonTemporaryWrite")]
    // Mixed lists: one unproven target denies the statement.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t, items", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE items, pg_temp.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t, public.t", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t, pg_temp.u", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t, pg_temp_3.t", "NonTemporaryWrite")]
    // Options stay closed over the qualified spelling as well.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t CASCADE", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t RESTART IDENTITY", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE ONLY pg_temp.t RESTART IDENTITY CASCADE", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE TABLE pg_temp.t ON CLUSTER c", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int); EXPLAIN TRUNCATE pg_temp.t", "UnsupportedStatement")]
    [InlineData("CREATE TEMP TABLE t (id int); EXPLAIN ANALYZE TRUNCATE pg_temp.u", "NonTemporaryWrite")]
    public void Final_Truncate_schema_qualified_denials_are_unchanged(string sql, string expected)
    {
        foreach (var usage in new[] { SqlUsage.Query, SqlUsage.CompareSetup })
        {
            foreach (var approve in new[] { false, true })
            {
                var decision = _classifier.Classify(
                    ExpandNames(sql), usage, "appdb", approve, approve ? "appdb" : null, Empty);
                Assert.False(decision.Allowed);
                Assert.Equal(expected, decision.Reason.ToString());
            }
        }
    }

    [Fact]
    public void Final_Truncate_of_pg_temp_qualified_name_is_not_proven_by_a_caller_built_set()
    {
        var plain = new HashSet<string>(StringComparer.Ordinal) { "t" };
        foreach (var sql in new[] { "TRUNCATE pg_temp.t", "TRUNCATE ONLY pg_temp.t", "TRUNCATE t, pg_temp.t" })
        {
            var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", true, "appdb", plain);
            Assert.False(decision.Allowed);
            Assert.Equal(SqlSafetyReason.NonTemporaryWrite, decision.Reason);
        }
    }

    // 011/final (M11): "revoke every proof" also covers a name that only a
    // caller-built plain set supplied, and a new declaration proves it again.
    [Theory]
    [InlineData("DROP TABLE pg_temp.\u00C9; INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    [InlineData("DROP TABLE IF EXISTS pg_temp.\u00C9; DELETE FROM t", "MutationNotAllowed")]
    [InlineData("DROP TABLE pg_temp.\u00C9; CREATE INDEX ix ON t (id)", "UnsupportedStatement")]
    [InlineData("DROP TABLE pg_temp.\u00C9; DROP TABLE t", "UnsupportedStatement")]
    [InlineData("DROP TABLE pg_temp.{A63}y; INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    // A long ON COMMIT DROP declaration takes the same clear-all path.
    [InlineData("CREATE TEMP TABLE {A63}x (id int) ON COMMIT DROP; INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    public void Final_Clear_all_revokes_a_name_from_a_caller_built_set(string sql, string expected)
    {
        var plain = new HashSet<string>(StringComparer.Ordinal) { "t" };

        // The name proves the write before the clear-all statement is added.
        var before = _classifier.Classify("INSERT INTO t VALUES (1)", SqlUsage.Query, "appdb", false, null, plain);
        Assert.True(before.Allowed, before.RejectionDescription);

        var decision = _classifier.Classify(ExpandNames(sql), SqlUsage.Query, "appdb", false, null, plain);
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.Reason.ToString());

        // The caller's own set is never mutated.
        Assert.Equal(["t"], plain);
    }

    [Fact]
    public void Final_Clear_all_result_carries_no_name_and_a_new_declaration_proves_again()
    {
        var plain = new HashSet<string>(StringComparer.Ordinal) { "t", "u" };

        var drop = _classifier.Classify(
            "DROP TABLE pg_temp.\u00C9", SqlUsage.Query, "appdb", false, null, plain);
        Assert.True(drop.Allowed, drop.RejectionDescription);
        Assert.Empty(drop.SessionTempTables);

        var later = _classifier.Classify(
            "INSERT INTO u VALUES (1)", SqlUsage.Query, "appdb", false, null, drop.SessionTempTables);
        Assert.False(later.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, later.Reason);

        var redeclared = _classifier.Classify(
            "DROP TABLE pg_temp.\u00C9; CREATE TEMP TABLE t (id int); INSERT INTO t VALUES (1); TRUNCATE t",
            SqlUsage.Query, "appdb", false, null, plain);
        Assert.True(redeclared.Allowed, redeclared.RejectionDescription);
        Assert.True(redeclared.HasSessionLocalWork);
        Assert.False(redeclared.HasMutation);
        Assert.Equal(["t"], redeclared.SessionTempTables);

        // Only the redeclared name came back.
        var other = _classifier.Classify(
            "DROP TABLE pg_temp.\u00C9; CREATE TEMP TABLE t (id int); INSERT INTO u VALUES (1)",
            SqlUsage.Query, "appdb", false, null, plain);
        Assert.False(other.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, other.Reason);
    }

    // 011/final (M12): evidence that the Unicode-lower-case Forget in
    // SessionTemps.Record is live code, not vestigial. An unquoted non-ASCII
    // declaration revokes the proof of the quoted name it lower-cases to.
    // Stricter than a UTF-8 server needs; removing the call would turn this
    // denial into an allow.
    [Theory]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); CREATE TEMP TABLE \u00C9 (id int); INSERT INTO \"\u00E9\" VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \"\u00E9\" (id int); CREATE TEMP TABLE IF NOT EXISTS \u00C9 (id int); TRUNCATE \"\u00E9\"", "NonTemporaryWrite")]
    public void Final_Unquoted_non_ascii_declaration_revokes_the_quoted_name_it_lower_cases_to(
        string sql, string expected) =>
        AssertDeniedInBothUsages(sql, expected);

    // 011/final (M9): a write denied in a flow that declared a TEMP table
    // without proof carries a fixed hint, so an agent does not ask for mutation
    // approval for what was meant as a temp write. Reason and exit code are
    // unchanged; the hint never echoes SQL.
    private const string UnprovenTempHint =
        "A TEMP table declared in this session flow is not a proven session temp " +
        "(unquoted non-ASCII name, name over 63 UTF-8 bytes, ON COMMIT DROP, or a schema other than pg_temp). " +
        "If it is the write target, quote or shorten its name and keep it past commit; mutation approval is not the remedy.";

    [Theory]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); INSERT INTO \u00C9 VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE {A63}x (id int); DELETE FROM {A63}x", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; UPDATE t SET id = 1", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; CREATE TEMP TABLE IF NOT EXISTS t (id int); INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE public.t (id int); INSERT INTO t VALUES (1)", "MutationNotAllowed")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); TRUNCATE \u00C9", "NonTemporaryWrite")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; TRUNCATE pg_temp.t", "NonTemporaryWrite")]
    public void Final_Write_denied_after_an_unproven_temp_declaration_carries_a_hint(string sql, string expected)
    {
        var expanded = ExpandNames(sql);
        var query = _classifier.Classify(expanded, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());
        Assert.Equal(UnprovenTempHint, query.Detail);
        Assert.Equal($"{expected}. {UnprovenTempHint}", query.RejectionDescription);
        Assert.DoesNotContain("\u00C9", query.RejectionDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", query.RejectionDescription, StringComparison.Ordinal);

        var setup = _classifier.Classify(expanded, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.False(setup.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, setup.Reason);
        Assert.Equal(UnprovenTempHint, setup.Detail);
    }

    [Fact]
    public void Final_Write_denied_after_a_carried_on_commit_drop_declaration_carries_the_hint()
    {
        var setup = _classifier.Classify(
            "CREATE TEMP TABLE t (id int) ON COMMIT DROP", SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.True(setup.Allowed, setup.RejectionDescription);

        var query = _classifier.Classify(
            "INSERT INTO t VALUES (1)", SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.False(query.Allowed);
        Assert.Equal(SqlSafetyReason.MutationNotAllowed, query.Reason);
        Assert.Equal(UnprovenTempHint, query.Detail);
    }

    [Theory]
    // No unproven declaration in the flow: the text is what it was.
    [InlineData("INSERT INTO items VALUES (1)", "MutationNotAllowed.")]
    [InlineData("TRUNCATE items", "NonTemporaryWrite.")]
    [InlineData("CREATE TEMP TABLE t (id int); INSERT INTO items VALUES (1)", "MutationNotAllowed.")]
    [InlineData("CREATE TEMP TABLE t (id int); DROP TABLE t; INSERT INTO t VALUES (1)", "MutationNotAllowed.")]
    // Other reasons never carry the hint, even after an unproven declaration.
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); DROP TABLE \u00C9", "UnsupportedStatement.")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); CREATE INDEX ix ON \u00C9 (id)", "UnsupportedStatement.")]
    [InlineData("CREATE TEMP TABLE t (id int) ON COMMIT DROP; TRUNCATE t CASCADE", "UnsupportedStatement.")]
    [InlineData("CREATE TEMP TABLE \u00C9 (id int); SELECT 1 INTO public.x", "SelectIntoNotAllowed.")]
    public void Final_Denial_text_is_unchanged_without_an_unproven_temp_write(string sql, string expected)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(decision.Allowed);
        Assert.Null(decision.Detail);
        Assert.Equal(expected, decision.RejectionDescription);
    }

    [Fact]
    public void Final_Approved_mutation_after_an_unproven_temp_declaration_is_still_an_ordinary_mutation()
    {
        // The hint changes no verdict: with approval the write is a mutation.
        var decision = _classifier.Classify(
            "CREATE TEMP TABLE \u00C9 (id int); INSERT INTO \u00C9 VALUES (1)",
            SqlUsage.Query, "appdb", true, "appdb", Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasMutation);
        Assert.Null(decision.Detail);
    }

    // Query usage without approval, then compare setup, where an unproven DML
    // target is reported as NonTemporaryWrite.
    private void AssertDeniedInBothUsages(string sql, string expected)
    {
        var query = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.False(query.Allowed);
        Assert.Equal(expected, query.Reason.ToString());

        var setup = _classifier.Classify(sql, SqlUsage.CompareSetup, "appdb", false, null, Empty);
        Assert.False(setup.Allowed);
        Assert.Equal(
            expected == "MutationNotAllowed" ? "NonTemporaryWrite" : expected,
            setup.Reason.ToString());
    }

    private static string ExpandNames(string sql) => sql
        .Replace("{A63}", new string('a', 63), StringComparison.Ordinal)
        .Replace("{E32}", new string('é', 32), StringComparison.Ordinal)
        .Replace("{E31}", new string('é', 31), StringComparison.Ordinal);

    private static SqlUsage ParseUsage(string usage) => usage switch
    {
        "Query" => SqlUsage.Query,
        "CompareSetup" => SqlUsage.CompareSetup,
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "Expected Query or CompareSetup."),
    };

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
}