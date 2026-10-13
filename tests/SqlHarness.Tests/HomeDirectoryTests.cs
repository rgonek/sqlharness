using System.Runtime.Versioning;

using SqlHarness.Core;

namespace SqlHarness.Tests;

[Collection(SqlHarnessHomeCollection.Name)]
public sealed class HomeDirectoryTests : IDisposable
{
    private readonly string? _savedHome = Environment.GetEnvironmentVariable("SQLHARNESS_HOME");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sqlharness-home-" + Guid.NewGuid().ToString("N"));

    public HomeDirectoryTests() => Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLHARNESS_HOME", _savedHome);
        if (Directory.Exists(_home))
            Directory.Delete(_home, true);
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void EnsureHomeFor_creates_a_fresh_home_owner_only()
    {
        Assert.False(Directory.Exists(_home));

        SqlHarnessPaths.EnsureHomeFor(Path.Combine(_home, "snapshots"));

        Assert.True(Directory.Exists(_home));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_home));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void EnsureHomeFor_leaves_an_existing_broad_home_unchanged()
    {
        Directory.CreateDirectory(_home);
        File.SetUnixFileMode(_home, (UnixFileMode)0b111_101_101);

        SqlHarnessPaths.EnsureHomeFor(Path.Combine(_home, "snapshots"));

        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(_home));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void EnsureHomeFor_ignores_paths_outside_the_home()
    {
        var other = Path.Combine(Path.GetTempPath(), "sqlharness-not-home-" + Guid.NewGuid().ToString("N"));

        SqlHarnessPaths.EnsureHomeFor(other);

        Assert.False(Directory.Exists(_home));
        Assert.False(Directory.Exists(other));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void A_writer_creates_the_home_owner_only()
    {
        Assert.False(Directory.Exists(_home));

        SqlHarnessConfigWriter.Write(Path.Combine(_home, "config.json"), SqlHarnessConfig.Default);

        Assert.True(File.Exists(Path.Combine(_home, "config.json")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_home));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void HomeMode_reports_the_existing_home_mode()
    {
        Directory.CreateDirectory(_home);
        File.SetUnixFileMode(_home, (UnixFileMode)0b111_101_101);

        var mode = SqlHarnessPaths.HomeMode;

        Assert.NotNull(mode);
        Assert.Equal((UnixFileMode)0b111_101_101, mode!.Value);
    }

    [Fact]
    public void HomeMode_is_null_when_the_home_is_missing()
    {
        Assert.False(Directory.Exists(_home));

        Assert.Null(SqlHarnessPaths.HomeMode);
    }

    [Theory]
    [InlineData(0b111_000_000, true)]
    [InlineData(0b110_000_000, true)]
    [InlineData(0b100_000_000, true)]
    [InlineData(0b111_101_101, false)]
    [InlineData(0b111_000_100, false)]
    [InlineData(0b111_001_000, false)]
    [InlineData(0b000_000_000, true)]
    public void IsOwnerOnly_accepts_only_owner_bits(int mode, bool expected) =>
        Assert.Equal(expected, HomeDirectory.IsOwnerOnly((UnixFileMode)mode));
}