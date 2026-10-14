using Npgsql;

namespace SqlHarness.Core.Postgres;

internal static class PostgresReadOnlyFailure
{
    // Fixed text, never SQL and never a value from the batch. Map answers every
    // SQLSTATE 25006, so the first clause states only the transaction, not a
    // cause: the server refuses a temp-target DML miss, nextval and setval, a
    // volatile function that writes, and setup DDL here alike
    // (plans/047-readonly-proof.md cases 2, 5 and 8). The rest is guidance for
    // the caller who did mean session-local work, not an assertion about which
    // of those it was. The summary therefore names the refused writes instead
    // of claiming the transaction refuses "everything else": advisory locks and
    // pg_sleep succeed under it (cases 9 and 10).
    internal const string RejectionMessage =
        "PostgreSQL rejected a write in a read-only transaction. Query and measured SQL run in that " +
        "transaction, where DML on a temp is allowed and the writes listed next are refused: CREATE TEMP, " +
        "CREATE INDEX, TRUNCATE and DROP, nextval and setval, and a volatile function that writes. " +
        "If the write was meant for a session temp, check the setup that creates it and consider " +
        "qualifying the target as pg_temp.<name>; a persistent write needs an approved mutation.";

    internal static Exception Map(Exception exception) => ContainsReadOnlyViolation(exception)
        ? new SqlHarnessSafetyException(RejectionMessage)
        : exception;

    internal static bool ContainsReadOnlyViolation(Exception exception)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            if (current is PostgresException { SqlState: PostgresErrorCodes.ReadOnlySqlTransaction })
                return true;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
        return false;
    }
}