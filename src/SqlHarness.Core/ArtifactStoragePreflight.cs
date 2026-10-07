using System.Text;

namespace SqlHarness.Core;

/// <summary>Checks the filesystem operations used to publish a benchmark without touching existing artifacts.</summary>
internal sealed class ArtifactStoragePreflight
{
    private readonly Action<string> _createDirectory;
    private readonly Action<string, string, Encoding> _writeText;
    private readonly Action<string, string> _moveDirectory;
    private readonly Action<string> _deleteFile;
    private readonly Action<string, bool> _deleteDirectory;

    internal ArtifactStoragePreflight(
        Action<string>? createDirectory = null,
        Action<string, string, Encoding>? writeText = null,
        Action<string, string>? moveDirectory = null,
        Action<string>? deleteFile = null,
        Action<string, bool>? deleteDirectory = null)
    {
        _createDirectory = createDirectory ?? (path => Directory.CreateDirectory(path));
        _writeText = writeText ?? File.WriteAllText;
        _moveDirectory = moveDirectory ?? Directory.Move;
        _deleteFile = deleteFile ?? File.Delete;
        _deleteDirectory = deleteDirectory ?? Directory.Delete;
    }

    internal void Check(string root)
    {
        root = Path.GetFullPath(root);
        var probe = Path.Combine(root, ".preflight-" + Guid.NewGuid().ToString("N"));
        var moved = probe + ".published";
        try
        {
            Exception? primary = null;
            string? createdDirectory = null;
            try
            {
                _createDirectory(root);
                _createDirectory(probe);
                createdDirectory = probe;
                _writeText(Path.Combine(probe, "probe.txt"), "SQLHarness storage preflight", new UTF8Encoding(false));
                _moveDirectory(probe, moved);
                createdDirectory = moved;
            }
            catch (Exception exception)
            {
                primary = exception;
                throw;
            }
            finally
            {
                if (createdDirectory is not null)
                    OperationFailureMapper.CompleteCleanup(primary, () => Cleanup(createdDirectory));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactStoragePreflightException(root, exception);
        }
    }

    private void Cleanup(string directory)
    {
        _deleteFile(Path.Combine(directory, "probe.txt"));
        _deleteDirectory(directory, false);
    }
}

internal sealed class ArtifactStoragePreflightException(string root, Exception inner)
    : IOException("Artifact storage preflight failed.", inner)
{
    internal string Root { get; } = root;

    internal SqlHarnessError ToError(IReadOnlyList<string> knownSecrets) => new(
        "local_storage_failed",
        "artifact-preflight",
        SecretRedactor.Redact(this, knownSecrets),
        "Check create, write, rename and delete permissions on the artifact directory for the process account. SQLHarness does not change permissions automatically.",
        new SqlHarnessErrorLocation(Path: SecretRedactor.Redact(Root, knownSecrets)));
}