using Npgsql;

namespace SqlHarness.Core.Postgres;

internal static class PostgresReadOnlyFailure
{
    // Fixed text, never SQL and never a name from the batch: a DML target whose
    // qualification the classifier no longer proves reaches the server, so this
    // is the only place the caller learns that the name resolved to a persistent
    // table (plans/047-readonly-proof.md case 6: search_path decides, not the
    // spelling). pg_temp is named as the remedy because it is the one qualifier
    // that pins the session temp schema.
    internal const string RejectionMessage =
        "PostgreSQL rejected a write in a read-only transaction: the write target resolved to a persistent " +
        "table rather than this session's temp schema. Qualify a session temp as pg_temp.<name>; a " +
        "persistent write needs an approved mutation.";

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