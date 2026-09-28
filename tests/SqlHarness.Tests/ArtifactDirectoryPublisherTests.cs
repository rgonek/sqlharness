using System.Text;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class ArtifactDirectoryPublisherTests
{
    [Fact]
    public void Publish_creates_unique_directories_and_hands_staging_to_the_writer()
    {
        using var temp = new TempDirectory();
        var publisher = new ArtifactDirectoryPublisher(temp.Path, () => DateTimeOffset.UnixEpoch);

        string? seenStaging = null;
        var first = publisher.Publish("wind", (staging, directory) =>
        {
            seenStaging = staging;
            Assert.NotEqual(staging, directory);
            File.WriteAllText(Path.Combine(staging, "report.json"), directory, Encoding.UTF8);
        });
        var second = publisher.Publish("wind", (staging, directory) =>
            File.WriteAllText(Path.Combine(staging, "report.json"), directory, Encoding.UTF8));

        Assert.NotNull(seenStaging);
        Assert.NotEqual(first, second);
        Assert.StartsWith(Path.GetFullPath(temp.Path), Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first, File.ReadAllText(Path.Combine(first, "report.json")));
        Assert.Equal(second, File.ReadAllText(Path.Combine(second, "report.json")));
        Assert.DoesNotContain(
            Directory.GetDirectories(temp.Path),
            path => Path.GetFileName(path).Contains(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public void Publish_sanitizes_unsafe_targets_and_never_leaks_them_into_names()
    {
        using var temp = new TempDirectory();
        var publisher = new ArtifactDirectoryPublisher(temp.Path, () => DateTimeOffset.UnixEpoch);

        var first = publisher.Publish("wind/../../unsafe", (staging, _) => WriteMarker(staging));
        var fallback = publisher.Publish("../../", (staging, _) => WriteMarker(staging));
        var secret = publisher.Publish("db-secret=abc123", (staging, _) => WriteMarker(staging));

        AssertSafeChild(temp.Path, first, "wind-unsafe");
        AssertSafeChild(temp.Path, fallback, "target");
        AssertSafeChild(temp.Path, secret, "db-secret-abc123");
        Assert.DoesNotContain("..", Path.GetFileName(first), StringComparison.Ordinal);
    }

    [Fact]
    public void First_write_failure_leaves_no_final_directory()
    {
        using var temp = new TempDirectory();
        var publisher = new ArtifactDirectoryPublisher(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) => throw new IOException("first failure"));

        var noBom = new UTF8Encoding(false);
        var exception = Assert.Throws<IOException>(() =>
            publisher.Publish("wind", (staging, _) => publisher.WriteText(Path.Combine(staging, "a.json"), "marker", noBom)));

        Assert.Equal("first failure", exception.Message);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Subsequent_write_failure_leaves_no_final_directory()
    {
        using var temp = new TempDirectory();
        var calls = 0;
        var publisher = new ArtifactDirectoryPublisher(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                calls++;
                if (calls == 2)
                    throw new IOException("second failure");
                File.WriteAllText(path, content, encoding);
            });

        var noBom = new UTF8Encoding(false);
        var exception = Assert.Throws<IOException>(() =>
            publisher.Publish("wind", (staging, _) =>
            {
                publisher.WriteText(Path.Combine(staging, "a.json"), "marker", noBom);
                publisher.WriteText(Path.Combine(staging, "b.json"), "marker", noBom);
            }));

        Assert.Equal("second failure", exception.Message);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Move_failure_leaves_no_final_directory_and_preserves_the_original()
    {
        using var temp = new TempDirectory();
        var publisher = new ArtifactDirectoryPublisher(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            File.WriteAllText,
            (_, _) => throw new IOException("move failure"));

        var exception = Assert.Throws<IOException>(() =>
            publisher.Publish("wind", (staging, _) => WriteMarker(staging)));

        Assert.Equal("move failure", exception.Message);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Move_that_publishes_then_throws_removes_the_final_directory()
    {
        using var temp = new TempDirectory();
        var publisher = new ArtifactDirectoryPublisher(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            File.WriteAllText,
            (source, destination) =>
            {
                Directory.Move(source, destination);
                throw new IOException("move failure after publish");
            });

        var exception = Assert.Throws<IOException>(() =>
            publisher.Publish("wind", (staging, _) => WriteMarker(staging)));

        Assert.Equal("move failure after publish", exception.Message);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void Cleanup_failures_preserve_the_original_and_attempt_remaining_paths()
    {
        using var temp = new TempDirectory();
        var fileDeletes = new List<string>();
        var directoryDeletes = new List<string>();
        var publisher = new ArtifactDirectoryPublisher(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            (path, content, encoding) =>
            {
                File.WriteAllText(path, content, encoding);
                if (path.EndsWith("b.json", StringComparison.Ordinal))
                    throw new IOException("original failure");
            },
            Directory.Move,
            null,
            path =>
            {
                fileDeletes.Add(path);
                if (path.EndsWith("a.json", StringComparison.Ordinal))
                    throw new IOException("delete failure");
                File.Delete(path);
            },
            (path, recursive) =>
            {
                directoryDeletes.Add(path);
                Directory.Delete(path, recursive);
            });

        var noBom = new UTF8Encoding(false);
        var exception = Assert.Throws<IOException>(() =>
            publisher.Publish("wind", (staging, _) =>
            {
                publisher.WriteText(Path.Combine(staging, "a.json"), "marker", noBom);
                publisher.WriteText(Path.Combine(staging, "b.json"), "marker", noBom);
            }));

        Assert.Equal("original failure", exception.Message);
        Assert.Contains(fileDeletes, path => path.EndsWith("a.json", StringComparison.Ordinal));
        Assert.Contains(fileDeletes, path => path.EndsWith("b.json", StringComparison.Ordinal));
        Assert.Contains(directoryDeletes, path => path.Contains(".staging-", StringComparison.Ordinal));
        AssertNoPublishedDirectory(temp.Path);
    }

    [Fact]
    public void Success_path_is_not_visible_before_the_move_completes()
    {
        using var temp = new TempDirectory();
        string? movedDestination = null;
        var publisher = new ArtifactDirectoryPublisher(
            temp.Path,
            () => DateTimeOffset.UnixEpoch,
            File.WriteAllText,
            (source, destination) =>
            {
                Assert.False(Directory.Exists(destination));
                Assert.True(File.Exists(Path.Combine(source, "report.json")));
                movedDestination = destination;
                Directory.Move(source, destination);
            });

        var directory = publisher.Publish("wind", (staging, _) => WriteMarker(staging, "report.json"));

        Assert.Equal(directory, movedDestination);
        Assert.True(File.Exists(Path.Combine(directory, "report.json")));
    }

    [Fact]
    public void Parallel_publishes_with_one_clock_create_distinct_complete_directories()
    {
        using var temp = new TempDirectory();
        var publisher = new ArtifactDirectoryPublisher(temp.Path, () => DateTimeOffset.UnixEpoch);
        var directories = new string[16];

        Parallel.For(0, directories.Length, index =>
        {
            directories[index] = publisher.Publish("wind", (staging, directory) =>
                File.WriteAllText(
                    Path.Combine(staging, "report.json"),
                    directory + ":" + index,
                    new UTF8Encoding(false)));
        });

        Assert.Equal(directories.Length, directories.Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < directories.Length; index++)
        {
            var directory = directories[index];
            Assert.StartsWith(Path.GetFullPath(temp.Path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(directory + ":" + index, File.ReadAllText(Path.Combine(directory, "report.json")));
        }

        Assert.DoesNotContain(
            Directory.GetDirectories(temp.Path),
            path => Path.GetFileName(path).Contains(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public void Null_arguments_throw_before_touching_the_filesystem()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlharness-publisher-" + Guid.NewGuid().ToString("N"));
        var publisher = new ArtifactDirectoryPublisher(root, () => DateTimeOffset.UnixEpoch);

        Assert.Throws<ArgumentNullException>(() => publisher.Publish(null!, (staging, _) => WriteMarker(staging)));
        Assert.Throws<ArgumentNullException>(() => publisher.Publish("wind", null!));
        Assert.False(Directory.Exists(root));
    }

    private static void WriteMarker(string staging, string name = "report.json") =>
        File.WriteAllText(Path.Combine(staging, name), "marker", Encoding.UTF8);

    private static void AssertSafeChild(string root, string directory, string targetSegment)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDirectory = Path.GetFullPath(directory);
        Assert.StartsWith(fullRoot, fullDirectory, StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileName(fullDirectory);
        Assert.Equal(name, Path.GetRelativePath(fullRoot, fullDirectory));
        var prefix = $"{DateTimeOffset.UnixEpoch:yyyyMMddTHHmmssfffZ}-{targetSegment}-";
        Assert.StartsWith(prefix, name, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{32}$", name[prefix.Length..]);
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, name);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, name);
    }

    private static void AssertNoPublishedDirectory(string root) =>
        Assert.DoesNotContain(
            Directory.GetDirectories(root),
            path => !Path.GetFileName(path).Contains(".staging-", StringComparison.Ordinal));

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sqlharness-publisher-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}