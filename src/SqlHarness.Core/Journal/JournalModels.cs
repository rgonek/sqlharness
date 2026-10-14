namespace SqlHarness.Core;

public enum JournalTransport
{
    Cli,
    Mcp,
}

/// <summary>
/// SQLHarness-derived identity of one agent session. <see cref="SessionKey"/> is
/// computed by SQLHarness (MCP process UUID or CLI process-tree hash); it is not
/// the agent's own conversation id.
/// </summary>
public sealed record SessionIdentity(
    string SessionKey,
    string AgentKind,
    string Source,
    JournalTransport Transport,
    string? ClientName,
    string? ClientVersion,
    string? McpMode,
    int? AgentPid,
    DateTimeOffset? AgentStartedAt,
    int HostPid,
    DateTimeOffset? HostStartedAt,
    string? Cwd,
    string? ClientTitle = null,
    string? RootsJson = null,
    string? ProtocolVersion = null);

/// <summary>Operation metadata known before execution. SQL text is dropped by the journal unless storeSensitive.</summary>
public sealed record OperationStart(
    string Operation,
    string? Profile,
    IReadOnlyDictionary<string, string>? Vars,
    bool MutationRequested,
    string? SqlHash,
    string? CandidateSqlHash,
    string? SqlText,
    string? CandidateSqlText,
    AgentCallMetadata? Agent = null,
    string? OperationRef = null,
    bool DryRun = false)
{
    public CompareContext? Context { get; init; }
    public CompareJournalConditions? CompareConditions { get; init; }
}

/// <summary>Compare conditions belong to this attempt. Values and setup are sensitive and opt-in.</summary>
public sealed record CompareJournalConditions(
    int TimeoutSeconds,
    int Repeat,
    ResultComparisonMode ComparisonMode,
    bool CompareNullability,
    string? SetupSql,
    IReadOnlyList<string> Parameters,
    SqlHarnessParameterMatrixInput? Matrix,
    IReadOnlyList<SqlHarnessParameterSetInput>? ParameterSets,
    IReadOnlyList<SqlHarnessParameterInput>? TypedParameters);

/// <summary>Allowlisted, client-supplied labels from tools/call _meta; never verified, never content.</summary>
public sealed record AgentCallMetadata(
    string? CallId,
    string? Model,
    string? ReasoningEffort,
    string? SessionId,
    string? TurnId,
    string? TurnTrigger,
    string? ThreadSource);

/// <summary>ErrorMessage is a credential-redacted caller-facing diagnostic; SQL text and plans remain opt-in.</summary>
public sealed record OperationEnd(
    string Status,
    int ExitCode,
    string? ErrorKind,
    long DurationMilliseconds,
    string? Engine,
    string? Server,
    string? Database,
    int? ResultSets,
    long? RowsReturned,
    long? RawTokens = null,
    string? ArtifactDirectory = null,
    string? SummaryJson = null,
    string? ErrorMessage = null,
    string? CancelReason = null,
    string? ParentOperationRef = null);

public sealed record JournalHandle(long OperationId);

/// <summary>Progress of a running watch after one completed poll; counts only, never result data.</summary>
public sealed record WatchProgress(int Polls, int ChangedPolls, long ElapsedMilliseconds);