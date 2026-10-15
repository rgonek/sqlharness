using SqlHarness.Core;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresSafetyTierTests
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
    private readonly PostgresSafetyClassifier _classifier = new();

    [Theory]
    [InlineData("UPDATE public.orders SET total = 0 WHERE id = 1", SqlRiskTier.Dml)]
    [InlineData("DROP TABLE public.orders", SqlRiskTier.Schema)]
    [InlineData("TRUNCATE public.orders", SqlRiskTier.Schema)]
    [InlineData("TRUNCATE orders", SqlRiskTier.Schema)]
    [InlineData("CREATE INDEX ix_orders_id ON public.orders (id)", SqlRiskTier.Schema)]
    [InlineData("ALTER TABLE public.orders ADD COLUMN note text", SqlRiskTier.Schema)]
    [InlineData("CREATE VIEW public.order_totals AS SELECT 1 AS total", SqlRiskTier.Schema)]
    [InlineData("CREATE SCHEMA app", SqlRiskTier.Schema)]
    [InlineData("CREATE SEQUENCE app.order_ids", SqlRiskTier.Schema)]
    [InlineData("CREATE FUNCTION app.answer() RETURNS int LANGUAGE sql AS 'SELECT 42'", SqlRiskTier.Schema)]
    [InlineData("CREATE TYPE app.mood AS ENUM ('sad', 'ok')", SqlRiskTier.Schema)]
    [InlineData("CREATE TRIGGER order_change AFTER INSERT ON public.orders FOR EACH ROW EXECUTE FUNCTION app.on_order_change()", SqlRiskTier.Schema)]
    [InlineData("ANALYZE TABLE public.orders", SqlRiskTier.Schema)]
    [InlineData("SELECT nextval('orders_id_seq')", SqlRiskTier.Schema)]
    [InlineData("GRANT SELECT ON public.orders TO reader", SqlRiskTier.Privilege)]
    [InlineData("CREATE ROLE app_reader", SqlRiskTier.Privilege)]
    [InlineData("ALTER ROLE app_reader LOGIN", SqlRiskTier.Privilege)]
    [InlineData("DROP ROLE app_reader", SqlRiskTier.Privilege)]
    [InlineData("CALL public.rebuild_totals(1)", SqlRiskTier.Opaque)]
    [InlineData("EXECUTE prepared_update", SqlRiskTier.Opaque)]
    [InlineData("CREATE TEMP TABLE t (id int); DROP TABLE t;", SqlRiskTier.Session)]
    public void Mutation_usage_assigns_postgres_tiers(string sql, object expected)
    {
        var decision = Classify(sql, SqlUsage.Mutation);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
    }

    [Fact]
    public void Grant_reports_folded_grantee() =>
        Assert.Equal(["reader"], Classify("GRANT SELECT ON public.orders TO Reader", SqlUsage.Mutation).Grantees);

    [Fact]
    public void Grant_and_revoke_report_folded_grantees_in_source_order()
    {
        var decision = Classify(
            "GRANT SELECT ON public.orders TO App_User, \"Reader\"; REVOKE SELECT ON public.orders FROM App_User",
            SqlUsage.Mutation);
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal(SqlRiskTier.Privilege, decision.Tier);
        Assert.Equal(["app_user", "Reader", "app_user"], decision.Grantees);
    }

    [Fact]
    public void Create_and_alter_role_nodes_report_folded_target_principals()
    {
        var create = Classify("CREATE ROLE New_User", SqlUsage.Mutation);
        Assert.True(create.Allowed, create.RejectionDescription);
        Assert.Equal(SqlRiskTier.Privilege, create.Tier);
        Assert.Equal(["new_user"], create.Grantees);

        var alter = Classify("ALTER ROLE Existing_User LOGIN", SqlUsage.Mutation);
        Assert.True(alter.Allowed, alter.RejectionDescription);
        Assert.Equal(SqlRiskTier.Privilege, alter.Tier);
        Assert.Equal(["existing_user"], alter.Grantees);
    }

    [Theory]
    [InlineData("CREATE EXTENSION dblink")]
    [InlineData("COPY public.orders FROM PROGRAM 'cat x'")]
    [InlineData("SET ROLE postgres")]
    [InlineData("CREATE DATABASE other")]
    [InlineData("DROP DATABASE other")]
    [InlineData("SELECT pg_terminate_backend(1)")]
    [InlineData("LOAD 'extension' ")]
    [InlineData("LISTEN orders_changed")]
    [InlineData("NOTIFY orders_changed")]
    [InlineData("SET application_name = 'mutation test'")]
    [InlineData("PREPARE p AS SELECT 1")]
    [InlineData("DEALLOCATE p")]
    public void Rejected_under_mutation(string sql) =>
        Assert.False(Classify(sql, SqlUsage.Mutation).Allowed);

    [Fact]
    public void Do_block_remains_a_parse_error_when_parser_has_no_do_statement() =>
        Assert.Equal(SqlSafetyReason.ParseError, Classify("DO $$ BEGIN PERFORM 1; END $$", SqlUsage.Mutation).Reason);

    [Theory]
    [InlineData("ALTER VIEW public.order_totals RENAME TO order_totals_v2")]
    [InlineData("CREATE PROCEDURE app.refresh_orders() LANGUAGE sql AS 'SELECT 1'")]
    [InlineData("ANALYZE public.orders")]
    [InlineData("CREATE USER New_User")]
    [InlineData("ALTER USER Existing_User WITH LOGIN")]
    public void Valid_postgres_utility_forms_missing_from_parser_remain_parse_errors(string sql) =>
        Assert.Equal(SqlSafetyReason.ParseError, Classify(sql, SqlUsage.Mutation).Reason);

    [Theory]
    [InlineData("DROP TABLE public.orders")]
    [InlineData("GRANT SELECT ON public.orders TO reader")]
    public void Query_usage_keeps_schema_and_privilege_rejected(string sql)
    {
        var decision = Classify(sql, SqlUsage.Query);
        Assert.False(decision.Allowed);
        Assert.Equal(SqlSafetyReason.UnsupportedStatement, decision.Reason);
    }

    [Fact]
    public void Mutation_requires_read_write_temp_name_proof() =>
        Assert.Equal(SqlRiskTier.Session,
            Classify("CREATE TEMP TABLE t (id int); INSERT INTO t VALUES (1)", SqlUsage.Mutation).Tier);

    [Fact]
    public void Unproven_unqualified_target_is_dml_under_read_write_mutation() =>
        Assert.Equal(SqlRiskTier.Dml, Classify("UPDATE t SET id = 1", SqlUsage.Mutation).Tier);

    [Theory]
    [InlineData("SELECT 1", "Query", SqlRiskTier.ReadOnly)]
    [InlineData("SELECT nextval('s')", "Query", SqlRiskTier.Schema)]
    [InlineData("CREATE TEMP TABLE t (id int)", "Query", SqlRiskTier.Session)]
    [InlineData("CREATE TEMP TABLE t (id int)", "CompareSetup", SqlRiskTier.Session)]
    public void Existing_usages_gain_tier_without_changing_allowance(string sql, string usage, object expected)
    {
        var decision = Classify(sql, Enum.Parse<SqlUsage>(usage));
        Assert.True(decision.Allowed, decision.RejectionDescription);
        Assert.Equal((SqlRiskTier)expected, decision.Tier);
    }

    private SqlSafetyDecision Classify(string sql, SqlUsage usage) =>
        _classifier.Classify(sql, usage, "appdb", false, null, Empty);
}