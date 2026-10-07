using System.Data;

using Npgsql;

using NpgsqlTypes;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Dialect;
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

    // 012/T1 review gap: the typed path (no declaration text) must hit the
    // same rejection through PostgresDialect.BindParameters, Core's one binder
    // for the dialect, not only through the legacy-text PostgresParameters.Validate call above.
    [Fact]
    public void Typed_path_rejects_a_sql_server_only_type_through_bind_parameters()
    {
        var error = Assert.Throws<SqlHarnessSafetyException>(() => SqlDialects.For(SqlEngine.Postgres)
            .BindParameters([new SqlHarnessParameterInput("amount", "money", "1.23")]));

        Assert.Equal("SQL parameter type 'Money' is not supported on Postgres.", error.Message);
        Assert.DoesNotContain("1.23", error.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("SELECT @n::int")]
    [InlineData("SELECT @n LIMIT 1")]
    [InlineData("SELECT @doc->'a'")]
    [InlineData("SELECT @doc ->> 'key'")]
    [InlineData("SELECT @doc @> '{\"a\":1}'")]
    [InlineData("SELECT @n, $$hello$$")]
    public async Task Query_accepts_postgres_placeholders_the_tsql_parser_rejects(string sql)
    {
        var session = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["value"], [1]));
        var module = new SqlHarnessModule(session, new FakeGain(), Profiles);
        var parameter = sql.Contains("@doc", StringComparison.Ordinal) ? "doc:int=1" : "n:int=1";

        var outcome = await module.ExecuteAsync(new SqlHarnessQueryOperation(
            new SqlTargetRequest("local-pg", new Dictionary<string, string>()),
            sql,
            [parameter],
            30,
            50,
            false,
            null));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal(sql, Assert.Single(session.Commands).Sql);
    }

    [Theory]
    [InlineData("SELECT @n::int")]
    [InlineData("SELECT @n LIMIT 1")]
    [InlineData("SELECT @doc->'a'")]
    [InlineData("SELECT @doc ->> 'key'")]
    [InlineData("SELECT @doc @> '{\"a\":1}'")]
    [InlineData("SELECT @doc ? 'a'")]
    [InlineData("SELECT @n, $$hello$$")]
    [InlineData("SELECT @n::int, @n, @N")]
    [InlineData("SELECT :n::int")]
    [InlineData("SELECT @n$extra")]
    public void Dialect_accepts_postgres_placeholder_syntax(string sql)
    {
        var parameter = sql.Contains("@doc", StringComparison.Ordinal) ? "doc:int=1" : "n:int=1";
        Accept(sql, parameter);
    }

    [Fact]
    public void Dialect_matches_a_name_without_regard_to_case()
    {
        Accept("SELECT @ID LIMIT 1", "id:int=1");
    }

    [Fact]
    public void Dialect_accepts_a_parameter_used_only_in_setup_or_one_variant()
    {
        AcceptAcross("seed:int=1", "id:int=2", "SELECT @seed", "SELECT @id::int");
        AcceptAcross("only:int=1", "shared:int=2", "SELECT @only::int, @shared", "SELECT @shared LIMIT 1");
    }

    [Fact]
    public void Dialect_ignores_placeholders_inside_comments_strings_and_dollar_quotes()
    {
        const string sql = """
            SELECT @n::int -- @unused
            /* @unused */
            , '@unused'
            , $$ @unused $$
            , $tag$ @unused $tag$
            , "@unused"
            , E'@unused'
            """;

        Accept(sql, "n:int=1");
        var exception = Reject(sql, "n:int=1", "unused:int=2");
        Assert.Contains("@unused", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not referenced", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dialect_does_not_treat_abs_or_a_dotted_name_as_a_shorter_placeholder()
    {
        var spaced = Reject("SELECT @ n", "n:int=1");
        Assert.Contains("@n", spaced.Message, StringComparison.Ordinal);

        var dotted = Reject("SELECT @a.b", "a:int=1");
        Assert.Contains("@a", dotted.Message, StringComparison.Ordinal);

        var positional = Reject("SELECT $1", "n:int=1");
        Assert.Contains("@n", positional.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dialect_rejects_an_unreferenced_parameter_and_unparsed_sql()
    {
        var unused = Reject("SELECT @n LIMIT 1", "n:int=1", "extra:int=2");
        Assert.Contains("@extra", unused.Message, StringComparison.Ordinal);

        var syntax = Reject("SELECT @n FROM (", "n:int=1");
        Assert.Equal("SQL parameter references could not be parsed.", syntax.Message);
    }

    [Fact]
    public void Dialect_does_not_parse_sql_that_has_no_parameters()
    {
        SqlDialects.For(SqlEngine.Postgres).ValidateParameterReferences([], "SELECT * FROM (");
    }

    [Fact]
    public async Task Compare_and_measure_use_the_postgres_reference_scan()
    {
        const string sql = "SELECT @n::int; SELECT @n";
        var compare = await Execute(new SqlHarnessCompareOperation(
            Target(),
            null,
            sql,
            sql,
            ["n:int=1"],
            30,
            1));
        Assert.Contains("Measured SQL must be a single SELECT", compare.SafeError, StringComparison.Ordinal);

        var measure = await Execute(new SqlHarnessMeasureOperation(
            Target(),
            null,
            sql,
            ["n:int=1"],
            30,
            1));
        Assert.Contains("Measured SQL must be a single SELECT", measure.SafeError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matrix_measure_sets_watch_and_snapshot_use_the_postgres_reference_scan()
    {
        var matrixSession = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["value"], [1]));
        var matrix = await Execute(new SqlHarnessCompareMatrixOperation(
            Target(),
            null,
            "SELECT @n::int",
            "SELECT @n::int",
            [],
            30,
            1,
            "n:int=1,2"), matrixSession);
        Assert.NotEmpty(matrixSession.Commands);
        Assert.DoesNotContain("could not be parsed", matrix.SafeError ?? string.Empty, StringComparison.Ordinal);

        var setSession = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["value"], [1]));
        var sets = await Execute(new SqlHarnessMeasureOperation(
            Target(),
            "SELECT @seed",
            "SELECT @id::int",
            ["seed:int=1"],
            30,
            1,
            [new("small", ["id:int=1"]), new("large", ["id:int=2"])]), setSession);
        Assert.NotEmpty(setSession.Commands);
        Assert.DoesNotContain("could not be parsed", sets.SafeError ?? string.Empty, StringComparison.Ordinal);

        var watch = await Execute(new SqlHarnessWatchOperation(
            Target(),
            "SELECT @n::int",
            ["n:int=1"],
            30,
            1,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            "not a predicate",
            null));
        Assert.Contains("Watch condition predicate is invalid.", watch.SafeError, StringComparison.Ordinal);

        var snapshots = new RecordingSnapshotStore();
        var snapshotSession = FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["value"], [1]));
        var snapshot = await new SqlHarnessModule(snapshotSession, new FakeGain(), Profiles, snapshotStore: snapshots)
            .ExecuteAsync(new SqlHarnessSnapshotOperation(
                Target(),
                "SELECT @n LIMIT 1",
                ["n:int=1"],
                30,
                1,
                "before",
                false,
                false));
        Assert.True(snapshot.ExitCode == SqlHarnessExitCode.Success, snapshot.SafeError);
        Assert.Equal(1, snapshots.Saves);
        Assert.Equal("SELECT @n LIMIT 1", Assert.Single(snapshotSession.Commands).Sql);
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

    private static SqlTargetRequest Target() =>
        new("local-pg", new Dictionary<string, string>());

    private static void Accept(string sql, params string[] parameters) =>
        SqlDialects.For(SqlEngine.Postgres).ValidateParameterReferences(SqlParameterParser.Parse(parameters), sql);

    private static void AcceptAcross(string left, string right, params string?[] batches) =>
        SqlDialects.For(SqlEngine.Postgres).ValidateParameterReferences(
            SqlParameterParser.Parse([left, right]),
            batches);

    private static SqlHarnessSafetyException Reject(string sql, params string[] parameters) =>
        Assert.Throws<SqlHarnessSafetyException>(() =>
            SqlDialects.For(SqlEngine.Postgres).ValidateParameterReferences(SqlParameterParser.Parse(parameters), sql));

    private static Task<SqlHarnessOutcome> Execute(SqlHarnessOperation operation, FakeSession? session = null) =>
        new SqlHarnessModule(session ?? FakeSession.WithIdentity("localhost", "appdb", FakeReader.Rows(["value"], [1])), new FakeGain(), Profiles)
            .ExecuteAsync(operation);

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

    private sealed class RecordingSnapshotStore : ISnapshotStore
    {
        public int Saves { get; private set; }

        public void Save(string name, SnapshotDocument document, bool force) => Saves++;

        public SnapshotDocument Load(string name) => throw new NotSupportedException();
    }

    private sealed class FakeGain : IGainSource
    {
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