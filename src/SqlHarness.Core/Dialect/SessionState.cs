namespace SqlHarness.Core.Dialect;

internal sealed record SessionState(
    int Options,
    int IsolationLevel,
    int LockTimeout,
    int DateFirst,
    string DateFormat,
    int DeadlockPriority);
