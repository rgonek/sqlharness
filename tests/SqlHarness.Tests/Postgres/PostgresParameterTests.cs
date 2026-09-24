using System.Data;

using Npgsql;

using NpgsqlTypes;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Postgres;

public sealed class PostgresParameterTests
{
    [Theory]
    [InlineData("amount:money=1.23")]
    [InlineData("amount:smallmoney=1.23")]
    [InlineData("when:smalldatetime=2026-07-29T12:00:00")]
    [InlineData("path:hierarchyid=/1/2/")]
    [InlineData("loc:geography=4326;POINT(0 0)")]
    [InlineData("shape:geometry=0;POINT(0 0)")]
    public void Rejects_sql_server_only_types(string input)
    {
        var parsed = SqlParameterParser.Parse([input]);
        var error = Assert.Throws<SqlHarnessSafetyException>(() => PostgresParameters.Validate(parsed));
        Assert.DoesNotContain("1.23", error.Message);
    }

    [Fact]
    public void Accepts_mapped_types()
    {
        var parsed = SqlParameterParser.Parse([
            "n:int=1",
            "u:uniqueidentifier=0f8fad5b-d9cb-469f-a165-70867728950e",
            "t:nvarchar=witaj",
            "d:datetime2=2026-07-29T12:00:00",
            "flag:bit=true",
            "tiny:tinyint=255",
            "bin:varbinary=AQID",
        ]);
        PostgresParameters.Validate(parsed);
    }

    [Fact]
    public void Tinyint_256_is_rejected()
    {
        // Shared parser already constrains tinyint to byte; Validate still guards out-of-range values.
        IReadOnlyList<SqlHarnessParameter> parsed =
        [
            new("@tiny", SqlDbType.TinyInt, 256, null),
        ];
        Assert.Throws<SqlHarnessSafetyException>(() => PostgresParameters.Validate(parsed));
    }

    [Fact]
    public void Bind_maps_types_onto_npgsql_command()
    {
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var at = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Unspecified);
        var offset = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var parsed = SqlParameterParser.Parse([
            "n:int=1",
            "tiny:tinyint=255",
            $"u:uniqueidentifier={id:D}",
            "flag:bit=true",
            "t:nvarchar=witaj",
            "bin:varbinary=AQID",
            "d:datetime2=2026-07-29T12:00:00",
            "at:datetimeoffset=2026-07-29T12:00:00Z",
            "missing:int:null",
        ]);

        using var command = new NpgsqlCommand();
        NpgsqlSession.BindCommand(command, new SqlExecutionCommand("SELECT @n", parsed, 30));

        Assert.Equal("SELECT @n", command.CommandText);
        Assert.Equal(NpgsqlDbType.Integer, command.Parameters["@n"].NpgsqlDbType);
        Assert.Equal(1, command.Parameters["@n"].Value);
        Assert.Equal(NpgsqlDbType.Smallint, command.Parameters["@tiny"].NpgsqlDbType);
        Assert.Equal((short)255, command.Parameters["@tiny"].Value);
        Assert.IsType<short>(command.Parameters["@tiny"].Value);
        Assert.Equal(NpgsqlDbType.Uuid, command.Parameters["@u"].NpgsqlDbType);
        Assert.Equal(id, command.Parameters["@u"].Value);
        Assert.Equal(NpgsqlDbType.Boolean, command.Parameters["@flag"].NpgsqlDbType);
        Assert.Equal(true, command.Parameters["@flag"].Value);
        Assert.Equal(NpgsqlDbType.Text, command.Parameters["@t"].NpgsqlDbType);
        Assert.Equal("witaj", command.Parameters["@t"].Value);
        Assert.Equal(NpgsqlDbType.Bytea, command.Parameters["@bin"].NpgsqlDbType);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(command.Parameters["@bin"].Value));
        Assert.Equal(NpgsqlDbType.Timestamp, command.Parameters["@d"].NpgsqlDbType);
        Assert.Equal(at, command.Parameters["@d"].Value);
        Assert.Equal(NpgsqlDbType.TimestampTz, command.Parameters["@at"].NpgsqlDbType);
        Assert.Equal(offset, command.Parameters["@at"].Value);
        Assert.Equal(NpgsqlDbType.Integer, command.Parameters["@missing"].NpgsqlDbType);
        Assert.Equal(DBNull.Value, command.Parameters["@missing"].Value);
    }

    [Fact]
    public async Task Query_carries_parsed_parameters_for_postgres()
    {
        const string sql = "SELECT @id AS id";
        var session = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["id"], [1]));
        var module = new SqlHarnessModule(session, new FakeGain(), Profiles);

        var outcome = await module.ExecuteAsync(new SqlHarnessQueryOperation(
            new SqlTargetRequest("local-pg", new Dictionary<string, string>()),
            sql,
            ["id:int=1"],
            30,
            50,
            false,
            null));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var command = Assert.Single(session.Commands);
        Assert.Equal(sql, command.Sql);
        var parameter = Assert.Single(command.Parameters);
        Assert.Equal("@id", parameter.Name);
        Assert.Equal(SqlDbType.Int, parameter.Type);
        Assert.Equal(1, parameter.Value);
    }

    [Fact]
    public async Task Query_rejects_money_parameter_on_postgres_before_execution()
    {
        const string sql = "SELECT @amount AS amount";
        var session = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["amount"], [1.23m]));
        var module = new SqlHarnessModule(session, new FakeGain(), Profiles);

        var outcome = await module.ExecuteAsync(new SqlHarnessQueryOperation(
            new SqlTargetRequest("local-pg", new Dictionary<string, string>()),
            sql,
            ["amount:money=1.23"],
            30,
            50,
            false,
            null));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(session.Commands);
        Assert.DoesNotContain("1.23", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["local-pg"] = new(
                "localhost,5432",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

    private sealed class FakeGain : IGainStore
    {
        public void Append(GainRecord record) { }
        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSession : ISqlSessionFactory, ISqlSession
    {
        private readonly Queue<Func<ISqlReader>> _results;

        private FakeSession(IEnumerable<Func<ISqlReader>> results) => _results = new(results);

        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } = null!;

        public static FakeSession WithIdentity(string server, string database, FakeReader reader) =>
            new([() => reader])
            {
                Identity = new(server, database, server, database, "profile", Engine: "postgres"),
            };

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            if (!string.Equals(target.Database, Identity.ActualDatabase, StringComparison.Ordinal))
                throw new SqlTargetMismatchException("Connected SQL target identity does not match the resolved target.");
            return Task.FromResult<ISqlSession>(this);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Dequeue()());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader(string[] names, object?[][] rows) : ISqlReader
    {
        private int _position = -1;
        public int FieldCount => names.Length;
        public int RecordsAffected => -1;
        public static FakeReader Rows(string[] names, params object?[][] rows) => new(names, rows);
        public string GetName(int ordinal) => names[ordinal];
        public Type GetFieldType(int ordinal) => rows.FirstOrDefault()?[ordinal]?.GetType() ?? typeof(object);
        public bool GetAllowNull(int ordinal) => rows.Any(row => row[ordinal] is null or DBNull);
        public object GetValue(int ordinal) => rows[_position][ordinal] ?? DBNull.Value;
        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++_position < rows.Length);
        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}