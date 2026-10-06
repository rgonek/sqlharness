using System.Collections.ObjectModel;

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
/// Immutable target scope for one MCP call. Fixed-mode scopes are resolved
/// at startup; request-mode scopes are resolved against the process snapshot
/// for each call. Neither retains caller-mutable profile or var dictionaries.
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

    /// <summary>Closed-profile request resolved for this call.</summary>
    public SqlTargetRequest TargetRequest { get; }

    /// <summary>Target resolved for this immutable call scope.</summary>
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

    /// <summary>Frozen profile snapshot used to resolve this scope.</summary>
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

        var roots = ValidateOptions(options);
        var frozenProfiles = FreezeProfiles(snapshot);

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
            resolved = TargetResolver.Resolve(request, frozenProfiles);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpStartupException("The MCP profile or variables are invalid.", exception);
        }

        return CreateResolved(request, resolved, roots, frozenProfiles, options.MaxResultBytes, options.MaxOperationSeconds);
    }

    internal static McpScope CreateResolved(
        SqlTargetRequest request,
        ResolvedTarget resolved,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, TargetProfile> profiles,
        int maxResultBytes,
        int maxOperationSeconds)
    {
        var frozenVars = new ReadOnlyDictionary<string, string>(
            request.Vars.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        var frozenRequest = request with { Vars = frozenVars };
        return new McpScope(
            frozenRequest,
            resolved,
            ArtifactOwner.From(frozenRequest, resolved),
            Array.AsReadOnly(roots.ToArray()),
            profiles,
            maxResultBytes,
            maxOperationSeconds);
    }

    internal static IReadOnlyList<string> ValidateOptions(McpServerOptions options)
    {
        var roots = ValidateInputRoots(options.InputRoots);
        if (options.MaxResultBytes < McpLimits.MinCallToolResultBudgetBytes ||
            options.MaxResultBytes > McpLimits.MaxCallToolResultBudgetBytes)
            throw new McpStartupException("The MCP result budget is invalid.");
        if (options.MaxOperationSeconds < McpLimits.MinOperationSeconds ||
            options.MaxOperationSeconds > McpLimits.MaxOperationSeconds)
            throw new McpStartupException("The MCP operation budget is invalid.");
        return Array.AsReadOnly(roots.ToArray());
    }

    internal static IReadOnlyDictionary<string, TargetProfile> FreezeProfiles(
        IReadOnlyDictionary<string, TargetProfile> profiles)
    {
        try
        {
            var copy = new Dictionary<string, TargetProfile>(StringComparer.Ordinal);
            foreach (var (name, profile) in profiles)
            {
                if (string.IsNullOrWhiteSpace(name) || profile is null || profile.Vars is null)
                    throw new McpStartupException("The MCP profile data is invalid.");
                var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                var variableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in profile.Vars)
                {
                    if (string.IsNullOrWhiteSpace(key) || value is null ||
                        !variableNames.Add(key) || !vars.TryAdd(key, value))
                        throw new McpStartupException("The MCP profile data is invalid.");
                }
                if (!copy.TryAdd(name, profile with { Vars = new ReadOnlyDictionary<string, string>(vars) }))
                    throw new McpStartupException("The MCP profile data is invalid.");
            }
            return new ReadOnlyDictionary<string, TargetProfile>(copy);
        }
        catch (McpStartupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpStartupException("The MCP profile data is invalid.", exception);
        }
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
