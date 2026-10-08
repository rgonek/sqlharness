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
        Assert.False(result.Config.Dashboard.AutoStart);
        Assert.Equal(47800, result.Config.Dashboard.Port);
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
}