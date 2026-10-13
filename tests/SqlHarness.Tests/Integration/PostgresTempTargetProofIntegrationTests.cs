using Npgsql;

using SqlHarness.Core;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Integration;

/// <summary>
/// Plan 047, step 3, live proof of the read-only boundary: an unqualified
/// PostgreSQL write is no longer judged offline by name, so the server decides
/// whether the target is this session's temp or a persistent table.
///
/// Every object lives in a per-run schema created and dropped by the test, and
/// every temp table dies with its own connection. The session factory below is
/// the only way the tests shape a connection: it opens a physical session
/// (pooling off, so a temp never leaks into the next test), applies the search
/// path the case is about, and can declare a session temp before the module
/// connects through it.
/// </summary>
public sealed class PostgresTempTargetProofIntegrationTests
{
    private const string ProfileName = "integration";

    /// <summary>Over the server's 63-byte identifier limit, so unprovable offline (047).</summary>
    private const string LongUnprovableTempName = "p47i_long_temp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>Unquoted non-ASCII, so unprovable offline (047).</summary>
    private const string NonAsciiUnprovableTempName = "p47i_temp_\u00e9_rows";

    [PostgresIntegrationFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Temp_dml_on_an_unprovable_name_runs_read_only_and_writes_the_row()
    {
        await using var proof = await PostgresProofTarget.CreateAsync();
        try
        {
            foreach (var tempName in new[] { LongUnprovableTempName, NonAsciiUnprovableTempName })
            {
                // The declaration is made on the session the module connects to,
                // because the batch itself cannot declare it: query SQL runs inside
                // SET TRANSACTION READ ONLY, where CREATE TEMP is refused with
                // 25006, and the read-write setup phase still requires the offline
                // name proof this case is about. Only the write under test runs
                // through the module.
                var module = proof.Module("public, pg_temp", sessionPrelude: $"CREATE TEMP TABLE {tempName} (id int)");

                var outcome = await module.ExecuteAsync(Query($"INSERT INTO {tempName} VALUES (1); SELECT count(*) AS written FROM {tempName}"));

                Assert.True(
                    outcome.ExitCode == SqlHarnessExitCode.Success,
                    $"Unqualified temp DML over '{tempName}' returned {outcome.ExitCode}: {outcome.SafeError}");
                var report = Assert.IsType<SqlHarnessQueryReport>(outcome.Report);
                Assert.Equal("session-local", report.StatementClassification);
                var rows = Assert.Single(Assert.Single(report.ResultSets).Rows);
                Assert.Equal(1L, Convert.ToInt64(rows[0], System.Globalization.CultureInfo.InvariantCulture));

                // The declaration itself is accepted offline and the temp outlives the
                // read-only transaction, so a setup phase can create one and a measured
                // SELECT reads it. That module has no session prelude: the setup phase
                // makes the declaration itself.
                var measured = await proof.Module("public, pg_temp").ExecuteAsync(Measure(
                    $"CREATE TEMP TABLE {tempName} (id int)",
                    $"SELECT count(*) AS written FROM {tempName}"));
                Assert.True(
                    measured.ExitCode == SqlHarnessExitCode.Success,
                    $"A setup phase that declares '{tempName}' returned {measured.ExitCode}: {measured.SafeError}");
                var measureReport = Assert.IsType<SqlHarnessMeasureReport>(measured.Report);
                Assert.True(measureReport.ResultsStable);
                Assert.Equal(2, measureReport.MeasuredRunCount);
            }
        }
        finally
        {
            await proof.DisposeAsync();
        }
    }

    [PostgresIntegrationFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Unqualified_write_onto_a_persistent_table_is_rejected_at_execution_and_writes_no_row()
    {
        await using var proof = await PostgresProofTarget.CreateAsync();
        try
        {
            await proof.CreatePersistentTable(proof.TableName, seedRows: 1);
            // No temp of that name exists in this session, so the unqualified name
            // resolves to the persistent table.
            var module = proof.Module(searchPath: $"{proof.Schema}, public, pg_temp");

            var outcome = await module.ExecuteAsync(Query($"INSERT INTO {proof.TableName} VALUES (99)"));

            Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
            Assert.Null(outcome.Report);
            Assert.Contains("read-only transaction", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(proof.TableName, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(1L, await proof.PersistentRows(proof.TableName));
        }
        finally
        {
            await proof.DisposeAsync();
        }
    }

    [PostgresIntegrationFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Search_path_order_decides_whether_the_unqualified_write_lands_on_the_temp_or_the_table()
    {
        const string tableName = "p47i_shadow_rows";

        await using var proof = await PostgresProofTarget.CreateAsync();
        try
        {
            await proof.CreatePersistentTable(tableName, seedRows: 1);
            var sql = $"INSERT INTO {tableName} VALUES (2); SELECT count(*) AS written FROM {tableName}";

            // Persistent schema ahead of pg_temp: the unqualified write is a
            // persistent write and the read-only transaction refuses it.
            var shadowed = await proof
                .Module(searchPath: $"{proof.Schema}, public, pg_temp")
                .ExecuteAsync(Query(sql));
            Assert.Equal(SqlHarnessExitCode.Safety, shadowed.ExitCode);
            Assert.Contains("read-only transaction", shadowed.SafeError ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(1L, await proof.PersistentRows(tableName));

            // pg_temp first: the same statement resolves to the session temp, which
            // the read-only transaction allows.
            var shadowing = await proof
                .Module(searchPath: $"pg_temp, {proof.Schema}, public", sessionPrelude: $"CREATE TEMP TABLE {tableName} (id int)")
                .ExecuteAsync(Query(sql));
            Assert.Equal(SqlHarnessExitCode.Success, shadowing.ExitCode);
            var report = Assert.IsType<SqlHarnessQueryReport>(shadowing.Report);
            Assert.Equal("session-local", report.StatementClassification);
            var rows = Assert.Single(Assert.Single(report.ResultSets).Rows);
            Assert.Equal(1L, Convert.ToInt64(rows[0], System.Globalization.CultureInfo.InvariantCulture));

            // The row went to the temp; the persistent table never grew.
            Assert.Equal(1L, await proof.PersistentRows(tableName));
        }
        finally
        {
            await proof.DisposeAsync();
        }
    }

    [PostgresIntegrationFact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Setup_ddl_on_proven_temps_still_runs_before_the_measured_select()
    {
        await using var proof = await PostgresProofTarget.CreateAsync();
        try
        {
            // 047 step 3 changed the query phase only; the read-write setup phase
            // still requires a proven temp for every target.
            var module = proof.Module(searchPath: "public, pg_temp");
            var setup = """
                CREATE TEMP TABLE p47i_indexed_rows (id int);
                INSERT INTO p47i_indexed_rows VALUES (1);
                CREATE INDEX p47i_indexed_rows_ix ON p47i_indexed_rows (id);
                TRUNCATE p47i_indexed_rows;
                DROP TABLE p47i_indexed_rows;
                CREATE TEMP TABLE p47i_kept_rows (id int);
                INSERT INTO p47i_kept_rows VALUES (7);
                """;

            var outcome = await module.ExecuteAsync(Measure(setup, "SELECT id FROM p47i_kept_rows"));

            Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
            var report = Assert.IsType<SqlHarnessMeasureReport>(outcome.Report);
            Assert.True(report.ResultsStable);
            Assert.Equal(2, report.MeasuredRunCount);
        }
        finally
        {
            await proof.DisposeAsync();
        }
    }

    private static SqlHarnessQueryOperation Query(string sql) =>
        new(new SqlTargetRequest(ProfileName, new Dictionary<string, string>()), sql, [], 30, 50, false, null);

    private static SqlHarnessMeasureOperation Measure(string setup, string candidate) =>
        new(new SqlTargetRequest(ProfileName, new Dictionary<string, string>()), setup, candidate, [], 30, 2);

    /// <summary>One disposable target: its own schema on the confirmed database, dropped on dispose.</summary>
    private sealed class PostgresProofTarget : IAsyncDisposable
    {
        private readonly NpgsqlConnection _admin;
        private readonly TempDirectory _artifacts;
        private readonly Dictionary<string, TargetProfile> _profiles;
        private readonly string _connectionString;

        private PostgresProofTarget(NpgsqlConnection admin, TempDirectory artifacts, string connectionString, NpgsqlConnectionStringBuilder builder)
        {
            _admin = admin;
            _artifacts = artifacts;
            _connectionString = connectionString;
            Schema = "p47i_" + Guid.NewGuid().ToString("N")[..8];
            TableName = "p47i_rows";
            _profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                [ProfileName] = new(
                    $"{builder.Host},{builder.Port}",
                    builder.Database!,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "sql",
                    // The disposable cluster authenticates by trust, so no password
                    // is read: the tests never build a connection string from it.
                    SqlUser: builder.Username,
                    PasswordEnvVar: "SQLHARNESS_PG_INTEGRATION_PASSWORD",
                    TrustServerCertificate: true,
                    Engine: "postgres"),
            };
        }

        internal string Schema { get; }

        internal string TableName { get; }

        internal static async Task<PostgresProofTarget> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.Variable);
            Assert.False(string.IsNullOrWhiteSpace(connectionString), $"Set {PostgresIntegrationFactAttribute.Variable} to opt in.");

            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                // A pooled connection can carry another test's temp tables.
                Pooling = false,
            };
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "The opt-in connection string names no database.");
            var confirmed = Environment.GetEnvironmentVariable(PostgresIntegrationFactAttribute.DisposableTargetConfirmationVariable);
            if (!string.IsNullOrWhiteSpace(confirmed))
            {
                Assert.Equal(builder.Database, confirmed, StringComparer.Ordinal);
            }

            var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            var proof = new PostgresProofTarget(admin, new TempDirectory(), connectionString, builder);
            await proof.ExecuteAsync($"CREATE SCHEMA {proof.Schema}");
            return proof;
        }

        internal SqlHarnessModule Module(string searchPath, string? sessionPrelude = null)
        {
            var factory = new ProofSessionFactory(_connectionString, searchPath, sessionPrelude);
            return new SqlHarnessModule(
                factory,
                new CompareArtifactWriter(_artifacts.Path, () => DateTimeOffset.UtcNow),
                () => _profiles);
        }

        internal async Task CreatePersistentTable(string name, long seedRows)
        {
            await ExecuteAsync($"CREATE TABLE {Schema}.{name} (id bigint NOT NULL)");
            for (var index = 0L; index < seedRows; index++)
                await ExecuteAsync($"INSERT INTO {Schema}.{name} (id) VALUES ({index})");
        }

        internal Task<long> PersistentRows(string name) =>
            ScalarAsync($"SELECT count(*) FROM {Schema}.{name}");

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_admin.State == System.Data.ConnectionState.Open)
                    await ExecuteAsync($"DROP SCHEMA IF EXISTS {Schema} CASCADE");
            }
            finally
            {
                await _admin.DisposeAsync();
                _artifacts.Dispose();
            }
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _admin);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<long> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _admin);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }

    /// <summary>
    /// The proof's session. It owns the physical connection so a case can shape
    /// <c>search_path</c> and declare a session temp before the module runs; the
    /// module's connection string is never modified.
    /// </summary>
    private sealed class ProofSessionFactory(string connectionString, string searchPath, string? sessionPrelude)
        : ISqlSessionFactory
    {
        public async Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                // A pooled connection can carry another test's temp tables.
                Pooling = false,
                SearchPath = searchPath,
            };
            var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            if (sessionPrelude is not null)
            {
                await using var command = new NpgsqlCommand(sessionPrelude, connection);
                await command.ExecuteNonQueryAsync(ct);
            }

            var messages = new SessionMessageBuffer();
            NoticeEventHandler handler = (_, _) => { };
            connection.Notice += handler;
            return new NpgsqlSession(connection, messages, handler)
            {
                Identity = new SqlHarnessTargetIdentityReport(
                    target.Server,
                    target.Database,
                    target.Server,
                    target.Database,
                    target.Mode,
                    Engine: "postgres"),
            };
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sqlharness-pg47i-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a disposable artifact root.
            }
        }
    }
}