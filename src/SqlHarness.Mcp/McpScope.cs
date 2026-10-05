using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp;

/// <summary>
/// Startup failure before the MCP handshake. Messages are generic on
/// purpose: they never echo profile names, var values, paths, or secrets.
/// </summary>
public sealed class McpStartupException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Immutable startup scope for one MCP server process. The profile set is
/// read exactly once and the target is resolved once; later profile-file
/// edits cannot redirect subsequent calls (spec section 3). There is no
/// reload or switch-target surface: a new target requires a process restart.
/// </summary>
public sealed class McpScope
{
    private McpScope(
        SqlTargetRequest targetRequest,
        ResolvedTarget resolvedTarget,
        ArtifactOwner owner,
        IReadOnlyList<string> inputRoots,
        IReadOnlyDictionary<string, TargetProfile> profiles,
        int maxResultBytes,
        int maxOperationSeconds)
    {
        TargetRequest = targetRequest;
        ResolvedTarget = resolvedTarget;
        Owner = owner;
        InputRoots = inputRoots;
        Profiles = profiles;
        MaxResultBytes = maxResultBytes;
        MaxOperationSeconds = maxOperationSeconds;
    }

    /// <summary>Target request as approved at startup.</summary>
    public SqlTargetRequest TargetRequest { get; }

    /// <summary>Target resolved once at startup and never re-resolved.</summary>
    public ResolvedTarget ResolvedTarget { get; }

    /// <summary>Artifact owner stamped by live publishes and enforced on MCP artifact reads.</summary>
    public ArtifactOwner Owner { get; }

    /// <summary>Normalized absolute input roots (empty means no file inputs).</summary>
    public IReadOnlyList<string> InputRoots { get; }

    /// <summary>
    /// Frozen process-wide cap for one serialized tool response in UTF-8
    /// bytes. Tool calls resolve their effective budget against it and may
    /// only lower it.
    /// </summary>
    public int MaxResultBytes { get; }

    /// <summary>
    /// Frozen process-wide time budget for one database call in seconds.
    /// Tool calls may only lower it; the linked deadline always reaches Core.
    /// </summary>
    public int MaxOperationSeconds { get; }

    /// <summary>Frozen profile snapshot shared by every call in this process.</summary>
    public IReadOnlyDictionary<string, TargetProfile> Profiles { get; }

    /// <summary>
    /// Scoped profile provider for Core. It closes over the frozen snapshot,
    /// so tools never re-read the global store per call.
    /// </summary>
    public Func<IReadOnlyDictionary<string, TargetProfile>> ProfileProvider => () => Profiles;

    /// <summary>
    /// Builds the shared Core composition over the frozen provider: the same
    /// session factories and session policy as the CLI, without duplicating
    /// them. Construction opens no connection and performs no auth.
    /// </summary>
    public ISqlHarnessModule CreateModule() => new SqlHarnessModule(ProfileProvider);

    /// <summary>
    /// Resolves and freezes the startup scope, reading the profile set
    /// exactly once.
    /// </summary>
    public static McpScope Create(
        McpServerOptions options,
        IReadOnlyDictionary<string, TargetProfile>? profiles = null) =>
        Create(options, profiles is null
            ? () => ProfileStore.Load()
            : () => profiles);

    /// <summary>
    /// Resolves and freezes the startup scope using the supplied loader,
    /// which is invoked exactly once.
    /// </summary>
    public static McpScope Create(
        McpServerOptions options,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loadProfiles);

        IReadOnlyDictionary<string, TargetProfile> snapshot;
        try
        {
            snapshot = loadProfiles();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpStartupException("The MCP profile data is unavailable.", exception);
        }

        if (snapshot is null)
            throw new McpStartupException("The MCP profile data is unavailable.");

        if (string.IsNullOrWhiteSpace(options.Profile))
            throw new McpStartupException("The MCP startup profile is invalid.");

        var roots = ValidateInputRoots(options.InputRoots);

        if (options.MaxResultBytes < McpLimits.MinCallToolResultBudgetBytes ||
            options.MaxResultBytes > McpLimits.MaxCallToolResultBudgetBytes)
            throw new McpStartupException("The MCP result budget is invalid.");
        if (options.MaxOperationSeconds < McpLimits.MinOperationSeconds ||
            options.MaxOperationSeconds > McpLimits.MaxOperationSeconds)
            throw new McpStartupException("The MCP operation budget is invalid.");

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.Vars is not null)
        {
            foreach (var (key, value) in options.Vars)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                    throw new McpStartupException("The MCP profile variables are invalid.");
                vars[key] = value;
            }
        }

        var request = new SqlTargetRequest(options.Profile, vars);
        ResolvedTarget resolved;
        try
        {
            resolved = TargetResolver.Resolve(request, snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpStartupException("The MCP profile or variables are invalid.", exception);
        }

        return new McpScope(request, resolved, ArtifactOwner.From(request, resolved), roots, snapshot, options.MaxResultBytes, options.MaxOperationSeconds);
    }

    private static IReadOnlyList<string> ValidateInputRoots(IReadOnlyList<string>? roots)
    {
        if (roots is null || roots.Count == 0)
            return [];
        var normalized = new List<string>(roots.Count);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) ||
                !Path.IsPathFullyQualified(root) ||
                !Directory.Exists(root))
            {
                throw new McpStartupException("The MCP input roots are invalid.");
            }

            var normalizedRoot = McpInputRoots.NormalizeRoot(root);
            if (McpInputRoots.IsFilesystemRoot(normalizedRoot))
                throw new McpStartupException("The MCP input roots are invalid.");
            normalized.Add(normalizedRoot);
        }

        return normalized;
    }
}