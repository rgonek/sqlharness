using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace SqlHarness.Core.Postgres;

/// <summary>
/// Single PostgreSQL parse entry point for dialect analysis. Classify, measured-batch
/// validation, and parameter-reference validation share this document so they never
/// implement diverging parse rules; each consumer keeps its own allow/deny decision on top.
/// </summary>
internal static class PostgresDocument
{
    internal static bool TryParse(string sql, out Sequence<Statement>? statements)
    {
        try
        {
            statements = new SqlQueryParser().Parse(sql.AsSpan(), new PostgreSqlDialect());
            return true;
        }
        catch (Exception)
        {
            statements = null;
            return false;
        }
    }
}

/// <summary>
/// Single home for PostgreSQL query-shape rules (CTE recursion, SELECT INTO, writes).
/// Previously Classify and ValidateMeasuredBatch each carried their own copy.
/// </summary>
internal static class PostgresQueryShape
{
    internal static bool HasSelectInto(Query query)
    {
        if (query.With is { } with)
        {
            foreach (var cte in with.CteTables)
            {
                if (HasSelectInto(cte.Query))
                    return true;
            }
        }

        return HasSelectInto(query.Body);
    }

    internal static bool HasSelectInto(SetExpression body) => body switch
    {
        SetExpression.SelectExpression selectExpression => selectExpression.Select.Into is not null,
        SetExpression.QueryExpression queryExpression => HasSelectInto(queryExpression.Query),
        SetExpression.SetOperation setOperation =>
            HasSelectInto(setOperation.Left) || HasSelectInto(setOperation.Right),
        SetExpression.Insert insertBody when insertBody.Statement is Statement.Select select =>
            HasSelectInto(select.Query),
        _ => false,
    };

    internal static bool HasWrite(Query query)
    {
        if (query.With is { } with)
        {
            foreach (var cte in with.CteTables)
            {
                if (HasWrite(cte.Query))
                    return true;
            }
        }

        return HasWrite(query.Body);
    }

    internal static bool HasWrite(SetExpression body) => body switch
    {
        SetExpression.SelectExpression => false,
        SetExpression.ValuesExpression => false,
        SetExpression.TableExpression => false,
        SetExpression.QueryExpression nested => HasWrite(nested.Query),
        SetExpression.SetOperation op => HasWrite(op.Left) || HasWrite(op.Right),
        _ => true,
    };
}
