using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class SqlHarnessConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlharness-config-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public SqlHarnessConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Missing_file_yields_defaults_without_warning()
    {
        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Missing, result.Status);
        Assert.Null(result.Warning);
        Assert.True(result.Config.Journal.Enabled);
        Assert.False(result.Config.Journal.StoreSensitive);
        Assert.False(result.Config.Journal.Retention.Enabled);
        Assert.False(result.Config.Diagnostics.Enabled);
        Assert.False(result.Config.Dashboard.AutoStart);
        Assert.Equal(47800, result.Config.Dashboard.Port);
        Assert.False(result.Config.Dashboard.SafetyNotifications);
        Assert.Equal(30, result.Config.Connection.TimeoutSeconds);
        Assert.True(System.Text.Json.JsonDocument.Parse(SqlHarnessConfigWriter.Serialize(result.Config)).RootElement
            .GetProperty("compare").GetProperty("requireContext").GetBoolean());
    }

    [Fact]
    public void Mutation_approval_timeout_out_of_range_uses_defaults_with_warning()
    {
        File.WriteAllText(ConfigPath, """{ "mutations": { "approvalTimeoutSeconds": 10 } }""");

        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Invalid, result.Status);
        Assert.NotNull(result.Warning);
        Assert.Equal(SqlHarnessConfig.Default, result.Config);
    }

    [Fact]
    public void Connection_timeout_defaults_and_custom_value_round_trips()
    {
        var defaults = SqlHarnessConfigLoader.Parse("{}"u8.ToArray()).Config!;
        var parsed = SqlHarnessConfigLoader.Parse("""{ "connection": { "timeoutSeconds": 47 } }"""u8.ToArray());

        Assert.Equal(30, defaults.Connection.TimeoutSeconds);
        Assert.Empty(parsed.Errors);
        Assert.Equal(47, parsed.Config!.Connection.TimeoutSeconds);
        Assert.Equal(47, System.Text.Json.JsonDocument.Parse(SqlHarnessConfigWriter.Serialize(parsed.Config))
            .RootElement.GetProperty("connection").GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public void Execution_timeout_defaults_when_missing_and_custom_value_round_trips()
    {
        var defaults = SqlHarnessConfigLoader.Parse("{}"u8.ToArray()).Config!;
        var parsed = SqlHarnessConfigLoader.Parse("""{ "execution": { "defaultTimeoutSeconds": 47 } }"""u8.ToArray());

        Assert.Equal(30, defaults.Execution.DefaultTimeoutSeconds);
        Assert.Empty(parsed.Errors);
        Assert.Equal(47, parsed.Config!.Execution.DefaultTimeoutSeconds);
        Assert.Equal(47, System.Text.Json.JsonDocument.Parse(SqlHarnessConfigWriter.Serialize(parsed.Config))
            .RootElement.GetProperty("execution").GetProperty("defaultTimeoutSeconds").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Execution_timeout_out_of_range_fails_validation(int seconds)
    {
        var parsed = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(
            $"{{ \"execution\": {{ \"defaultTimeoutSeconds\": {seconds} }} }}"));

        Assert.Null(parsed.Config);
        Assert.Contains(parsed.Errors, error => error.Field == "execution.defaultTimeoutSeconds" && error.Message == "Must be between 1 and 300.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Execution_timeout_accepts_inclusive_bounds(int seconds)
    {
        var parsed = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(
            $"{{ \"execution\": {{ \"defaultTimeoutSeconds\": {seconds} }} }}"));

        Assert.Empty(parsed.Errors);
        Assert.Equal(seconds, parsed.Config!.Execution.DefaultTimeoutSeconds);
    }

    [Fact]
    public void Explicit_null_execution_section_is_invalid()
    {
        var parsed = SqlHarnessConfigLoader.Parse("""{ "execution": null }"""u8.ToArray());

        Assert.Null(parsed.Config);
        Assert.Contains(parsed.Errors, error => error.Field == "$" && error.Message.Contains("execution", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Connection_timeout_accepts_inclusive_bounds(int seconds)
    {
        var parsed = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes($"{{ \"connection\": {{ \"timeoutSeconds\": {seconds} }} }}"));

        Assert.Empty(parsed.Errors);
        Assert.Equal(seconds, parsed.Config!.Connection.TimeoutSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Connection_timeout_rejects_out_of_range_values(int seconds)
    {
        var parsed = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes($"{{ \"connection\": {{ \"timeoutSeconds\": {seconds} }} }}"));

        Assert.Null(parsed.Config);
        Assert.Contains(parsed.Errors, error => error.Field == "connection.timeoutSeconds");
    }

    [Theory]
    [InlineData("{ \"connection\": null }")]
    [InlineData("{ \"connection\": { \"timeoutSeconds\": \"30\" } }")]
    [InlineData("{ \"connection\": { \"unknown\": 30 } }")]
    public void Connection_settings_reject_null_section_wrong_type_and_unknown_fields(string json)
    {
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json)).Errors);
    }

    [Fact]
    public void Unsupported_mutation_verification_is_reported_and_never_becomes_click()
    {
        const string json = """{ "mutations": { "verification": "webauthn" } }""";
        var parsed = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        File.WriteAllText(ConfigPath, json);

        var loaded = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Null(parsed.Config);
        Assert.Contains(parsed.Errors, error => error.Field == "mutations.verification");
        Assert.Equal(SqlHarnessConfigStatus.Invalid, loaded.Status);
        Assert.NotNull(loaded.Warning);
        Assert.Equal("click", ConfigProperty(ConfigProperty(loaded.Config, "Mutations")!, "Verification"));
    }

    [Theory]
    [InlineData("defaultMode", "2")]
    [InlineData("verification", "0")]
    public void Mutation_settings_reject_numeric_enum_strings(string field, string value)
    {
        var json = $$"""{ "mutations": { "{{field}}": "{{value}}" } }""";
        var parsed = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(parsed.Config);
        Assert.Contains(parsed.Errors, error => error.Field == $"mutations.{field}");
    }

    [Fact]
    public void Mutation_settings_default_and_round_trip_supported_values()
    {
        var defaults = SqlHarnessConfigLoader.Parse("{}"u8.ToArray()).Config!;
        var parsed = SqlHarnessConfigLoader.Parse("""{ "mutations": { "defaultMode": "operator", "verification": "click", "approvalTimeoutSeconds": 240 } }"""u8.ToArray());

        Assert.Equal("never", ConfigProperty(ConfigProperty(defaults, "Mutations")!, "DefaultMode"));
        Assert.Equal("click", ConfigProperty(ConfigProperty(defaults, "Mutations")!, "Verification"));
        Assert.Equal(120, ConfigProperty(ConfigProperty(defaults, "Mutations")!, "ApprovalTimeoutSeconds"));
        Assert.Empty(parsed.Errors);
        Assert.Equal("operator", ConfigProperty(ConfigProperty(parsed.Config!, "Mutations")!, "DefaultMode"));
        Assert.Equal(240, ConfigProperty(ConfigProperty(parsed.Config!, "Mutations")!, "ApprovalTimeoutSeconds"));
        Assert.Equal("click", ConfigProperty(ConfigProperty(parsed.Config!, "Mutations")!, "Verification"));
    }

    private static object? ConfigProperty(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);

    [Fact]
    public void Diagnostics_is_opt_in_and_rejects_unknown_or_wrong_typed_fields()
    {
        var enabled = SqlHarnessConfigLoader.Parse("""{ "diagnostics": { "enabled": true } }"""u8.ToArray());

        Assert.Empty(enabled.Errors);
        Assert.True(enabled.Config!.Diagnostics.Enabled);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "diagnostics": { "enabled": "true" } }"""u8.ToArray()).Errors);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "diagnostics": { "captureSecrets": true } }"""u8.ToArray()).Errors);
        Assert.False(SqlHarnessConfigLoader.Parse("{}"u8.ToArray()).Config!.Diagnostics.Enabled);
    }

    [Fact]
    public void Valid_file_is_read()
    {
        File.WriteAllText(ConfigPath, """
            {
              "journal": { "enabled": true, "storeSensitive": true,
                           "retention": { "enabled": true, "maxAgeDays": 7, "maxSizeMb": 100 } },
              "dashboard": { "autoStart": true, "port": 48000, "idleShutdownHours": 2 }
            }
            """);

        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Valid, result.Status);
        Assert.True(result.Config.Journal.StoreSensitive);
        Assert.Equal(7, result.Config.Journal.Retention.MaxAgeDays);
        Assert.Equal(48000, result.Config.Dashboard.Port);
    }

    [Fact]
    public void Compare_policy_round_trips_and_defaults_to_required()
    {
        var defaults = SqlHarnessConfigLoader.Parse("{}"u8.ToArray()).Config!;
        Assert.True(System.Text.Json.JsonDocument.Parse(SqlHarnessConfigWriter.Serialize(defaults)).RootElement
            .GetProperty("compare").GetProperty("requireContext").GetBoolean());

        foreach (var required in new[] { true, false })
        {
            var json = $"{{ \"compare\": {{ \"requireContext\": {required.ToString().ToLowerInvariant()} }} }}";
            var parsed = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json));

            Assert.Empty(parsed.Errors);
            Assert.Equal(required, System.Text.Json.JsonDocument.Parse(SqlHarnessConfigWriter.Serialize(parsed.Config!))
                .RootElement.GetProperty("compare").GetProperty("requireContext").GetBoolean());
        }
    }

    [Fact]
    public void Dashboard_safety_notifications_default_off_and_round_trip()
    {
        var defaults = SqlHarnessConfigLoader.Parse("{}"u8.ToArray()).Config!;
        Assert.False(defaults.Dashboard.SafetyNotifications);
        var parsed = SqlHarnessConfigLoader.Parse("""{ "dashboard": { "safetyNotifications": true } }"""u8.ToArray());

        Assert.Empty(parsed.Errors);
        Assert.True(parsed.Config!.Dashboard.SafetyNotifications);
        Assert.True(System.Text.Json.JsonDocument.Parse(SqlHarnessConfigWriter.Serialize(parsed.Config)).RootElement
            .GetProperty("dashboard").GetProperty("safetyNotifications").GetBoolean());
    }

    [Theory]
    [InlineData("{ \"compare\": { \"requireContext\": \"true\" } }")]
    [InlineData("{ \"compare\": { \"unknown\": false } }")]
    public void Compare_policy_rejects_wrong_types_and_unknown_fields(string json)
    {
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json)).Errors);
    }

    [Fact]
    public void Partial_file_keeps_defaults_for_omitted_sections()
    {
        File.WriteAllText(ConfigPath, """{ "journal": { "storeSensitive": true } }""");

        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Valid, result.Status);
        Assert.True(result.Config.Journal.Enabled);
        Assert.True(result.Config.Journal.StoreSensitive);
        Assert.False(result.Config.Dashboard.AutoStart);
    }

    [Theory]
    [InlineData("""{ "journal": { "storeSensitive": true }, "unknown": 1 }""")]
    [InlineData("""{ "journal": { "storeSensitive": true, "port": 1 } }""")]
    [InlineData("""{ "journal": { "storeSensitive": true }, "dashboard": { "port": 80 } }""")]
    [InlineData("""{ "journal": { "storeSensitive": true, "retention": { "maxAgeDays": 0 } } }""")]
    [InlineData("""{ "journal": { "storeSensitive": "true" } }""")]
    [InlineData("""{ "journal": null }""")]
    [InlineData("""{ "diagnostics": { "enabled": true, "unknown": 1 } }""")]
    [InlineData("""{ "journal": { "storeSensitive": true }, } """)]
    [InlineData("// comment\n        { \"journal\": { \"storeSensitive\": true } }")]
    [InlineData("not json")]
    public void Invalid_config_never_enables_store_sensitive(string content)
    {
        File.WriteAllText(ConfigPath, content);

        var result = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Invalid, result.Status);
        Assert.NotNull(result.Warning);
        Assert.False(result.Config.Journal.StoreSensitive);
        Assert.False(result.Config.Dashboard.AutoStart);
        Assert.False(result.Config.Diagnostics.Enabled);
        Assert.True(result.Config.Journal.Enabled);
    }

    [Fact]
    public void Oversized_file_is_invalid()
    {
        File.WriteAllText(ConfigPath, "{ \"journal\": { \"storeSensitive\": true } }" + new string(' ', 70_000));

        Assert.Equal(SqlHarnessConfigStatus.Invalid, SqlHarnessConfigLoader.Load(ConfigPath).Status);
    }

    [Fact]
    public void Warning_does_not_echo_file_content_or_path()
    {
        File.WriteAllText(ConfigPath, """{ "secretish": "SQLH_CONFIG_MARKER" }""");

        var warning = SqlHarnessConfigLoader.Load(ConfigPath).Warning!;

        Assert.DoesNotContain("SQLH_CONFIG_MARKER", warning);
        Assert.DoesNotContain(_dir, warning);
    }

    [Fact]
    public void Parse_reports_out_of_range_fields_by_path()
    {
        var result = SqlHarnessConfigLoader.Parse("""
            { "journal": { "enabled": true, "storeSensitive": false,
                           "retention": { "enabled": true, "maxAgeDays": 0, "maxSizeMb": 5 } },
              "dashboard": { "autoStart": false, "port": 47800, "idleShutdownHours": 999 } }
            """u8.ToArray());

        Assert.Null(result.Config);
        Assert.Equal(
            ["journal.retention.maxAgeDays", "journal.retention.maxSizeMb", "dashboard.idleShutdownHours"],
            result.Errors.Select(error => error.Field));
    }

    [Fact]
    public void Parse_rejects_unknown_fields_and_wrong_types()
    {
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "journal": { "bogus": 1 } }"""u8.ToArray()).Errors);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "journal": { "enabled": "yes" } }"""u8.ToArray()).Errors);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("""{ "journal": null }"""u8.ToArray()).Errors);
        Assert.NotEmpty(SqlHarnessConfigLoader.Parse("not json"u8.ToArray()).Errors);
    }

    [Fact]
    public void Written_config_round_trips_through_the_loader()
    {
        var config = SqlHarnessConfig.Default with
        {
            Journal = new JournalConfig { StoreSensitive = true, Retention = new JournalRetentionConfig { Enabled = true, MaxAgeDays = 7 } },
            Dashboard = new DashboardConfig { AutoStart = true, IdleShutdownHours = 2 },
            Diagnostics = new DiagnosticsConfig { Enabled = true },
            Connection = new ConnectionConfig { TimeoutSeconds = 87 },
        };

        SqlHarnessConfigWriter.Write(ConfigPath, config);
        var loaded = SqlHarnessConfigLoader.Load(ConfigPath);

        Assert.Equal(SqlHarnessConfigStatus.Valid, loaded.Status);
        Assert.Equal(config, loaded.Config);
    }

    [Fact]
    public void Write_replaces_atomically_and_leaves_no_temp_file()
    {
        File.WriteAllText(ConfigPath, "{ broken");

        SqlHarnessConfigWriter.Write(ConfigPath, SqlHarnessConfig.Default);

        Assert.Equal(SqlHarnessConfigStatus.Valid, SqlHarnessConfigLoader.Load(ConfigPath).Status);
        Assert.Equal(["config.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ConfigPath));
    }

    [Fact]
    public void Write_creates_the_home_directory()
    {
        var nested = Path.Combine(_dir, "fresh", "config.json");

        SqlHarnessConfigWriter.Write(nested, SqlHarnessConfig.Default);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Agent_transcripts_default_off_and_round_trip()
    {
        Assert.False(SqlHarnessConfig.Default.Dashboard.AgentTranscripts.Enabled);
        var parsed = SqlHarnessConfigLoader.Parse("""{"dashboard":{"agentTranscripts":{"enabled":true}}}"""u8.ToArray());
        Assert.True(parsed.Config!.Dashboard.AgentTranscripts.Enabled);
        var again = SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(SqlHarnessConfigWriter.Serialize(parsed.Config)));
        Assert.True(again.Config!.Dashboard.AgentTranscripts.Enabled);
    }

    [Theory]
    [InlineData("""{"dashboard":{"agentTranscripts":null}}""")]
    [InlineData("""{"dashboard":{"agentTranscripts":{"enabled":"yes"}}}""")]
    [InlineData("""{"dashboard":{"agentTranscripts":{"enabled":true,"paths":[]}}}""")]
    public void Invalid_agent_transcripts_section_is_rejected(string json)
    {
        Assert.Null(SqlHarnessConfigLoader.Parse(System.Text.Encoding.UTF8.GetBytes(json)).Config);
    }
}