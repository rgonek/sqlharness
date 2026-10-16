using System.Data;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class IndexInventoryCaptureTests
{
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private const string PasswordEnv = "SQLHARNESS_PLAN063_INVENTORY_PASSWORD";
    private const string Password = "inventory-catalog-secret";

    private static readonly string[] ObjectColumns = ["slot", "object_found"];
    private static readonly string[] IndexColumns =
    [
        "slot", "index_id", "name", "type_desc", "is_unique", "is_primary_key",
        "is_disabled", "is_hypothetical", "has_filter",
    ];
    private static readonly string[] ColumnColumns =
    [
        "slot", "index_id", "column_name", "key_ordinal", "is_included_column",
    ];

    [Fact]
    public void Inventory_batch_passes_the_query_classifier()
    {
        var decision = SqlDialects.For(SqlEngine.SqlServer).Classify(
            IndexInventoryQuery.Sql,
            SqlUsage.Query,
            "db",
            false,
            null,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.True(decision.Allowed, decision.RejectionDescription);
    }

    [Fact]
    public void Hinted_tables_are_distinct_in_first_appearance_order_and_capped_at_eight()
    {
        var tables = IndexInventoryCapture.HintedTables(
            [
                Plan(Hint(null, "dbo", "T1"), Hint(null, "dbo", "T2")),
                Plan(Hint(null, "dbo", "T1"), Hint(null, "dbo", "T3")),
            ],
            "db",
            out var skipped,
            out var other);

        Assert.Equal([("dbo", "T1"), ("dbo", "T2"), ("dbo", "T3")], tables);
        Assert.Equal(0, skipped);
        Assert.Equal(0, other);

        var ten = Enumerable.Range(1, 10).Select(index => Hint(null, "dbo", "T" + index)).ToArray();
        tables = IndexInventoryCapture.HintedTables([Plan(ten)], "db", out skipped, out other);
        Assert.Equal(Enumerable.Range(1, 8).Select(index => ("dbo", "T" + index)).ToArray(), tables);
        Assert.Equal(2, skipped);
        Assert.Equal(0, other);

        tables = IndexInventoryCapture.HintedTables([Plan(Hint(null, null, "Orders"))], "db", out skipped, out other);
        Assert.Equal([("dbo", "Orders")], tables);
        Assert.Equal(0, skipped);
        Assert.Equal(0, other);

        tables = IndexInventoryCapture.HintedTables(
            [Plan(Hint(null, "dbo", "Orders"), Hint(null, "DBO", "orders"))],
            "db",
            out skipped,
            out other);
        Assert.Equal([("dbo", "Orders")], tables);
        Assert.Equal(0, skipped);
        Assert.Equal(0, other);
    }

    [Fact]
    public async Task Hints_for_another_database_are_counted_not_queried()
    {
        var plan = Showplan(
            new MissingHint("otherdb", "dbo", "Foreign"),
            new MissingHint("TESTDB-A", "dbo", "Local"),
            new MissingHint(null, null, "Bare"));
        var hinted = IndexInventoryCapture.HintedTables(
            [PlanDistiller.Distill(plan)],
            "testdb-a",
            out var skipped,
            out var other);

        Assert.Equal([("dbo", "Local"), ("dbo", "Bare")], hinted);
        Assert.Equal(0, skipped);
        Assert.Equal(1, other);

        using var temp = new TempDirectory();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: plan);
        session.InventoryResults =
        [
            Grid(ObjectColumns, [Row(1, 1), Row(2, 1)]),
            Grid(IndexColumns, [Index(1, 1, "IX_Local"), Index(2, 1, "IX_Bare")]),
            Grid(ColumnColumns, [Column(1, 1, "Id"), Column(2, 1, "Name")]),
        ];
        var outcome = await Module(session, temp.Path).ExecuteAsync(Measure(1, setup: null));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var command = Assert.Single(session.Commands, item => item.Sql == IndexInventoryQuery.Sql);
        Assert.Equal("Local", command.Parameters.Single(parameter => parameter.Name == "@table1").Value);
        Assert.Equal("Bare", command.Parameters.Single(parameter => parameter.Name == "@table2").Value);
        Assert.Equal("dbo", command.Parameters.Single(parameter => parameter.Name == "@schema2").Value);
        Assert.DoesNotContain(command.Parameters, parameter => Equals(parameter.Value, "Foreign") || Equals(parameter.Value, "otherdb"));
        var inventory = ReadInventory(DirectoryOf(outcome));
        Assert.Equal(["Local", "Bare"], inventory.Tables.Select(table => table.Table));
        Assert.Equal("dbo", inventory.Tables[1].Schema);
    }

    [Fact]
    public async Task No_hint_means_no_catalog_batch()
    {
        using var temp = new TempDirectory();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan());
        var outcome = await Module(session, temp.Path).ExecuteAsync(Measure(1));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.DoesNotContain(session.Commands, command => command.Sql == IndexInventoryQuery.Sql);
        Assert.False(File.Exists(Path.Combine(DirectoryOf(outcome), "index-inventory.json")));
    }

    [Fact]
    public async Task Measure_with_a_hint_captures_the_inventory_after_the_last_repetition()
    {
        using var temp = new TempDirectory();
        var before = DateTimeOffset.UtcNow;
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(new MissingHint(null, "dbo", "Orders")));
        session.InventoryResults = OneTable("PK_Orders", "Id", unique: true, primary: true, type: "CLUSTERED");
        var operation = Measure(repeat: 2, timeout: 12);
        var outcome = await Module(session, temp.Path).ExecuteAsync(operation);

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal(["setup", "warmup", "query-1", "query-2"], session.Labels);
        var measured = session.Commands
            .Where(command => !command.Sql.Contains("STATISTICS", StringComparison.Ordinal))
            .Select(command => command.Sql)
            .ToArray();
        Assert.Equal(
            [operation.SetupSql!, operation.QuerySql, operation.QuerySql, operation.QuerySql, IndexInventoryQuery.Sql],
            measured);
        var command = session.Commands[^1];
        Assert.Equal(12, command.TimeoutSeconds);
        Assert.Equal(16, command.Parameters.Count);
        Assert.All(command.Parameters, parameter =>
        {
            Assert.Equal(SqlDbType.NVarChar, parameter.Type);
            Assert.Equal(128, parameter.Size);
        });
        Assert.Equal(
            ["@schema1", "@table1", "@schema2", "@table2", "@schema3", "@table3", "@schema4", "@table4",
             "@schema5", "@table5", "@schema6", "@table6", "@schema7", "@table7", "@schema8", "@table8"],
            command.Parameters.Select(parameter => parameter.Name));
        Assert.Equal("dbo", command.Parameters[0].Value);
        Assert.Equal("Orders", command.Parameters[1].Value);
        Assert.All(command.Parameters.Skip(2), parameter => Assert.Equal(DBNull.Value, parameter.Value));

        var directory = DirectoryOf(outcome);
        var json = File.ReadAllText(Path.Combine(directory, "index-inventory.json"));
        Assert.DoesNotContain("\"ObjectFound\"", json, StringComparison.Ordinal);
        Assert.Contains("\"objectFound\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "plans", "index-inventory.json")));
        var reportJson = File.ReadAllText(Path.Combine(directory, "report.json"));
        Assert.DoesNotContain("objectFound", reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("indexesTruncated", reportJson, StringComparison.Ordinal);

        var inventory = JsonSerializer.Deserialize<IndexInventory>(json, ArtifactDirectoryPublisher.JsonOptions);
        Assert.NotNull(inventory);
        Assert.Equal("testdb-a", inventory.Database);
        Assert.Equal(TimeSpan.Zero, inventory.CapturedAt.Offset);
        Assert.InRange(inventory.CapturedAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Empty(inventory.Gaps);
        var table = Assert.Single(inventory.Tables);
        Assert.Equal("dbo", table.Schema);
        Assert.Equal("Orders", table.Table);
        Assert.True(table.ObjectFound);
        Assert.False(table.IndexesTruncated);
        var index = Assert.Single(table.Indexes);
        Assert.Equal(1, index.IndexId);
        Assert.Equal("PK_Orders", index.Name);
        Assert.Equal("CLUSTERED", index.Type);
        Assert.Equal(["Id"], index.KeyColumns);
        Assert.Empty(index.IncludeColumns);
        Assert.True(index.Unique);
        Assert.True(index.PrimaryKey);
        Assert.False(index.Disabled);
        Assert.False(index.Hypothetical);
        Assert.False(index.HasFilter);
    }

    [Fact]
    public async Task Inventory_batch_timeout_is_capped_at_thirty_seconds()
    {
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create();
        var plan = Showplan(new MissingHint(null, "dbo", "Orders"));
        session.InventoryResults = OneTable("IX_Orders", "Id");

        await IndexInventoryCapture.CaptureAsync(SqlEngine.SqlServer, session, [plan], 90, [], CancellationToken.None);
        Assert.Equal(30, Assert.Single(session.Commands).TimeoutSeconds);

        session.Commands.Clear();
        await IndexInventoryCapture.CaptureAsync(SqlEngine.SqlServer, session, [plan], 12, [], CancellationToken.None);
        Assert.Equal(12, Assert.Single(session.Commands).TimeoutSeconds);
    }

    [Fact]
    public async Task Same_table_name_in_two_schemas_maps_each_hint_to_its_own_slot()
    {
        using var temp = new TempDirectory();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(
            new MissingHint(null, "dbo", "Orders"),
            new MissingHint(null, "sales", "Orders")));
        session.InventoryResults =
        [
            Grid(ObjectColumns, [Row(1, 1), Row(2, 1)]),
            Grid(IndexColumns, [Index(1, 1, "PK_Orders", "CLUSTERED", true, true), Index(2, 2, "IX_Sales")]),
            Grid(ColumnColumns, [Column(1, 1, "Id"), Column(2, 2, "SoldOn")]),
        ];
        var outcome = await Module(session, temp.Path).ExecuteAsync(Measure(1, setup: null));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var command = Assert.Single(session.Commands, item => item.Sql == IndexInventoryQuery.Sql);
        Assert.Equal("dbo", command.Parameters.Single(parameter => parameter.Name == "@schema1").Value);
        Assert.Equal("Orders", command.Parameters.Single(parameter => parameter.Name == "@table1").Value);
        Assert.Equal("sales", command.Parameters.Single(parameter => parameter.Name == "@schema2").Value);
        Assert.Equal("Orders", command.Parameters.Single(parameter => parameter.Name == "@table2").Value);
        var inventory = ReadInventory(DirectoryOf(outcome));
        Assert.Equal(2, inventory.Tables.Count);
        Assert.Equal(("dbo", "Orders", "PK_Orders"), (inventory.Tables[0].Schema, inventory.Tables[0].Table, inventory.Tables[0].Indexes[0].Name));
        Assert.Equal(("sales", "Orders", "IX_Sales"), (inventory.Tables[1].Schema, inventory.Tables[1].Table, inventory.Tables[1].Indexes[0].Name));
        Assert.Equal(["Id"], inventory.Tables[0].Indexes[0].KeyColumns);
        Assert.Equal(["SoldOn"], inventory.Tables[1].Indexes[0].KeyColumns);
    }

    [Fact]
    public async Task Missing_object_is_recorded_as_not_found()
    {
        using var temp = new TempDirectory();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(new MissingHint(null, "dbo", "Missing")));
        session.InventoryResults =
        [
            Grid(ObjectColumns, [Row(1, 0)]),
            Grid(IndexColumns, []),
            Grid(ColumnColumns, []),
        ];
        var outcome = await Module(session, temp.Path).ExecuteAsync(Measure(1, setup: null));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var table = Assert.Single(ReadInventory(DirectoryOf(outcome)).Tables);
        Assert.Equal("Missing", table.Table);
        Assert.False(table.ObjectFound);
        Assert.Empty(table.Indexes);
        Assert.False(table.IndexesTruncated);
    }

    [Fact]
    public async Task Catalog_failure_keeps_measure_successful_and_records_a_gap()
    {
        using var temp = new TempDirectory();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(new MissingHint(null, "dbo", "Orders")));
        session.InventoryFailure = new TimeoutException($"catalog failed {Password}");
        var previous = Environment.GetEnvironmentVariable(PasswordEnv);
        Environment.SetEnvironmentVariable(PasswordEnv, Password);
        SqlHarnessOutcome outcome;
        try
        {
            outcome = await new SqlHarnessModule(session, new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow), SecureProfiles)
                .ExecuteAsync(new SqlHarnessMeasureOperation(
                    new SqlTargetRequest("secure", new Dictionary<string, string>()),
                    null,
                    "SELECT Value FROM dbo.Clients",
                    [],
                    30,
                    1));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PasswordEnv, previous);
        }

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.Null(outcome.SafeError);
        var directory = DirectoryOf(outcome);
        var json = File.ReadAllText(Path.Combine(directory, "index-inventory.json"));
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        var reportJson = File.ReadAllText(Path.Combine(directory, "report.json"));
        Assert.DoesNotContain(Password, reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Index inventory unavailable", reportJson, StringComparison.Ordinal);
        var inventory = JsonSerializer.Deserialize<IndexInventory>(json, ArtifactDirectoryPublisher.JsonOptions);
        Assert.NotNull(inventory);
        Assert.Empty(inventory.Tables);
        Assert.Equal(["Index inventory unavailable: catalog failed [REDACTED]."], inventory.Gaps);
    }

    [Fact]
    public async Task More_than_eight_tables_records_a_skip_gap()
    {
        using var temp = new TempDirectory();
        var hints = Enumerable.Range(1, 10).Select(index => new MissingHint(null, "dbo", "T" + index)).ToArray();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(hints));
        var objectRows = Enumerable.Range(1, 8).Select(slot => Row(slot, 1)).ToArray();
        var indexRows = Enumerable.Range(1, 8).Select(slot => Index(slot, 1, "IX_" + slot)).ToArray();
        session.InventoryResults =
        [
            Grid(ObjectColumns, objectRows),
            Grid(IndexColumns, indexRows),
            Grid(ColumnColumns, Enumerable.Range(1, 8).Select(slot => Column(slot, 1, "Id")).ToArray()),
        ];
        var outcome = await Module(session, temp.Path).ExecuteAsync(Measure(1, setup: null));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var command = Assert.Single(session.Commands, item => item.Sql == IndexInventoryQuery.Sql);
        Assert.Equal("T1", command.Parameters.Single(parameter => parameter.Name == "@table1").Value);
        Assert.Equal("T8", command.Parameters.Single(parameter => parameter.Name == "@table8").Value);
        Assert.DoesNotContain(command.Parameters, parameter => parameter.Value is "T9" or "T10");
        var inventory = ReadInventory(DirectoryOf(outcome));
        Assert.Equal(Enumerable.Range(1, 8).Select(index => "T" + index).ToArray(), inventory.Tables.Select(table => table.Table));
        Assert.Equal(["Index inventory covers the first 8 hinted tables; 2 more were skipped."], inventory.Gaps);
    }

    [Fact]
    public async Task Reader_keeps_sixty_four_indexes_and_drops_the_sixty_fifth()
    {
        using var temp = new TempDirectory();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(new MissingHint(null, "dbo", "Orders")));
        var indexes = new object?[65][];
        for (var id = 1; id <= 65; id++)
            indexes[id - 1] = Index(1, id, id == 65 ? "DROPPED" : "IX_" + id);
        session.InventoryResults =
        [
            Grid(ObjectColumns, [Row(1, 1)]),
            Grid(IndexColumns, indexes),
            Grid(ColumnColumns, [Column(1, 1, "Kept"), Column(1, 65, "Ignored")]),
        ];
        var outcome = await Module(session, temp.Path).ExecuteAsync(Measure(1, setup: null));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var table = Assert.Single(ReadInventory(DirectoryOf(outcome)).Tables);
        Assert.True(table.IndexesTruncated);
        Assert.Equal(64, table.Indexes.Count);
        Assert.Equal("IX_1", table.Indexes[0].Name);
        Assert.Equal(["Kept"], table.Indexes[0].KeyColumns);
        Assert.DoesNotContain(table.Indexes, index => index.Name == "DROPPED" || index.KeyColumns.Contains("Ignored") || index.IncludeColumns.Contains("Ignored"));
    }

    [Fact]
    public async Task Column_rows_are_read_in_ascending_ordinal_order()
    {
        var reader = new SequentialGridReader(
        [
            Grid(ObjectColumns, [Row(1, 1)]),
            Grid(IndexColumns, [Index(1, 1, "IX_Orders")]),
            Grid(ColumnColumns, [Column(1, 1, "Id"), Column(1, 1, "Note", included: true)]),
        ]);

        var tables = await IndexInventoryQuery.ReadAsync(reader, [("dbo", "Orders")], CancellationToken.None);

        var index = Assert.Single(Assert.Single(tables).Indexes);
        Assert.Equal(["Id"], index.KeyColumns);
        Assert.Equal(["Note"], index.IncludeColumns);
    }

    [Fact]
    public async Task Reader_keeps_only_positive_key_ordinals_when_a_leading_column_is_not_included()
    {
        var reader = new SequentialGridReader(
        [
            Grid(ObjectColumns, [Row(1, 1)]),
            Grid(IndexColumns, [Index(1, 1, "IX_Orders")]),
            Grid(ColumnColumns,
            [
                Column(1, 1, "OrderDate", keyOrdinal: 0),
                Column(1, 1, "CustomerId", keyOrdinal: 1),
                Column(1, 1, "OrderId", keyOrdinal: 2),
            ]),
        ]);

        var tables = await IndexInventoryQuery.ReadAsync(reader, [("dbo", "Orders")], CancellationToken.None);

        var index = Assert.Single(Assert.Single(tables).Indexes);
        Assert.Equal(["CustomerId", "OrderId"], index.KeyColumns);
        Assert.Empty(index.IncludeColumns);
    }

    [Fact]
    public async Task Postgres_never_runs_the_inventory_batch()
    {
        using var temp = new TempDirectory();
        var session = new PostgresSession();
        var module = new SqlHarnessModule(session, new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UtcNow), PostgresProfiles);
        var outcome = await module.ExecuteAsync(new SqlHarnessMeasureOperation(
            new SqlTargetRequest("local-pg", new Dictionary<string, string>()),
            null,
            "SELECT 1",
            [],
            30,
            1));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.DoesNotContain(session.Commands, command => command.Sql == IndexInventoryQuery.Sql);
        Assert.False(File.Exists(Path.Combine(DirectoryOf(outcome), "index-inventory.json")));
    }

    [Fact]
    public async Task Compare_captures_one_inventory_for_both_variants()
    {
        using var temp = new TempDirectory();
        const string baseline = "SELECT Value FROM dbo.Clients";
        const string candidate = "SELECT Value FROM dbo.Clients /* candidate */";
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create();
        session.PlanForSql = sql => sql.Contains("candidate", StringComparison.Ordinal)
            ? Showplan(new MissingHint(null, "sales", "Orders"))
            : Showplan(new MissingHint(null, "dbo", "Orders"));
        session.InventoryResults =
        [
            Grid(ObjectColumns, [Row(1, 1), Row(2, 1)]),
            Grid(IndexColumns, [Index(1, 1, "PK_Orders"), Index(2, 1, "IX_Sales")]),
            Grid(ColumnColumns, [Column(1, 1, "Id"), Column(2, 1, "SoldOn")]),
        ];
        var outcome = await Module(session, temp.Path).ExecuteAsync(new SqlHarnessCompareOperation(
            Target(),
            null,
            baseline,
            candidate,
            [],
            30,
            1)
        {
            Context = new CompareContext("inventory", "whole"),
        });

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var inventoryAt = session.Commands.FindIndex(command => command.Sql == IndexInventoryQuery.Sql);
        Assert.Equal(session.Commands.Count - 1, inventoryAt);
        Assert.True(inventoryAt > session.Commands.FindLastIndex(command => command.Sql == baseline));
        Assert.True(inventoryAt > session.Commands.FindLastIndex(command => command.Sql == candidate));
        Assert.Equal(1, session.Commands.Count(command => command.Sql == IndexInventoryQuery.Sql));
        var inventory = ReadInventory(DirectoryOf(outcome));
        Assert.Equal([("dbo", "Orders"), ("sales", "Orders")], inventory.Tables.Select(table => (table.Schema, table.Table)));
        Assert.Equal("PK_Orders", inventory.Tables[0].Indexes[0].Name);
        Assert.Equal("IX_Sales", inventory.Tables[1].Indexes[0].Name);
    }

    [Fact]
    public async Task Matrix_cells_each_capture_an_inventory_on_their_own_connection()
    {
        using var temp = new TempDirectory();
        var factory = new CellFactory();
        var outcome = await Module(factory, temp.Path).ExecuteAsync(new SqlHarnessCompareMatrixOperation(
            Target(),
            null,
            "SELECT @Value AS Value",
            "SELECT @Value AS Value /* candidate */",
            [],
            30,
            1,
            "Value:int=1,2")
        {
            Context = new CompareContext("inventory", "cell"),
        });

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        var report = Assert.IsType<SqlHarnessCompareMatrixReport>(outcome.Report);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.Equal(2, report.Cells.Count);
        Assert.All(factory.Sessions, cell => Assert.Single(cell.Commands, command => command.Sql == IndexInventoryQuery.Sql));
        var first = ReadInventory(report.Cells[0].Compare.ArtifactDirectory!);
        var second = ReadInventory(report.Cells[1].Compare.ArtifactDirectory!);
        Assert.NotEqual(report.Cells[0].Compare.ArtifactDirectory, report.Cells[1].Compare.ArtifactDirectory);
        Assert.Equal("Alpha", Assert.Single(first.Tables).Table);
        Assert.Equal("IX_Alpha", first.Tables[0].Indexes[0].Name);
        Assert.Equal("Beta", Assert.Single(second.Tables).Table);
        Assert.Equal("IX_Beta", second.Tables[0].Indexes[0].Name);
    }

    [Fact]
    public async Task Measure_parameter_sets_capture_one_inventory_for_the_operation()
    {
        using var temp = new TempDirectory();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(new MissingHint(null, "dbo", "Orders")));
        session.InventoryResults = OneTable("IX_Orders", "Id");
        var outcome = await Module(session, temp.Path).ExecuteAsync(new SqlHarnessMeasureOperation(
            Target(),
            null,
            "SELECT @Id AS Value",
            [],
            30,
            1,
            [
                new("small", []) { TypedParameters = [new SqlHarnessParameterInput("Id", "int", "1")] },
                new("large", []) { TypedParameters = [new SqlHarnessParameterInput("Id", "int", "2")] },
            ]));

        Assert.True(outcome.ExitCode == SqlHarnessExitCode.Success, outcome.SafeError);
        Assert.Equal(4, session.Commands.Count(command => command.Sql == "SELECT @Id AS Value"));
        Assert.Equal(IndexInventoryQuery.Sql, session.Commands[^1].Sql);
        Assert.Equal(1, session.Commands.Count(command => command.Sql == IndexInventoryQuery.Sql));
        Assert.Equal("Orders", Assert.Single(ReadInventory(DirectoryOf(outcome)).Tables).Table);
    }

    [Fact]
    public async Task Operation_cancellation_during_capture_cancels_the_operation()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(new MissingHint(null, "dbo", "Orders")));
        session.CancelInventory = cancellation;
        var outcome = await Module(session, temp.Path).ExecuteAsync(Measure(1, setup: null), cancellation.Token);

        Assert.Equal(SqlHarnessExitCode.SqlExecution, outcome.ExitCode);
        Assert.Null(outcome.Report);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains(session.Commands, command => command.Sql == IndexInventoryQuery.Sql);
    }

    private static SqlHarnessModule Module(ISqlSessionFactory sessions, string root) =>
        new(sessions, new CompareArtifactWriter(root, () => DateTimeOffset.UtcNow), Profiles);

    private static SqlHarnessMeasureOperation Measure(int repeat, int timeout = 30, string? setup = "SELECT Id INTO #ids FROM dbo.Clients") =>
        new(Target(), setup, "SELECT Value FROM dbo.Clients", [], timeout, repeat);

    private static SqlTargetRequest Target() =>
        new("test", new Dictionary<string, string> { ["env"] = "a" });

    private static IReadOnlyDictionary<string, TargetProfile> Profiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^(a|b)$" }, "integrated"),
        };

    private static IReadOnlyDictionary<string, TargetProfile> SecureProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["secure"] = new(
                "test-server",
                "testdb-a",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: PasswordEnv,
                TrustServerCertificate: true),
        };

    private static IReadOnlyDictionary<string, TargetProfile> PostgresProfiles() =>
        new Dictionary<string, TargetProfile>
        {
            ["local-pg"] = new(
                "localhost",
                "appdb",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "sqlharness",
                PasswordEnvVar: "SQLHARNESS_PG_PASSWORD",
                TrustServerCertificate: true,
                Engine: "postgres"),
        };

    private static string DirectoryOf(SqlHarnessOutcome outcome) => outcome.Report switch
    {
        SqlHarnessMeasureReport measure => measure.ArtifactDirectory!,
        SqlHarnessMeasureSetReport sets => sets.ArtifactDirectory!,
        SqlHarnessCompareReport compare => compare.ArtifactDirectory!,
        _ => throw new InvalidOperationException($"No artifact directory on {outcome.Report?.GetType().Name}."),
    };

    private static IndexInventory ReadInventory(string directory)
    {
        var inventory = JsonSerializer.Deserialize<IndexInventory>(
            File.ReadAllText(Path.Combine(directory, "index-inventory.json")),
            ArtifactDirectoryPublisher.JsonOptions);
        Assert.NotNull(inventory);
        return inventory;
    }

    private static DistilledPlan Plan(params MissingIndex[] hints) =>
        new([new PlanStatement(null, new PlanNode("Scan", null, null, null, null, null, null, null, null, [], []), hints)]);

    private static MissingIndex Hint(string? database, string? schema, string table) =>
        new(database, schema, table, ["Id"], [], [], 1);

    private static string Showplan(params MissingHint[] hints)
    {
        var groups = string.Concat(hints.Select(hint =>
            $"""<MissingIndexGroup Impact="10"><MissingIndex{Attributes(hint)}><ColumnGroup Usage="EQUALITY"><Column Name="[Id]" ColumnId="1" /></ColumnGroup></MissingIndex></MissingIndexGroup>"""));
        var missing = hints.Length == 0 ? string.Empty : "<MissingIndexes>" + groups + "</MissingIndexes>";
        return $"""<ShowPlanXML xmlns="{ShowplanNamespace}"><BatchSequence><Batch><Statements><StmtSimple><QueryPlan>{missing}<RelOp NodeId="1" PhysicalOp="Scan" EstimatedTotalSubtreeCost="1" /></QueryPlan></StmtSimple></Statements></Batch></BatchSequence></ShowPlanXML>""";
    }

    private static string Attributes(MissingHint hint)
    {
        var text = string.Empty;
        if (hint.Database is not null)
            text += $@" Database=""[{hint.Database}]""";
        if (hint.Schema is not null)
            text += $@" Schema=""[{hint.Schema}]""";
        text += $@" Table=""[{hint.Table}]""";
        return text;
    }

    private static (string[] Names, object?[][] Rows)[] OneTable(
        string indexName,
        string keyColumn,
        bool unique = false,
        bool primary = false,
        string type = "NONCLUSTERED") =>
    [
        Grid(ObjectColumns, [Row(1, 1)]),
        Grid(IndexColumns, [Index(1, 1, indexName, type, unique, primary)]),
        Grid(ColumnColumns, [Column(1, 1, keyColumn)]),
    ];

    private static (string[] Names, object?[][] Rows) Grid(string[] names, object?[][] rows) => (names, rows);

    private static object?[] Row(params object?[] values) => values;

    private static object?[] Index(int slot, int indexId, string name, string type = "NONCLUSTERED", bool unique = false, bool primary = false) =>
        [slot, indexId, name, type, unique, primary, false, false, false];

    private static object?[] Column(int slot, int indexId, string name, bool included = false, int? keyOrdinal = null) =>
        [slot, indexId, name, keyOrdinal ?? (included ? 0 : 1), included];

    private readonly record struct MissingHint(string? Database, string? Schema, string Table);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlharness-inventory-" + Guid.NewGuid().ToString("N"));

        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class CellFactory : ISqlSessionFactory
    {
        private int _next;

        public List<SqlHarnessMeasureTests.FakeMeasureSession> Sessions { get; } = [];

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct)
        {
            var index = _next++;
            var table = index == 0 ? "Alpha" : "Beta";
            var indexName = index == 0 ? "IX_Alpha" : "IX_Beta";
            var session = SqlHarnessMeasureTests.FakeMeasureSession.Create(plan: Showplan(new MissingHint(null, "dbo", table)));
            session.InventoryResults = OneTable(indexName, "Id");
            Sessions.Add(session);
            return Task.FromResult<ISqlSession>(session);
        }
    }

    private sealed class PostgresSession : ISqlSessionFactory, ISqlSession
    {
        private const string Explain = """
            [{"Plan":{"Node Type":"Seq Scan","Relation Name":"foo","Shared Hit Blocks":1,"Shared Read Blocks":0,"Local Hit Blocks":0,"Local Read Blocks":0},"Planning Time":1.0,"Execution Time":1.0}]
            """;

        public List<SqlExecutionCommand> Commands { get; } = [];

        public IReadOnlyList<string> Messages => [];

        public SqlHarnessTargetIdentityReport Identity { get; set; } =
            new("localhost", "appdb", "localhost", "appdb", "profile", Engine: "postgres");

        public Task<ISqlSession> ConnectAsync(ResolvedTarget target, CancellationToken ct) =>
            Task.FromResult<ISqlSession>(this);

        public Task<ISqlReader> ExecuteReaderAsync(SqlExecutionCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.Sql.StartsWith("EXPLAIN ", StringComparison.Ordinal))
                return Task.FromResult<ISqlReader>(new RowReader(["QUERY PLAN"], [[Explain]]));

            return Task.FromResult<ISqlReader>(new RowReader(["n"], [[1]]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Rejects a decreasing <see cref="ISqlReader.GetValue"/> ordinal, as SqlClient does under <c>CommandBehavior.SequentialAccess</c>.</summary>
    private sealed class SequentialGridReader(IReadOnlyList<(string[] Names, object?[][] Rows)> grids) : ISqlReader
    {
        private readonly (string[] Names, object?[][] Rows)[] _grids = grids.ToArray();
        private int _grid;
        private int _row = -1;
        private int _highestOrdinal = -1;

        private (string[] Names, object?[][] Rows) Current => _grids[_grid];

        public int FieldCount => _grids.Length == 0 ? 0 : Current.Names.Length;

        public int RecordsAffected => -1;

        public string GetName(int ordinal) => Current.Names[ordinal];

        public Type GetFieldType(int ordinal) => Current.Rows[0][ordinal]?.GetType() ?? typeof(object);

        public bool GetAllowNull(int ordinal) => false;

        public object GetValue(int ordinal)
        {
            if (ordinal <= _highestOrdinal)
                throw new InvalidOperationException($"Invalid attempt to read from column ordinal '{ordinal}'.");

            _highestOrdinal = ordinal;
            return Current.Rows[_row][ordinal] ?? DBNull.Value;
        }

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            _highestOrdinal = -1;
            return Task.FromResult(_grids.Length > 0 && ++_row < Current.Rows.Length);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            _highestOrdinal = -1;
            if (_grids.Length == 0 || ++_grid >= _grids.Length)
                return Task.FromResult(false);

            _row = -1;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RowReader(string[] names, object?[][] rows) : ISqlReader
    {
        private int _row = -1;

        public int FieldCount => names.Length;

        public int RecordsAffected => -1;

        public string GetName(int ordinal) => names[ordinal];

        public Type GetFieldType(int ordinal) => rows[0][ordinal]?.GetType() ?? typeof(object);

        public bool GetAllowNull(int ordinal) => false;

        public object GetValue(int ordinal) => rows[_row][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(++_row < rows.Length);

        public Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}