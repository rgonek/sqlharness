using System.Text.Json;

using Microsoft.Data.Sqlite;
using SqlHarness.Core;
using SqlHarness.Core.Journal;
using SqlHarness.Mcp.Tools;

using Xunit;

namespace SqlHarness.Mcp.Tests;

public sealed class AnnotateToolTests
{
    private const string CallerKey = "mcp:caller";
    private static readonly string Ref1 = Reference(1);

    [Fact]
    public async Task Malformed_operation_reference_fails_validation()
    {
        using var journal = new TestJournal();

        var error = await Assert.ThrowsAsync<McpMappingException>(() => AnnotateHandler.ApplyAsync(
            journal.Path, CallerKey, "conversation-a", [new AnnotationItemArgument("bad")], matchingOn: false, default));

        Assert.Contains("operationRef", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twenty_item_batch_returns_mixed_per_item_results_and_echoes_annotation()
    {
        using var journal = new TestJournal();
        journal.AddOperation(1, CallerKey, "codex", "query", Ref1);
        journal.AddOperation(2, "mcp:foreign", "codex", "query", Reference(2));
        journal.AddOperation(3, CallerKey, "codex", "mutation", Reference(3));
        journal.AddOperation(4, CallerKey, "codex", "artifact", Reference(4));
        journal.AddOperation(5, CallerKey, "codex", "query", Reference(5), dryRun: true);

        var items = Enumerable.Range(1, 20)
            .Select(index => new AnnotationItemArgument(Reference(index), index == 1 ? "first label" : null, index == 1 ? true : null))
            .ToArray();
        var results = await AnnotateHandler.ApplyAsync(journal.Path, CallerKey, "conversation-a", items, matchingOn: false, default);

        Assert.Equal(20, results.Count);
        Assert.Equal("success", results[0].Status);
        Assert.Equal(new AnnotationItemResult(Ref1, "success", "query", "first label", true), results[0]);
        Assert.All(results.Skip(1), result => Assert.Equal("operation_not_available", result.Status));
        Assert.Null(results[1].Tool);
        Assert.Null(results[1].Label);
        Assert.Null(results[1].Keep);
        var adapted = McpResultAdapter.Adapt(new SqlHarnessOutcome(SqlHarnessExitCode.Success, results, null), "sqlharness_annotate");
        Assert.False(adapted.IsError);
    }

    [Fact]
    public async Task Unknown_and_foreign_references_have_byte_identical_results()
    {
        using var unknownJournal = new TestJournal();
        using var foreignJournal = new TestJournal();
        foreignJournal.AddOperation(1, "mcp:foreign", "codex", "query", Ref1);
        var item = new[] { new AnnotationItemArgument(Ref1, "new label", true) };

        var unknown = await AnnotateHandler.ApplyAsync(unknownJournal.Path, CallerKey, null, item, matchingOn: false, default);
        var foreign = await AnnotateHandler.ApplyAsync(foreignJournal.Path, CallerKey, null, item, matchingOn: false, default);

        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(unknown), JsonSerializer.SerializeToUtf8Bytes(foreign));
    }

    [Fact]
    public async Task Mutation_and_artifact_operations_are_not_annotatable()
    {
        using var journal = new TestJournal();
        journal.AddOperation(1, CallerKey, "codex", "mutation", Ref1);
        journal.AddOperation(2, CallerKey, "codex", "artifact", Reference(2));
        var results = await AnnotateHandler.ApplyAsync(journal.Path, CallerKey, null,
            [new AnnotationItemArgument(Ref1), new AnnotationItemArgument(Reference(2))], matchingOn: false, default);

        Assert.All(results, result => Assert.Equal("operation_not_available", result.Status));
    }

    [Fact]
    public async Task Matching_flag_is_reloaded_as_an_authorization_input_per_call()
    {
        using var journal = new TestJournal();
        journal.AddOperation(1, CallerKey, "codex", "query", Ref1, agentSessionId: "conversation-b");

        var off = await AnnotateHandler.ApplyAsync(journal.Path, CallerKey, "conversation-a",
            [new AnnotationItemArgument(Ref1, "off")], matchingOn: false, default);
        var on = await AnnotateHandler.ApplyAsync(journal.Path, CallerKey, "conversation-a",
            [new AnnotationItemArgument(Ref1, "on")], matchingOn: true, default);

        Assert.Equal("success", off[0].Status);
        Assert.Equal("operation_not_available", on[0].Status);
    }

    [Fact]
    public async Task Matching_on_does_not_authorize_a_foreign_agent_with_the_same_conversation_id()
    {
        using var journal = new TestJournal();
        journal.AddOperation(1, "mcp:opencode-foreign", "opencode", "query", Ref1, agentSessionId: "same-id");

        var result = await AnnotateHandler.ApplyAsync(journal.Path, CallerKey, "same-id",
            [new AnnotationItemArgument(Ref1, "new label")], matchingOn: true, default);

        Assert.Equal("operation_not_available", result[0].Status);
        Assert.Null(result[0].Tool);
        Assert.Null(result[0].Label);
    }

    [Fact]
    public void Matching_flag_is_read_again_from_configuration()
    {
        var configPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlharness-annotate-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(configPath, "{\"dashboard\":{\"agentTranscripts\":{\"enabled\":false}}}");
            Assert.False(AnnotateHandler.LoadMatchingFlag(configPath));
            File.WriteAllText(configPath, "{\"dashboard\":{\"agentTranscripts\":{\"enabled\":true}}}");
            Assert.True(AnnotateHandler.LoadMatchingFlag(configPath));
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task Tool_catalog_adds_annotation_tool_only_when_enabled()
    {
        var profiles = new Dictionary<string, SqlHarness.Core.Targets.TargetProfile>(StringComparer.Ordinal)
        {
            ["profile"] = new("server.invalid", "db", new Dictionary<string, string>(), "integrated"),
        };
        var scope = McpScope.Create(new McpServerOptions { Profile = "profile" }, profiles);
        var off = McpToolCatalog.CreateTools(scope, scope.CreateModule());
        var on = McpToolCatalog.CreateTools(scope, scope.CreateModule(), operationAnnotationsEnabled: true);

        Assert.Equal(9, off.Count);
        Assert.DoesNotContain(off, tool => tool.ProtocolTool.Name == "sqlharness_annotate");
        Assert.Equal(10, on.Count);
        Assert.Contains(on, tool => tool.ProtocolTool.Name == "sqlharness_annotate");
    }

    private static string Reference(int id) => id.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class TestJournal : IDisposable
    {
        public TestJournal()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlharness-annotate-" + Guid.NewGuid().ToString("N") + ".db");
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions(id INTEGER PRIMARY KEY, session_key TEXT NOT NULL, agent_kind TEXT NOT NULL, transport TEXT NOT NULL);
                CREATE TABLE operations(id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, operation TEXT NOT NULL, operation_ref TEXT, agent_session_id TEXT, dry_run INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE agent_conversations(id INTEGER PRIMARY KEY, agent_kind TEXT NOT NULL, conversation_id TEXT NOT NULL);
                CREATE TABLE operation_agent_links(operation_id INTEGER NOT NULL, conversation INTEGER NOT NULL);
                CREATE TABLE operation_annotations(operation_id INTEGER PRIMARY KEY, agent_label_initial TEXT, agent_label TEXT, agent_keep INTEGER, owner_starred INTEGER NOT NULL DEFAULT 0, owner_note TEXT, updated_at TEXT NOT NULL);
                INSERT INTO sessions(id, session_key, agent_kind, transport) VALUES(1, 'mcp:caller', 'codex', 'mcp');
                INSERT INTO sessions(id, session_key, agent_kind, transport) VALUES(2, 'mcp:foreign', 'codex', 'mcp');
                INSERT INTO sessions(id, session_key, agent_kind, transport) VALUES(3, 'mcp:opencode-foreign', 'opencode', 'mcp');
                """;
            command.ExecuteNonQuery();
        }

        public string Path { get; }

        public void AddOperation(long id, string sessionKey, string agentKind, string tool, string operationRef,
            bool dryRun = false, string? agentSessionId = null)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operations(id, session_id, operation, operation_ref, agent_session_id, dry_run)
                SELECT $id, s.id, $tool, $ref, $agentSession, $dryRun FROM sessions s WHERE s.session_key = $key;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$tool", tool);
            command.Parameters.AddWithValue("$ref", operationRef);
            command.Parameters.AddWithValue("$agentSession", (object?)agentSessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$dryRun", dryRun ? 1 : 0);
            command.Parameters.AddWithValue("$key", sessionKey);
            command.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
