using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

/// <summary>
/// Injectable clock for MCP execution paths. Production uses the system
/// clock; tests inject a manually advanced clock so progress throttling and
/// deadlines are asserted without performance-dependent sleeps.
/// </summary>
public interface IMcpClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>System-clock implementation of <see cref="IMcpClock"/>.</summary>
public sealed class SystemMcpClock : IMcpClock
{
    public static readonly SystemMcpClock Instance = new();

    private SystemMcpClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Concurrency gate for one MCP server process (spec section 7): at most one
/// active database operation at a time. The database tools are query,
/// measure, compare, watch, snapshot, and inspect; a matrix or a parameter-set batch
/// travels inside its single compare/measure call, so it counts as one
/// operation. A second concurrent database call is rejected immediately with
/// a stable BUSY result: no queue, no retry-after, the client decides.
/// Safe local tools (capabilities, validate, plan,
/// artifact, gain) run in parallel as long as they share no mutable request
/// state: every call builds its own Core operation records, so there is no
/// shared reader, temp, or parameter state between calls.
/// </summary>
public sealed class McpExecutionGate
{
    /// <summary>Stable error code for a rejected concurrent database call.</summary>
    public const string BusyCode = "busy";

    /// <summary>Stable error code for a cancelled call.</summary>
    public const string CancelledCode = "cancelled";

    /// <summary>Static BUSY message: no payload, identical on every rejection.</summary>
    public const string BusyMessage = "Another database operation is already running.";

    /// <summary>Static cancellation message: no payload, never an exit 7.</summary>
    public const string CancelledMessage = "The operation was cancelled.";

    private static readonly HashSet<string> DbTools = new(StringComparer.Ordinal)
    {
        "sqlharness_query",
        "sqlharness_measure",
        "sqlharness_compare",
        "sqlharness_watch",
        "sqlharness_snapshot",
        "sqlharness_inspect",
    };

    private readonly SemaphoreSlim _database = new(1, 1);

    /// <summary>
    /// True for the tools that open a database connection and therefore run
    /// under the gate. Everything else is discovery or a safe local tool.
    /// </summary>
    public static bool IsDbTool(string? toolName) =>
        toolName is not null && DbTools.Contains(toolName);

    /// <summary>
    /// Enters the single database slot without waiting. Returns false when
    /// another database operation already holds it; the caller must then
    /// return <see cref="BusyResult"/> instead of executing.
    /// </summary>
    public bool TryEnterDb() => _database.Wait(TimeSpan.Zero);

    /// <summary>Releases the database slot. Callers release on every path.</summary>
    public void ExitDb() => _database.Release();

    /// <summary>
    /// Stable BUSY rejection: the call was not executed, so
    /// <c>IsError</c> is true, the exit code is Safety (2), the error code is
    /// <c>busy</c>, and the message is static.
    /// </summary>
    public static CallToolResult BusyResult(string command, McpResultBudget? budget = null) =>
        McpResultAdapter.Adapt(
            new SqlHarnessOutcome(
                SqlHarnessExitCode.Safety,
                null,
                null,
                null,
                new SqlHarnessError(BusyCode, "execution", BusyMessage)),
            command,
            budget ?? new McpResultBudget());

    /// <summary>
    /// Stable cancellation result: the call did not run to completion, so
    /// <c>IsError</c> is true. A cancelled call is never reported with the
    /// natural watch-deadline exit code 7.
    /// </summary>
    public static CallToolResult CancelledResult(string command, McpResultBudget? budget = null) =>
        McpResultAdapter.Adapt(
            new SqlHarnessOutcome(
                SqlHarnessExitCode.SqlExecution,
                null,
                null,
                null,
                new SqlHarnessError(CancelledCode, "execution", CancelledMessage)),
            command,
            budget ?? new McpResultBudget());
}

/// <summary>
/// Protocol progress for long database calls (spec section 7). A report is
/// sent only when the client supplied a progress token on this request;
/// without a token the reporter is a no-op. At most one notification per
/// second per call goes out, and the payload carries only the tool command
/// and the stage: never SQL, parameters, or rows. Transport is the protocol
/// notification channel on the calling session, never stdout (watch NDJSON
/// stays a CLI-only path).
/// </summary>
public sealed class McpProgressReporter(IMcpClock clock)
{
    /// <summary>Minimum interval between two progress notifications of one call.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>Stage reported when a database call starts executing.</summary>
    public const string Started = "started";

    /// <summary>Stage reported when a database call finishes executing.</summary>
    public const string Finished = "finished";

    private readonly IMcpClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private DateTimeOffset? _lastReport;

    /// <summary>
    /// Pure throttle decision: the first report always goes out, then at most
    /// one per <see cref="MinInterval"/>. A clock running backwards suppresses
    /// rather than flooding.
    /// </summary>
    public static bool ShouldSend(DateTimeOffset now, DateTimeOffset? lastReport) =>
        !lastReport.HasValue || now - lastReport.Value >= MinInterval;

    /// <summary>
    /// Sends one throttled stage notification when the request carries a
    /// progress token. Notification failures are swallowed: progress is
    /// advisory and the final tool result stays authoritative. A cancelled
    /// token propagates so cancellation keeps its stable result path.
    /// </summary>
    public async Task ReportAsync(
        RequestContext<CallToolRequestParams>? context,
        string command,
        string stage,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        var token = context?.Params?.ProgressToken;
        if (token is null)
            return;
        var now = _clock.UtcNow;
        if (!ShouldSend(now, _lastReport))
            return;
        _lastReport = now;
        try
        {
            await context!.Server.NotifyProgressAsync(
                new ProgressNotificationParams
                {
                    ProgressToken = token.Value,
                    Progress = new ProgressNotificationValue
                    {
                        // Coarse stage fraction only: repeat-level progress
                        // would need a Core seam, which MCP v1 does not add.
                        Progress = string.Equals(stage, Finished, StringComparison.Ordinal) ? 1 : 0,
                        Total = 1,
                        Message = command + " " + stage,
                    },
                },
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Advisory only: a dead progress channel must not fail the call.
        }
    }
}