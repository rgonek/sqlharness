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

public sealed record SqlHarnessConfigFieldError(string Field, string Message);

public sealed record SqlHarnessConfigParseResult(SqlHarnessConfig? Config, IReadOnlyList<SqlHarnessConfigFieldError> Errors);

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

            var parsed = Parse(File.ReadAllBytes(path));
            return parsed.Config is { } config
                ? new SqlHarnessConfigLoadResult(config, SqlHarnessConfigStatus.Valid, null)
                : Invalid();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Invalid();
        }
    }

    /// <summary>Strict parse of one config document; the config is returned only when there are no errors.</summary>
    public static SqlHarnessConfigParseResult Parse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumBytes)
            return Failed("$", "The settings document is larger than 64 KiB.");
        SqlHarnessConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SqlHarnessConfig>(json, Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            var path = (exception as JsonException)?.Path;
            return Failed(string.IsNullOrEmpty(path) ? "$" : path.TrimStart('$', '.'), "Unknown field or invalid value.");
        }

        if (config is null || !IsComplete(config))
            return Failed("$", "The journal, journal.retention and dashboard sections must be objects.");

        var errors = new List<SqlHarnessConfigFieldError>();
        if (config.Journal.Retention.MaxAgeDays is < 1 or > 3650)
            errors.Add(new("journal.retention.maxAgeDays", "Must be between 1 and 3650."));
        if (config.Journal.Retention.MaxSizeMb is < 10 or > 102_400)
            errors.Add(new("journal.retention.maxSizeMb", "Must be between 10 and 102400."));
        if (config.Dashboard.Port is < 1024 or > 65535)
            errors.Add(new("dashboard.port", "Must be between 1024 and 65535."));
        if (config.Dashboard.IdleShutdownHours is < 1 or > 168)
            errors.Add(new("dashboard.idleShutdownHours", "Must be between 1 and 168."));
        return errors.Count == 0 ? new(config, []) : new(null, errors);
    }

    private static SqlHarnessConfigParseResult Failed(string field, string message) => new(null, [new(field, message)]);

    private static SqlHarnessConfigLoadResult Invalid() =>
        new(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Invalid, InvalidWarning);

    // Explicit JSON nulls bypass initializers; treat them as invalid.
    private static bool IsComplete(SqlHarnessConfig config) =>
        config.Journal is not null && config.Journal.Retention is not null && config.Dashboard is not null;
}