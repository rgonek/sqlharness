using System.Text.Json;

namespace SqlHarness.Core;

/// <summary>
/// Writes <c>config.json</c> so readers see either the old or the new complete file:
/// a temp file in the same directory is written, made owner-only, then moved over the target.
/// </summary>
public static class SqlHarnessConfigWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Serialize(SqlHarnessConfig config) => JsonSerializer.Serialize(config, Options);

    public static void Write(string path, SqlHarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".config-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, Serialize(config) + Environment.NewLine);
            OwnerOnlyFiles.File(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}