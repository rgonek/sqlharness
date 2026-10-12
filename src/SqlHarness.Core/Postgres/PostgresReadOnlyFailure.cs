using Npgsql;

namespace SqlHarness.Core.Postgres;

internal static class PostgresReadOnlyFailure
{
    internal const string RejectionMessage =
        "PostgreSQL rejected a write in a read-only transaction; persistent writes need an approved mutation.";

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
