using System.Text.Json;
using System.Text.Json.Serialization;

using SqlHarness.Core.Mutations;

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
    public bool SafetyNotifications { get; init; }
    public AgentTranscriptsConfig AgentTranscripts { get; init; } = new();
}

public sealed record AgentTranscriptsConfig
{
    /// <summary>Read local Claude/Codex transcripts for titles, model and effort. Off by default.</summary>
    public bool Enabled { get; init; }
}

public sealed record CompareConfig
{
    public bool RequireContext { get; init; } = true;
}

public sealed record DiagnosticsConfig
{
    public bool Enabled { get; init; }
}

public sealed record MutationsConfig
{
    public string DefaultMode { get; init; } = "never";
    public string Verification { get; init; } = "click";
    public int ApprovalTimeoutSeconds { get; init; } = 120;
}

public sealed record ConnectionConfig
{
    public const int MinimumTimeoutSeconds = 1;
    public const int MaximumTimeoutSeconds = 300;
    public const int DefaultTimeoutSeconds = 30;

    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
}

/// <summary>Operator settings from <c>~/.sqlharness/config.json</c>; tool behavior only, never targets.</summary>
public sealed record SqlHarnessConfig
{
    public JournalConfig Journal { get; init; } = new();
    public DashboardConfig Dashboard { get; init; } = new();
    public CompareConfig Compare { get; init; } = new();
    public DiagnosticsConfig Diagnostics { get; init; } = new();
    public MutationsConfig Mutations { get; init; } = new();
    public ConnectionConfig Connection { get; init; } = new();

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
        "sqlharness: config.json is invalid; using defaults (journal hash-only, diagnostics disabled, no autostart, no retention).";

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
            if (parsed.Config is { } config)
                return new SqlHarnessConfigLoadResult(config, SqlHarnessConfigStatus.Valid, null);
            var verificationUnsupported = parsed.Errors.Any(error => error.Field == "mutations.verification");
            return Invalid(verificationUnsupported
                ? "sqlharness: config.json mutations.verification is unsupported; using defaults (click is the only supported verification)."
                : null);
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
            return Failed("$", "The journal, journal.retention, dashboard, dashboard.agentTranscripts, compare, diagnostics, mutations and connection sections must be objects.");

        var errors = new List<SqlHarnessConfigFieldError>();
        if (config.Journal.Retention.MaxAgeDays is < 1 or > 3650)
            errors.Add(new("journal.retention.maxAgeDays", "Must be between 1 and 3650."));
        if (config.Journal.Retention.MaxSizeMb is < 10 or > 102_400)
            errors.Add(new("journal.retention.maxSizeMb", "Must be between 10 and 102400."));
        if (config.Dashboard.Port is < 1024 or > 65535)
            errors.Add(new("dashboard.port", "Must be between 1024 and 65535."));
        if (config.Dashboard.IdleShutdownHours is < 1 or > 168)
            errors.Add(new("dashboard.idleShutdownHours", "Must be between 1 and 168."));
        if (!Enum.GetNames<MutationMode>().Any(name => string.Equals(name, config.Mutations.DefaultMode, StringComparison.OrdinalIgnoreCase)))
            errors.Add(new("mutations.defaultMode", "Must be never, operator or yolo."));
        if (!string.Equals(config.Mutations.Verification, "click", StringComparison.OrdinalIgnoreCase))
            errors.Add(new("mutations.verification", "Unsupported verification; click is the only supported value."));
        if (config.Mutations.ApprovalTimeoutSeconds is < 30 or > 600)
            errors.Add(new("mutations.approvalTimeoutSeconds", "Must be between 30 and 600."));
        if (config.Connection.TimeoutSeconds is < ConnectionConfig.MinimumTimeoutSeconds or > ConnectionConfig.MaximumTimeoutSeconds)
            errors.Add(new("connection.timeoutSeconds", $"Must be between {ConnectionConfig.MinimumTimeoutSeconds} and {ConnectionConfig.MaximumTimeoutSeconds}."));
        return errors.Count == 0 ? new(config, []) : new(null, errors);
    }

    private static SqlHarnessConfigParseResult Failed(string field, string message) => new(null, [new(field, message)]);

    private static SqlHarnessConfigLoadResult Invalid(string? warning = null) =>
        new(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Invalid, warning ?? InvalidWarning);

    // Explicit JSON nulls bypass initializers; treat them as invalid.
    private static bool IsComplete(SqlHarnessConfig config) =>
        config.Journal is not null && config.Journal.Retention is not null && config.Dashboard is not null &&
        config.Dashboard.AgentTranscripts is not null && config.Compare is not null && config.Diagnostics is not null &&
        config.Mutations is not null && config.Connection is not null;
}