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
    // A qualifier is never proof, even when the relation name matches a tracked temp:
    // only the unqualified name is what the session flow recorded.
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp_3.t")]
    [InlineData("CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t")]
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
    // Guard: the stricter identifier rule is local to TRUNCATE. The shared
    // session-local helper keeps its existing fold for DML / DROP / CREATE INDEX.
    [InlineData("CREATE TEMP TABLE \"é\" (id int); INSERT INTO É VALUES (1)")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); CREATE INDEX ix ON É (id)")]
    [InlineData("CREATE TEMP TABLE \"é\" (id int); DROP TABLE É")]
    [InlineData("CREATE TEMP TABLE É (id int); DELETE FROM \"é\"")]
    public void T4_Non_truncate_non_ascii_fold_verdicts_are_unchanged(string sql)
    {
        var decision = _classifier.Classify(sql, SqlUsage.Query, "appdb", false, null, Empty);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.True(decision.HasSessionLocalWork);
        Assert.False(decision.HasMutation);
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
        Assert.Contains("items", setup.SessionTempTables);

        var truncate = _classifier.Classify(
            "TRUNCATE items", SqlUsage.Query, "appdb", true, "appdb", setup.SessionTempTables);
        Assert.False(truncate.Allowed);
        Assert.Equal(SqlSafetyReason.NonTemporaryWrite, truncate.Reason);

        // Guard: the non-TRUNCATE verdict over the same carried name is unchanged.
        var insert = _classifier.Classify(
            "INSERT INTO items VALUES (1)", SqlUsage.Query, "appdb", false, null, setup.SessionTempTables);
        Assert.True(insert.Allowed, insert.RejectionDescription);
        Assert.False(insert.HasMutation);
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

    private static SqlUsage ParseUsage(string usage) => usage switch
    {
        "Query" => SqlUsage.Query,
        "CompareSetup" => SqlUsage.CompareSetup,
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "Expected Query or CompareSetup."),
    };

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
}