using System.Data;

using Microsoft.Data.SqlClient;

using SqlHarness.Core;
using SqlHarness.Core.Auth;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class SpaceTests
{
    private const string ProfileSecret = "tenant-space-secret-never-emit";
    private const string PasswordSecret = "Password=space-hunter2-never-emit";

    [Fact]
    public async Task Space_executes_once_on_verified_target()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.IsType<SqlHarnessSpaceReport>(outcome.Report);
        Assert.Single(session.Commands);
        Assert.Equal(SpaceQuery.Sql, session.Commands[0].Sql);
    }

    [Fact]
    public async Task Space_report_maps_files_allocation_tables_and_identity()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        var report = Assert.IsType<SqlHarnessSpaceReport>(outcome.Report);
        Assert.Same(session.Identity, report.Target);
        Assert.Equal("Primary", Assert.Single(report.Files).LogicalName);
        Assert.Equal(100m, report.Allocation.ReservedMb);
        Assert.Equal("Contracts", Assert.Single(report.Tables).Name);
        Assert.Empty(report.Indexes);
    }

    [Fact]
    public async Task Space_with_object_binds_schema_name_and_returns_indexes()
    {
        var session = SpaceFixture.Session(objectMatches: 1);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 40, "dbo.Contracts", 20));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var report = Assert.IsType<SqlHarnessSpaceReport>(outcome.Report);
        Assert.Equal("IX_Contracts_Date", Assert.Single(report.Indexes).Index);

        var command = Assert.Single(session.Commands);
        Assert.Equal(SpaceQuery.Sql, command.Sql);
        Assert.Equal(20, command.TimeoutSeconds);
        Assert.Collection(command.Parameters,
            p =>
            {
                Assert.Equal("@top", p.Name);
                Assert.Equal(SqlDbType.Int, p.Type);
                Assert.Equal(40, p.Value);
            },
            p =>
            {
                Assert.Equal("@objectSchema", p.Name);
                Assert.Equal("dbo", p.Value);
            },
            p =>
            {
                Assert.Equal("@objectName", p.Name);
                Assert.Equal("Contracts", p.Value);
            });
    }

    [Fact]
    public async Task Space_unqualified_object_binds_name_only()
    {
        var session = SpaceFixture.Session(objectMatches: 1);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, "Contracts", 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var command = Assert.Single(session.Commands);
        Assert.Equal(DBNull.Value, Assert.Single(command.Parameters, p => p.Name == "@objectSchema").Value);
        Assert.Equal("Contracts", Assert.Single(command.Parameters, p => p.Name == "@objectName").Value);
    }

    [Fact]
    public async Task Space_without_object_binds_null_filters()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        var command = Assert.Single(session.Commands);
        Assert.Equal(DBNull.Value, Assert.Single(command.Parameters, p => p.Name == "@objectSchema").Value);
        Assert.Equal(DBNull.Value, Assert.Single(command.Parameters, p => p.Name == "@objectName").Value);
    }

    [Fact]
    public async Task Space_object_not_found_exits_safety()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, "MissingTable", 30));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("not found or was ambiguous", outcome.SafeError, StringComparison.OrdinalIgnoreCase);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task Space_object_ambiguous_exits_safety()
    {
        var session = SpaceFixture.Session(objectMatches: 2);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, "Contracts", 30));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Contains("not found or was ambiguous", outcome.SafeError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a.b.c")]
    [InlineData("a.b.c.d")]
    [InlineData(".Runs")]
    [InlineData("audit.")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Space_rejects_invalid_object_specs_without_executing(string objectSpec)
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, objectSpec, 30));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Empty(session.Commands);
        Assert.NotNull(outcome.SafeError);
    }

    [Fact]
    public async Task Space_validation_before_authentication_rejects_bounds_without_connect()
    {
        var session = SpaceFixture.Session(objectMatches: 0);

        var badTimeout = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 0));
        Assert.Equal(SqlHarnessExitCode.Safety, badTimeout.ExitCode);
        Assert.Empty(session.Commands);
        Assert.Equal(0, session.ConnectCalls);

        var badTop = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 0, null, 30));
        Assert.Equal(SqlHarnessExitCode.Safety, badTop.ExitCode);
        Assert.Empty(session.Commands);
        Assert.Equal(0, session.ConnectCalls);

        var badTopHigh = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 501, null, 30));
        Assert.Equal(SqlHarnessExitCode.Safety, badTopHigh.ExitCode);
        Assert.Empty(session.Commands);

        var badTimeoutHigh = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 301));
        Assert.Equal(SqlHarnessExitCode.Safety, badTimeoutHigh.ExitCode);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Space_authentication_failure_maps_to_three_without_probe()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        session.ConnectFailure = new AzureCliException("not logged in");

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.Authentication, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Space_target_mismatch_maps_to_four_without_probe()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        session.ConnectFailure = new SqlTargetMismatchException("mismatch");

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.TargetMismatch, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Space_sql_failure_maps_to_five()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        session.ExecuteFailure = FakeSqlException();

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.Equal(SpaceQuery.Sql, Assert.Single(session.Commands).Sql);
    }

    [Fact]
    public async Task Space_sql_and_errors_never_include_profile_variables_or_passwords()
    {
        Assert.DoesNotContain(ProfileSecret, SpaceQuery.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", SpaceQuery.Sql, StringComparison.OrdinalIgnoreCase);

        var session = SpaceFixture.Session(objectMatches: 0);
        session.ExecuteFailure = new InvalidOperationException(
            $"Login failed {PasswordSecret}; var={ProfileSecret}");
        var target = new SqlTargetRequest(
            "test",
            new Dictionary<string, string> { ["tenant"] = ProfileSecret });

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(target, 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(ProfileSecret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordSecret, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Space_object_name_is_redacted_from_errors()
    {
        const string secretObject = "secret-table-name-xyz";
        var session = SpaceFixture.Session(objectMatches: 0);
        session.ExecuteFailure = new InvalidOperationException($"failed for {secretObject}");

        var outcome = await Module(session).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, secretObject, 30));

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.DoesNotContain(secretObject, outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", outcome.SafeError ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Space_success_captures_nonzero_raw_footprint_on_receipt()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var gain = new FakeGain();

        var outcome = await Module(session, gain).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(10, 1));
        var record = Assert.Single(gain.Records);
        Assert.Equal("space", record.Command);
        Assert.True(record.RawBytes > 0);
        Assert.True(record.RawLines > 0);
    }

    [Fact]
    public async Task Space_records_gain_with_space_command()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var gain = new FakeGain();

        var outcome = await Module(session, gain).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Empty(gain.Records);
        await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(20, 2));
        Assert.Equal("space", Assert.Single(gain.Records).Command);
        Assert.True(Assert.Single(gain.Records).Success);
    }

    [Fact]
    public async Task Space_receipt_maps_gain_storage_failure_to_local_storage()
    {
        var session = SpaceFixture.Session(objectMatches: 0);
        var gain = new FakeGain(new IOException("disk full"));

        var outcome = await Module(session, gain).ExecuteAsync(
            new SqlHarnessSpaceOperation(Target(), 25, null, 30));

        var completion = await Assert.IsType<SqlHarnessEmissionReceipt>(outcome.EmissionReceipt)
            .CompleteAsync(new OutputFootprint(8, 1));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, completion);
    }

    private static SqlHarnessModule Module(FakeSession session, FakeGain? gain = null) =>
        new(session, gain ?? new FakeGain(), Profiles);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["tenant"] = ProfileSecret });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new(
                "server",
                "db-{tenant}",
                new Dictionary<string, string> { ["tenant"] = "^.+$" },
                "integrated"),
        };

    private static SqlException FakeSqlException() =>
        (SqlException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SqlException));

    private sealed class FakeGain(Exception? appendFailure = null) : IGainStore
    {
        public List<GainRecord> Records { get; } = [];
        public void Append(GainRecord record)
        {
            if (appendFailure is not null)
                throw appendFailure;
            Records.Add(record);
        }

        public SqlHarnessGainReport Aggregate() => throw new NotSupportedException();
    }

    private sealed class FakeSession(ISqlReader reader) : ISqlSessionFactory, ISqlSession
    {
        public Exception? ConnectFailure { get; set; }
        public Exception? ExecuteFailure { get; set; }
        public int ConnectCalls { get; private set; }
        public List<SqlExecutionCommand> Commands { get; } = [];
        public IReadOnlyList<string> Messages => [];
        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("server", "db", "server", "db", "profile");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            ConnectCalls++;
            return ConnectFailure is null
                ? Task.FromResult<ISqlSession>(this)
                : Task.FromException<ISqlSession>(ConnectFailure);
        }

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (ExecuteFailure is not null)
                return Task.FromException<ISqlReader>(ExecuteFailure);
            return Task.FromResult(reader);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReader(params object?[][][] sets) : ISqlReader
    {
        private int _set;
        private int _row;

        private string[] Names => sets[_set][0].Cast<string>().ToArray();
        private object?[][] Rows => sets[_set].Skip(1).ToArray();

        public int FieldCount => Names.Length;
        public int RecordsAffected => -1;
        public string GetName(int ordinal) => Names[ordinal];
        public Type GetFieldType(int ordinal) => typeof(object);
        public bool GetAllowNull(int ordinal) => true;
        public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class SpaceFixture
    {
        public static FakeSession Session(long objectMatches) =>
            new(Reader(objectMatches, includeIndex: objectMatches == 1));

        public static ISqlReader Reader(long objectMatches, bool includeIndex = true)
        {
            return new FakeReader(
                Set(["MatchCount"], [objectMatches]),
                Set(
                    ["LogicalName", "Type", "PhysicalName", "SizeMb", "UsedMb", "FreeMb"],
                    ["Primary", "ROWS", @"C:\data.mdf", 200m, 80m, 120m]),
                Set(
                    ["ReservedMb", "UsedMb", "DataMb"],
                    [100m, 80m, 60m]),
                Set(
                    ["SchemaName", "ObjectName", "Rows", "ReservedMb", "UsedMb", "DataMb"],
                    ["dbo", "Contracts", 42L, 50m, 40m, 30m]),
                includeIndex
                    ? Set(
                        ["SchemaName", "TableName", "IndexName", "Type", "ReservedMb", "UsedMb", "DataMb", "Compression"],
                        ["dbo", "Contracts", "IX_Contracts_Date", "NONCLUSTERED", 12m, 10m, 8m, "PAGE"])
                    : Set(
                        ["SchemaName", "TableName", "IndexName", "Type", "ReservedMb", "UsedMb", "DataMb", "Compression"]));
        }

        private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];
    }
}