using Npgsql;

using SqlHarness.Core;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Tests.Integration;

public sealed class PostgresReadOnlyRuntimeIntegrationTests
{
    private const string ConfirmationVariable = PostgresIntegrationFactAttribute.DisposableTargetConfirmationVariable;

    [PostgresIntegrationFact(requireDisposableTargetConfirmation: true)]
    [Trait("Category", "PostgresIntegration")]
    public async Task Runtime_enforces_read_only_default_and_recovers_after_rejection_and_disposal()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.Variable)!;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.Equal(builder.Database, Environment.GetEnvironmentVariable(ConfirmationVariable), StringComparer.Ordinal);

        var schema = "sqlharness_runtime_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var session = new NpgsqlSession(connection, new SessionMessageBuffer(), (_, _) => { });
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {schema}");
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.effects (id integer NOT NULL)");
            await ExecuteAsync(connection, $"""
                CREATE FUNCTION {schema}.write_probe() RETURNS integer
                LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO {schema}.effects (id) VALUES (1);
                    RETURN 1;
                END
                $$
                """);

            var failure = await Assert.ThrowsAsync<SqlHarnessSafetyException>(async () =>
            {
                await using var rejected = await session.ExecuteReaderAsync(
                    new SqlExecutionCommand($"SELECT {schema}.write_probe()", [], 10), CancellationToken.None);
                while (await rejected.ReadAsync(CancellationToken.None)) { }
                while (await rejected.NextResultAsync(CancellationToken.None))
                    while (await rejected.ReadAsync(CancellationToken.None)) { }
            });
            Assert.Equal(PostgresReadOnlyFailure.RejectionMessage, failure.Message);
            Assert.Equal(0L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {schema}.effects"));

            var setupTable = "sqlharness_setup_" + Guid.NewGuid().ToString("N");
            await using (var setup = await session.ExecuteReaderAsync(
                new SqlExecutionCommand($"CREATE TEMP TABLE {setupTable} (id integer); INSERT INTO {setupTable} VALUES (0)", [], 10)
                { PostgresMode = PostgresExecutionMode.SessionSetup }, CancellationToken.None))
                await DrainAsync(setup);

            var incomplete = await session.ExecuteReaderAsync(
                new SqlExecutionCommand($"UPDATE pg_temp.{setupTable} SET id = 1 RETURNING id", [], 10), CancellationToken.None);
            Assert.True(await incomplete.ReadAsync(CancellationToken.None));
            await incomplete.DisposeAsync();
            Assert.Equal(0, await ReadSessionScalarAsync<int>(session, $"SELECT id FROM pg_temp.{setupTable}"));

            await using (var completed = await session.ExecuteReaderAsync(
                new SqlExecutionCommand($"UPDATE pg_temp.{setupTable} SET id = 2 RETURNING id", [], 10), CancellationToken.None))
                await DrainAsync(completed);
            Assert.Equal(2, await ReadSessionScalarAsync<int>(session, $"SELECT id FROM pg_temp.{setupTable}"));

            await using (var skippedResult = await session.ExecuteReaderAsync(
                new SqlExecutionCommand($"UPDATE pg_temp.{setupTable} SET id = 3 RETURNING id; SELECT 99", [], 10), CancellationToken.None))
            {
                Assert.True(await skippedResult.NextResultAsync(CancellationToken.None));
                Assert.True(await skippedResult.ReadAsync(CancellationToken.None));
                Assert.Equal(99, skippedResult.GetValue(0));
                Assert.False(await skippedResult.ReadAsync(CancellationToken.None));
                Assert.False(await skippedResult.NextResultAsync(CancellationToken.None));
            }
            Assert.Equal(2, await ReadSessionScalarAsync<int>(session, $"SELECT id FROM pg_temp.{setupTable}"));

            await using (var approved = await session.ExecuteReaderAsync(
                new SqlExecutionCommand($"SELECT {schema}.write_probe()", [], 10)
                { PostgresMode = PostgresExecutionMode.ApprovedMutation }, CancellationToken.None))
            {
                Assert.True(await approved.ReadAsync(CancellationToken.None));
                Assert.Equal(1, approved.GetValue(0));
                Assert.False(await approved.ReadAsync(CancellationToken.None));
                Assert.False(await approved.NextResultAsync(CancellationToken.None));
            }

            var truncated = await session.ExecuteReaderAsync(
                new SqlExecutionCommand("SELECT generate_series(1, 5)", [], 10), CancellationToken.None);
            Assert.True(await truncated.ReadAsync(CancellationToken.None));
            await truncated.DisposeAsync();

            Assert.Equal(1, await ReadSessionScalarAsync<int>(session, "SELECT 1"));
            Assert.Equal(1L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {schema}.effects"));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static async Task<T> ReadSessionScalarAsync<T>(ISqlSession session, string sql)
    {
        await using var reader = await session.ExecuteReaderAsync(new SqlExecutionCommand(sql, [], 10), CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        var value = (T)Convert.ChangeType(reader.GetValue(0), typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(await reader.ReadAsync(CancellationToken.None));
        Assert.False(await reader.NextResultAsync(CancellationToken.None));
        return value;
    }

    private static async Task DrainAsync(ISqlReader reader)
    {
        do
        {
            while (await reader.ReadAsync(CancellationToken.None)) { }
        } while (await reader.NextResultAsync(CancellationToken.None));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}