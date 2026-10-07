using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class ArtifactStoragePreflightTests
{
    [Fact]
    public void Check_creates_missing_root_and_leaves_no_probe()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "missing");

        new ArtifactStoragePreflight().Check(root);

        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Concurrent_checks_preserve_existing_artifacts()
    {
        using var temp = new TempDirectory();
        var existing = Path.Combine(temp.Path, "report.json");
        File.WriteAllText(existing, "existing artifact");

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            new ArtifactStoragePreflight().Check(temp.Path))));

        Assert.Equal("existing artifact", File.ReadAllText(existing));
        Assert.Equal([existing], Directory.GetFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("directory")]
    [InlineData("write")]
    [InlineData("move")]
    [InlineData("delete-file")]
    [InlineData("delete-directory")]
    public void Denied_filesystem_step_reports_the_root(string step)
    {
        using var temp = new TempDirectory();
        var failure = new UnauthorizedAccessException("denied operation");
        var check = new ArtifactStoragePreflight(
            createDirectory: path =>
            {
                if (step == "root" || step == "directory" && path != temp.Path) throw failure;
                Directory.CreateDirectory(path);
            },
            writeText: (path, text, encoding) =>
            {
                if (step == "write") throw failure;
                File.WriteAllText(path, text, encoding);
            },
            moveDirectory: (source, destination) =>
            {
                if (step == "move") throw failure;
                Directory.Move(source, destination);
            },
            deleteFile: path =>
            {
                if (step == "delete-file") throw failure;
                File.Delete(path);
            },
            deleteDirectory: (path, recursive) =>
            {
                if (step == "delete-directory") throw failure;
                Directory.Delete(path, recursive);
            });

        var exception = Assert.Throws<ArtifactStoragePreflightException>(() => check.Check(temp.Path));

        Assert.Equal(temp.Path, exception.Root);
        Assert.Same(failure, exception.InnerException);
        if (step is not ("delete-file" or "delete-directory"))
            Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Cleanup_failure_does_not_mask_write_failure()
    {
        using var temp = new TempDirectory();
        var original = new IOException("write failed");
        var check = new ArtifactStoragePreflight(
            writeText: (_, _, _) => throw original,
            deleteDirectory: (_, _) => throw new UnauthorizedAccessException("cleanup denied"));

        var exception = Assert.Throws<ArtifactStoragePreflightException>(() => check.Check(temp.Path));

        Assert.Same(original, exception.InnerException);
    }

    [Fact]
    public void Error_redacts_secrets_from_inner_failure_and_storage_location()
    {
        var secret = "private-parameter-sentinel";
        var root = Path.Combine(Path.GetTempPath(), secret);
        var exception = new ArtifactStoragePreflightException(root, new IOException("failed " + secret));

        var error = exception.ToError([secret]);

        Assert.DoesNotContain(secret, error.Message);
        Assert.DoesNotContain(secret, error.Location!.Path!);
        Assert.Contains("[REDACTED]", error.Message);
        Assert.Contains("[REDACTED]", error.Location.Path);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "artifact-preflight-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}