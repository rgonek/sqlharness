using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHarness.Core;

public sealed record JournalRetentionConfig
{
    public bool Enabled { get; init; }
    public int MaxAgeDays { get; init; } = 30;
    public int MaxSizeMb { get; init; } = 500;
}

public sealed record JournalConfig
{
    public bool Enabled { get; init; } = true;
    public bool StoreSensitive { get; init; }
    public JournalRetentionConfig Retention { get; init; } = new();
}

public sealed record DashboardConfig
{
    public bool AutoStart { get; init; }
    public int Port { get; init; } = 47800;
    public int IdleShutdownHours { get; init; } = 8;
}

/// <summary>Operator settings from <c>~/.sqlharness/config.json</c>; tool behavior only, never targets.</summary>
public sealed record SqlHarnessConfig
{
    public JournalConfig Journal { get; init; } = new();
    public DashboardConfig Dashboard { get; init; } = new();

    public static SqlHarnessConfig Default { get; } = new();
}

public enum SqlHarnessConfigStatus
{
    Missing,
    Valid,
    Invalid,
}

public sealed record SqlHarnessConfigLoadResult(
    SqlHarnessConfig Config,
    SqlHarnessConfigStatus Status,
    string? Warning);

/// <summary>
/// Strict, fail-closed reader: any unreadable, malformed, unknown-field, or
/// out-of-range file yields <see cref="SqlHarnessConfig.Default"/>, so an
/// invalid file can never enable sensitive storage or autostart.
/// </summary>
public static class SqlHarnessConfigLoader
{
    internal const int MaximumBytes = 64 * 1024;

    internal const string InvalidWarning =
        "sqlharness: config.json is invalid; using defaults (journal hash-only, no autostart, no retention).";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static SqlHarnessConfigLoadResult Load() => Load(SqlHarnessPaths.ConfigFile);

    public static SqlHarnessConfigLoadResult Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new SqlHarnessConfigLoadResult(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Missing, null);
            if (new FileInfo(path).Length > MaximumBytes)
                return Invalid();

            var config = JsonSerializer.Deserialize<SqlHarnessConfig>(File.ReadAllBytes(path), Options);
            return config is not null && IsComplete(config) && IsInRange(config)
                ? new SqlHarnessConfigLoadResult(config, SqlHarnessConfigStatus.Valid, null)
                : Invalid();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Invalid();
        }
    }

    private static SqlHarnessConfigLoadResult Invalid() =>
        new(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Invalid, InvalidWarning);

    // Explicit JSON nulls bypass initializers; treat them as invalid.
    private static bool IsComplete(SqlHarnessConfig config) =>
        config.Journal is not null && config.Journal.Retention is not null && config.Dashboard is not null;

    private static bool IsInRange(SqlHarnessConfig config) =>
        config.Journal.Retention.MaxAgeDays is >= 1 and <= 3650
        && config.Journal.Retention.MaxSizeMb is >= 10 and <= 102_400
        && config.Dashboard.Port is >= 1024 and <= 65535
        && config.Dashboard.IdleShutdownHours is >= 1 and <= 168;
}