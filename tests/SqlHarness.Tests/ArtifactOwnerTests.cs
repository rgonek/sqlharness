using SqlHarness.Core;

namespace SqlHarness.Tests;

/// <summary>
/// 003/T3: the trusted publish path stamps the scope owner into the manifest
/// atomically, and the Core reader enforces it opt-in (MCP supplies the frozen
/// scope owner; the offline CLI passes none and keeps reading by name).
/// </summary>
public class ArtifactOwnerTests
{
    private static readonly ArtifactOwner Owner = new(
        "owner-profile",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "acme" },
        "sqlserver",
        "owner-server",
        "ownerdb");

    [Fact]
    public void Publish_stamps_owner_and_reader_enforces_it_before_projection()
    {
        using var temp = new TempDirectory();
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), [Run()], "wind", Owner);
        var id = Path.GetFileName(directory);

        Assert.Contains("\"owner\"", File.ReadAllText(Path.Combine(directory, "manifest.json")), StringComparison.Ordinal);
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary", Owner));

        var refused = Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "summary", Owner with { Profile = "other-profile" }));
        Assert.Equal(SqlHarnessExitCode.Safety, refused.ExitCode);

        // The offline CLI path (no owner) keeps reading by name.
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "summary"));
    }

    [Fact]
    public void Publish_without_owner_keeps_legacy_manifest_and_refuses_scoped_reads()
    {
        using var temp = new TempDirectory();
        var directory = new CompareArtifactWriter(temp.Path, () => DateTimeOffset.UnixEpoch)
            .Write(CompareReport(), [Run()], "wind");
        var id = Path.GetFileName(directory);

        Assert.DoesNotContain("\"owner\"", File.ReadAllText(Path.Combine(directory, "manifest.json")), StringComparison.Ordinal);
        Assert.NotNull(ArtifactReader.ReadSection(temp.Path, id, "metrics"));
        Assert.Throws<ArtifactReadException>(
            () => ArtifactReader.ReadSection(temp.Path, id, "metrics", Owner));
    }

    private static SqlHarnessCompareReport CompareReport() => new(
        new SqlHarnessTargetIdentityReport("owner-server", "ownerdb", "owner-server", "ownerdb", "profile"),
        1, 2, true, Variant("baseline"), Variant("candidate"), null);

    private static CompareVariantReport Variant(string name) => new(
        name,
        new CompareDistribution(1, 2, 3),
        new CompareDistribution(2, 3, 4),
        new CompareDistribution(3, 4, 5),
        new Dictionary<string, long>(),
        [new CompareOperatorReport(1, "Index Seek", "Clients", false, false, false)],
        []);

    private static CompareRunArtifact Run() => new(
        "baseline", 1, 1, 2, 3, new Dictionary<string, long>(), "HASH", [FixturePlan()], 0);

    private static string FixturePlan() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "distiller-sample.sqlplan"));

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-artifact-owner-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}