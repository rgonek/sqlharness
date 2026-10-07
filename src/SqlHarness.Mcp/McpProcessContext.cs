using System.Collections.ObjectModel;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Mcp;

/// <summary>
/// Frozen process policy shared by all MCP requests. Request-scoped mode
/// resolves a fresh immutable target scope for each target-dependent call;
/// fixed mode retains the established startup-resolved scope.
/// </summary>
public sealed class McpProcessContext
{
    private McpProcessContext(
        bool requestScope,
        IReadOnlyDictionary<string, TargetProfile> profiles,
        IReadOnlyList<string> allowedProfiles,
        McpScope? fixedScope,
        IReadOnlyList<string> inputRoots,
        int maxResultBytes,
        int maxOperationSeconds)
    {
        RequestScope = requestScope;
        Profiles = profiles;
        AllowedProfiles = allowedProfiles;
        FixedScope = fixedScope;
        InputRoots = inputRoots;
        MaxResultBytes = maxResultBytes;
        MaxOperationSeconds = maxOperationSeconds;
        Gate = new McpExecutionGate();
    }

    public bool RequestScope { get; }
    public IReadOnlyDictionary<string, TargetProfile> Profiles { get; }
    public IReadOnlyList<string> AllowedProfiles { get; }
    public McpScope? FixedScope { get; }
    public IReadOnlyList<string> InputRoots { get; }
    public int MaxResultBytes { get; }
    public int MaxOperationSeconds { get; }
    public McpExecutionGate Gate { get; }

    private Func<ISqlHarnessModule, ISqlHarnessModule> _decorator = static module => module;

    /// <summary>
    /// Creates process policy from one profile read. Profile definitions and
    /// nested var maps are copied so caller-owned mutable dictionaries cannot
    /// redirect later requests.
    /// </summary>
    public static McpProcessContext Create(
        McpServerOptions options,
        Func<IReadOnlyDictionary<string, TargetProfile>> loadProfiles)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loadProfiles);

        IReadOnlyDictionary<string, TargetProfile> loaded;
        try
        {
            loaded = loadProfiles();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpStartupException("The MCP profile data is unavailable.", exception);
        }

        if (loaded is null)
            throw new McpStartupException("The MCP profile data is unavailable.");

        var profiles = McpScope.FreezeProfiles(loaded);
        var roots = McpScope.ValidateOptions(options);
        var allowedProfiles = ValidateAllowlist(options, profiles);

        if (options.RequestScope)
        {
            if (!string.IsNullOrWhiteSpace(options.Profile) ||
                (options.Vars is not null && options.Vars.Count != 0))
                throw new McpStartupException("The MCP startup mode options are invalid.");

            return new McpProcessContext(
                true,
                profiles,
                allowedProfiles,
                null,
                roots,
                options.MaxResultBytes,
                options.MaxOperationSeconds);
        }

        if (allowedProfiles.Count != 0)
            throw new McpStartupException("The MCP startup mode options are invalid.");
        var fixedScope = McpScope.Create(options, profiles);
        return new McpProcessContext(
            false,
            profiles,
            allowedProfiles,
            fixedScope,
            roots,
            options.MaxResultBytes,
            options.MaxOperationSeconds);
    }

    /// <summary>
    /// Resolves a per-call target against only the immutable startup
    /// snapshot. Fixed mode returns its existing startup scope and rejects a
    /// supplied request scope.
    /// </summary>
    public McpScope ResolveScope(McpRequestScope? request)
    {
        if (!RequestScope)
        {
            if (request is not null)
                throw new McpStartupException("The MCP request scope is invalid.");
            return FixedScope!;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Profile) || request.Vars is null)
            throw new McpStartupException("The MCP request scope is invalid.");
        if (!AllowedProfiles.Contains(request.Profile, StringComparer.Ordinal))
            throw new McpStartupException("The MCP request scope is invalid.");

        Dictionary<string, string> supplied;
        try
        {
            supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Vars)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null || !supplied.TryAdd(key, value))
                    throw new McpStartupException("The MCP request scope is invalid.");
            }
        }
        catch (McpStartupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpStartupException("The MCP request scope is invalid.", exception);
        }

        var profile = Profiles[request.Profile];
        var canonicalVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in profile.Vars.Keys)
        {
            if (!supplied.TryGetValue(key, out var value))
                throw new McpStartupException("The MCP request scope is invalid.");
            canonicalVars.Add(key, value);
        }
        if (supplied.Count != canonicalVars.Count)
            throw new McpStartupException("The MCP request scope is invalid.");

        var targetRequest = new SqlTargetRequest(request.Profile, new ReadOnlyDictionary<string, string>(canonicalVars));
        ResolvedTarget target;
        try
        {
            target = TargetResolver.Resolve(targetRequest, Profiles);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new McpStartupException("The MCP request scope is invalid.", exception);
        }

        if (target.Engine != SqlEngine.SqlServer)
            throw new McpStartupException("The MCP request scope is invalid.");

        return McpScope.CreateResolved(
            targetRequest,
            target,
            InputRoots,
            Profiles,
            MaxResultBytes,
            MaxOperationSeconds);
    }

    /// <summary>Installs the host's module decorator (activity journal); call before wiring tools.</summary>
    public void DecorateModules(Func<ISqlHarnessModule, ISqlHarnessModule> decorator) =>
        _decorator = decorator ?? throw new ArgumentNullException(nameof(decorator));

    public ISqlHarnessModule Decorate(ISqlHarnessModule module) => _decorator(module);

    /// <summary>Composition for target-free tools; it performs no connection.</summary>
    public ISqlHarnessModule CreateModule() => Decorate(new SqlHarnessModule(() => Profiles));

    private static IReadOnlyList<string> ValidateAllowlist(
        McpServerOptions options,
        IReadOnlyDictionary<string, TargetProfile> profiles)
    {
        var supplied = options.AllowedProfiles;
        if (supplied is null || supplied.Count == 0)
        {
            if (options.RequestScope)
                throw new McpStartupException("The MCP allowed-profile list is invalid.");
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(supplied.Count);
        foreach (var name in supplied)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name) || !profiles.TryGetValue(name, out var profile))
                throw new McpStartupException("The MCP allowed-profile list is invalid.");
            if (!string.IsNullOrWhiteSpace(profile.Engine) &&
                !string.Equals(profile.Engine, "sqlserver", StringComparison.OrdinalIgnoreCase))
                throw new McpStartupException("The MCP allowed-profile list is invalid.");
            result.Add(name);
        }

        return Array.AsReadOnly(result.ToArray());
    }
}