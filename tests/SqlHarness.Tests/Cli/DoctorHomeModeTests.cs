using System.Text.Json;

using SqlHarness.Cli;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class DoctorHomeModeTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-doctor-home-" + Guid.NewGuid().ToString("N"));

    public DoctorHomeModeTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, true);
        }
    }

    [Fact]
    public async Task Doctor_warns_when_an_existing_home_is_broad()
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(_home, (UnixFileMode)0b111_101_101);

        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("homePresent").GetBoolean());
        Assert.False(root.GetProperty("homePrivate").GetBoolean());
        var warnings = root.GetProperty("warnings").EnumerateArray().ToArray();
        Assert.Single(warnings);
        Assert.Contains("chmod 700", warnings[0].GetString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(_home, warnings[0].GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_warn_about_a_private_home()
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(_home, (UnixFileMode)0b111_000_000);

        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.True(root.GetProperty("homePresent").GetBoolean());
        Assert.True(root.GetProperty("homePrivate").GetBoolean());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Doctor_reports_a_missing_home_without_creating_it()
    {
        Directory.Delete(_home, true);

        var output = new StringWriter();
        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.False(root.GetProperty("homePresent").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("homePrivate").ValueKind);
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.False(Directory.Exists(_home));
    }

    [Fact]
    public async Task Doctor_reports_the_home_path()
    {
        var output = new StringWriter();

        var exitCode = await SqlHarnessCli.Create(new NoopModule(), output).RunAsync(["doctor", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(_home, document.RootElement.GetProperty("homePath").GetString());
    }

    private sealed class NoopModule : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
    }
}