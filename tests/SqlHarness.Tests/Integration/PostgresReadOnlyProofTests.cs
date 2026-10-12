using System.Diagnostics;
using System.Text;

using Npgsql;

namespace SqlHarness.Tests.Integration;

public sealed class PostgresReadOnlyProofTests
{
    private const string ConnectionVariable = "SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING";
    private const string ConfirmationVariable = "SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE";
    private const string ReportVariable = "SQLHARNESS_PG_READONLY_PROOF_REPORT";

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Server_enforces_read_only_transaction_proof()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString), $"Set {ConnectionVariable} to opt in.");

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.Database));
        var confirmedDatabase = Environment.GetEnvironmentVariable(ConfirmationVariable);
        Assert.Equal(builder.Database, confirmedDatabase, StringComparer.Ordinal);

        var reportPath = Environment.GetEnvironmentVariable(ReportVariable);
        Assert.False(string.IsNullOrWhiteSpace(reportPath), $"Run scripts/prove-postgres-readonly.ps1 to set {ReportVariable}.");

        var schema = "sqlharness_readonly_proof_" + Guid.NewGuid().ToString("N");
        var records = new List<ProofRecord>();
        string? version = null;
        string? cleanup = null;
        var timing = "not measured";

        await using var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            version = await connection.ExecuteScalarAsync<string>("SELECT version()");

            await connection.ExecuteNonQueryAsync($"CREATE SCHEMA {schema}");
            await connection.ExecuteNonQueryAsync($"CREATE TABLE {schema}.effects (id integer NOT NULL)");
            await connection.ExecuteNonQueryAsync($"CREATE SEQUENCE {schema}.proof_seq");
            await connection.ExecuteNonQueryAsync($"""
                CREATE FUNCTION {schema}.write_probe() RETURNS integer
                LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO {schema}.effects (id) VALUES (1);
                    RETURN 1;
                END
                $$
                """);

            var uncontained = await RunAsync(connection, $"SELECT {schema}.write_probe()", readOnly: false);
            records.Add(uncontained with { Name = "Function write outside READ ONLY transaction" });
            var initialRows = await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {schema}.effects");
            records.Add(new ProofRecord("Uncontained persistent effect observed", "PASS", null, $"rows={initialRows}"));
            Assert.Equal(1L, initialRows);

            records.Add((await RunAsync(connection, $"SELECT {schema}.write_probe()", readOnly: true)) with
            {
                Name = "Function persistent write inside READ ONLY transaction"
            });
            var rowsAfterRejectedWrite = await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {schema}.effects");
            records.Add(new ProofRecord("Persistent table unchanged after read-only rejection",
                rowsAfterRejectedWrite == initialRows ? "PASS" : "FAIL", null, $"rows={rowsAfterRejectedWrite}"));
            records.Add((await RunAsync(connection, $"SELECT pg_stat_statements_reset()", readOnly: true)) with
            {
                Name = "pg_stat_statements_reset() inside READ ONLY transaction"
            });
            records.Add((await RunAsync(connection, $"SELECT nextval('{schema}.proof_seq')", readOnly: true)) with
            {
                Name = "nextval() on persistent sequence inside READ ONLY transaction"
            });
            records.Add((await RunAsync(connection, "SELECT pg_promote()", readOnly: true)) with
            {
                Name = "pg_promote() inside READ ONLY transaction"
            });

            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using (var setReadOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                    await setReadOnly.ExecuteNonQueryAsync();
                await using (var setDefault = new NpgsqlCommand(
                    "SELECT set_config('default_transaction_read_only', 'off', false)", connection, transaction))
                    await setDefault.ExecuteScalarAsync();
                await transaction.CommitAsync();
            }

            records.Add((await RunAsync(connection, $"SELECT {schema}.write_probe()", readOnly: true)) with
            {
                Name = "Next per-command READ ONLY transaction after default changed to off"
            });

            await connection.ExecuteNonQueryAsync("CREATE TEMP TABLE sqlharness_readonly_temp (id integer NOT NULL)");
            records.Add(await RunAsync(connection, "INSERT INTO pg_temp.sqlharness_readonly_temp VALUES (1)", readOnly: true));
            records.Add(await RunAsync(connection, "UPDATE pg_temp.sqlharness_readonly_temp SET id = 2 WHERE id = 1", readOnly: true));
            records.Add(await RunAsync(connection, "DELETE FROM pg_temp.sqlharness_readonly_temp WHERE id = 2", readOnly: true));
            records.Add(await RunAsync(connection,
                "EXPLAIN (ANALYZE, COSTS OFF) INSERT INTO pg_temp.sqlharness_readonly_temp VALUES (3)", readOnly: true));
            records.Add(await RunAsync(connection,
                "EXPLAIN (ANALYZE, COSTS OFF) UPDATE pg_temp.sqlharness_readonly_temp SET id = 4 WHERE id = 3", readOnly: true));
            records.Add(await RunAsync(connection,
                "EXPLAIN (ANALYZE, COSTS OFF) DELETE FROM pg_temp.sqlharness_readonly_temp WHERE id = 4", readOnly: true));
            records.Add((await RunAsync(connection,
                $"EXPLAIN (ANALYZE, COSTS OFF) SELECT {schema}.write_probe()", readOnly: true)) with
            {
                Name = "EXPLAIN ANALYZE function persistent write inside READ ONLY transaction"
            });

            timing = await MeasureWrapperAsync(connection);
        }
        catch (Exception exception)
        {
            records.Add(new ProofRecord("Proof execution", "ERROR", GetSqlState(exception), exception.GetType().Name));
            throw new InvalidOperationException("Proof execution failed; consult the sanitized evidence report.");
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await connection.ExecuteNonQueryAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                    cleanup = "PASS: proof schema dropped; temporary table removed with session";
                }
                catch (Exception exception)
                {
                    cleanup = $"FAILED: {exception.GetType().Name} (SQLSTATE {GetSqlState(exception) ?? "none"})";
                    records.Add(new ProofRecord("Cleanup", "FAILED", GetSqlState(exception), exception.GetType().Name));
                }
            }

            WriteReport(reportPath!, builder, version, records, timing, cleanup);
        }

        Assert.Contains(records, record => record.Name == "Function persistent write inside READ ONLY transaction" && record.SqlState == "25006");
        Assert.Contains(records, record => record.Name == "Next per-command READ ONLY transaction after default changed to off" && record.SqlState == "25006");
        Assert.Contains(records, record => record.Name == "SELECT nextval('" + schema + ".proof_seq')" && record.SqlState == "25006");
        Assert.Contains(records, record => record.Name == "EXPLAIN (ANALYZE, COSTS OFF) SELECT " + schema + ".write_probe()" && record.SqlState == "25006");
        Assert.Contains(records, record => record.Name.StartsWith("INSERT INTO pg_temp.", StringComparison.Ordinal) && record.Outcome == "PASS");
        Assert.Contains(records, record => record.Name.StartsWith("UPDATE pg_temp.", StringComparison.Ordinal) && record.Outcome == "PASS");
        Assert.Contains(records, record => record.Name.StartsWith("DELETE FROM pg_temp.", StringComparison.Ordinal) && record.Outcome == "PASS");
        Assert.Equal(3, records.Count(record => record.Name.StartsWith("EXPLAIN (ANALYZE, COSTS OFF)", StringComparison.Ordinal) && record.Name.Contains("pg_temp.", StringComparison.Ordinal) && record.Outcome == "PASS"));
        Assert.Equal("PASS: proof schema dropped; temporary table removed with session", cleanup);
    }

    private static async Task<ProofRecord> RunAsync(NpgsqlConnection connection, string sql, bool readOnly)
    {
        var name = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (!readOnly)
        {
            try
            {
                await using var uncontained = new NpgsqlCommand(sql, connection);
                await uncontained.ExecuteScalarAsync();
                return new ProofRecord(name, "PASS", null, "executed without a transaction wrapper");
            }
            catch (Exception exception)
            {
                return new ProofRecord(name, "REJECTED", GetSqlState(exception), exception.GetType().Name);
            }
        }

        await using var transaction = await connection.BeginTransactionAsync();
        string? returnValue = null;
        try
        {
            await using (var setReadOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await setReadOnly.ExecuteNonQueryAsync();

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            if (sql.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase))
            {
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { }
            }
            else if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var scalar = await command.ExecuteScalarAsync();
                returnValue = scalar is null or DBNull ? null : $"returned: {scalar}";
            }
            else
                returnValue = $"affected rows: {await command.ExecuteNonQueryAsync()}";

            await transaction.CommitAsync();
            return new ProofRecord(name, "PASS", null, returnValue ?? "READ ONLY transaction committed");
        }
        catch (Exception exception)
        {
            try { await transaction.RollbackAsync(); }
            catch { /* Preserve the original SQLSTATE in the recorded result. */ }
            return new ProofRecord(name, "REJECTED", GetSqlState(exception), exception.GetType().Name);
        }
    }

    private static async Task<string> MeasureWrapperAsync(NpgsqlConnection connection)
    {
        const int repetitions = 100;
        var bare = Stopwatch.StartNew();
        for (var index = 0; index < repetitions; index++)
            await connection.ExecuteScalarAsync<int>("SELECT 1");
        bare.Stop();

        var wrapped = Stopwatch.StartNew();
        for (var index = 0; index < repetitions; index++)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var setReadOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await setReadOnly.ExecuteNonQueryAsync();
            await using (var command = new NpgsqlCommand("SELECT 1", connection, transaction))
                await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
        }
        wrapped.Stop();

        var barePerRun = bare.Elapsed.TotalMilliseconds / repetitions;
        var wrappedPerRun = wrapped.Elapsed.TotalMilliseconds / repetitions;
        var percent = barePerRun == 0 ? 0 : ((wrappedPerRun - barePerRun) / barePerRun) * 100;
        return $"{repetitions} repetitions each; bare={bare.Elapsed.TotalMilliseconds:F3} ms total ({barePerRun:F3} ms/run), " +
               $"READ ONLY wrapper={wrapped.Elapsed.TotalMilliseconds:F3} ms total ({wrappedPerRun:F3} ms/run), " +
               $"overhead={percent:F1}% vs bare";
    }

    private static string? GetSqlState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgresException)
                return postgresException.SqlState;
        return null;
    }

    private static void WriteReport(
        string path,
        NpgsqlConnectionStringBuilder connection,
        string? version,
        IReadOnlyCollection<ProofRecord> records,
        string timing,
        string? cleanup)
    {
        var report = new StringBuilder();
        report.AppendLine("# Plan 052 PostgreSQL read-only proof");
        report.AppendLine();
        report.AppendLine("> Evidence captured by an explicitly confirmed disposable-target run. Review each outcome before treating step 1 as proven.");
        report.AppendLine();
        report.AppendLine($"- Evidence status: {(version is null ? "INCOMPLETE" : "EXECUTED; interpretation pending")}");
        report.AppendLine($"- PostgreSQL target: `{Escape(connection.Host)}:{connection.Port}/{Escape(connection.Database)}`");
        report.AppendLine($"- PostgreSQL version: `{Escape(version ?? "not connected")}`");
        report.AppendLine("- Documentation: [PostgreSQL `SET TRANSACTION`](https://www.postgresql.org/docs/current/sql-set-transaction.html), section `SET TRANSACTION`.");
        report.AppendLine();
        report.AppendLine("| Case | Outcome | SQLSTATE | Detail |");
        report.AppendLine("|---|---|---|---|");
        foreach (var record in records)
            report.AppendLine($"| {Escape(record.Name)} | {Escape(record.Outcome)} | {Escape(record.SqlState ?? "none")} | {Escape(record.Detail)} |");
        report.AppendLine();
        report.AppendLine("## Wrapper timing");
        report.AppendLine();
        report.AppendLine(timing);
        report.AppendLine();
        report.AppendLine("## Cleanup");
        report.AppendLine();
        report.AppendLine(cleanup ?? "Not completed.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, report.ToString());
    }

    private static string Escape(string? value) => (value ?? "unknown").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private sealed record ProofRecord(string Name, string Outcome, string? SqlState, string Detail);
}

internal static class NpgsqlProofExtensions
{
    public static async Task<T> ExecuteScalarAsync<T>(this NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public static async Task ExecuteNonQueryAsync(this NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
