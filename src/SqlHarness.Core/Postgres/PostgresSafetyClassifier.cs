using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace SqlHarness.Core.Postgres;

internal sealed class PostgresSafetyClassifier
{
    private static readonly IReadOnlySet<string> EmptyTemps =
        new HashSet<string>(StringComparer.Ordinal);

    private static readonly HashSet<string> DeniedExactFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nextval",
        "setval",
        "pg_sleep",
        "pg_read_file",
        "pg_ls_dir",
        "lo_import",
    };

    internal SqlSafetyDecision Classify(
        string sql,
        SqlUsage usage,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        IReadOnlySet<string> sessionTempTables)
    {
        Sequence<Statement> statements;
        try
        {
            statements = new SqlQueryParser().Parse(sql.AsSpan(), new PostgreSqlDialect());
        }
        catch (ParserException)
        {
            return Denied(SqlSafetyReason.ParseError);
        }
        catch (Exception)
        {
            return Denied(SqlSafetyReason.ParseError);
        }

        if (statements.Count == 0)
            return Denied(SqlSafetyReason.UnsupportedStatement);

        var inspection = new ProhibitedConstructVisitor();
        foreach (var statement in statements)
            ((IElement)statement).Visit(inspection);

        if (inspection.HasProhibitedFunction)
            return Denied(SqlSafetyReason.UnsupportedStatement);

        var knownTemps = new HashSet<string>(sessionTempTables, StringComparer.Ordinal);
        return usage switch
        {
            SqlUsage.Query => ClassifyQuery(statements, database, allowMutation, confirmDatabase, knownTemps),
            SqlUsage.CompareSetup => ClassifyCompareSetup(statements, knownTemps),
            _ => Denied(SqlSafetyReason.UnsupportedStatement),
        };
    }

    private static SqlSafetyDecision ClassifyQuery(
        Sequence<Statement> statements,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        HashSet<string> knownTemps)
    {
        var hasMutation = false;
        var hasSessionLocal = false;

        foreach (var statement in statements)
        {
            var outcome = ClassifyStatement(statement, knownTemps);
            switch (outcome.Kind)
            {
                case StatementKind.ReadOnly:
                    break;
                case StatementKind.SessionLocal:
                    hasSessionLocal = true;
                    break;
                case StatementKind.Mutation:
                    hasMutation = true;
                    break;
                case StatementKind.SelectInto:
                    return Denied(SqlSafetyReason.SelectIntoNotAllowed);
                case StatementKind.Unsupported:
                    return Denied(SqlSafetyReason.UnsupportedStatement);
                case StatementKind.NonTemporaryWrite:
                    return Denied(SqlSafetyReason.NonTemporaryWrite);
                default:
                    return Denied(SqlSafetyReason.UnsupportedStatement);
            }
        }

        if (!hasMutation)
            return Allowed(hasSessionLocal: hasSessionLocal, sessionTemps: knownTemps);

        if (!allowMutation)
            return Denied(SqlSafetyReason.MutationNotAllowed);

        if (confirmDatabase is null)
            return Denied(SqlSafetyReason.DatabaseConfirmationRequired);

        if (!string.Equals(database, confirmDatabase, StringComparison.Ordinal))
            return Denied(SqlSafetyReason.DatabaseConfirmationMismatch);

        return Allowed(hasMutation: true, hasSessionLocal: hasSessionLocal, sessionTemps: knownTemps);
    }

    private static SqlSafetyDecision ClassifyCompareSetup(
        Sequence<Statement> statements,
        HashSet<string> knownTemps)
    {
        var hasSessionLocal = false;
        foreach (var statement in statements)
        {
            var outcome = ClassifyStatement(statement, knownTemps);
            switch (outcome.Kind)
            {
                case StatementKind.ReadOnly:
                    break;
                case StatementKind.SessionLocal:
                    hasSessionLocal = true;
                    break;
                case StatementKind.Mutation:
                case StatementKind.NonTemporaryWrite:
                case StatementKind.SelectInto:
                    return Denied(SqlSafetyReason.NonTemporaryWrite);
                default:
                    return Denied(SqlSafetyReason.UnsupportedStatement);
            }
        }

        return Allowed(hasSessionLocal: hasSessionLocal, sessionTemps: knownTemps);
    }

    private static StatementOutcome ClassifyStatement(
        Statement statement,
        HashSet<string> knownTemps)
    {
        switch (statement)
        {
            case Statement.Select select:
                return ClassifyQueryNode(select.Query, knownTemps);

            case Statement.Insert insert:
                return ClassifyWriteTarget(insert.InsertOperation.Name, knownTemps);

            case Statement.Update update:
            {
                if (GetRelationName(update.Table.Relation) is not { } name)
                    return StatementOutcome.Unsupported;
                return ClassifyWriteTarget(name, knownTemps);
            }

            case Statement.Delete delete:
            {
                if (GetDeleteTarget(delete.DeleteOperation) is not { } name)
                    return StatementOutcome.Unsupported;
                return ClassifyWriteTarget(name, knownTemps);
            }

            case Statement.Merge merge:
            {
                if (GetRelationName(merge.Table) is not { } name)
                    return StatementOutcome.Unsupported;
                return ClassifyWriteTarget(name, knownTemps);
            }

            case Statement.CreateTable create:
            {
                if (!create.Element.Temporary)
                    return StatementOutcome.Unsupported;

                var key = ObjectKey(create.Element.Name);
                if (key is null)
                    return StatementOutcome.Unsupported;

                knownTemps.Add(key);
                return StatementOutcome.SessionLocal;
            }

            case Statement.CreateIndex createIndex:
            {
                if (createIndex.Element.TableName is null)
                    return StatementOutcome.Unsupported;
                if (!IsSessionLocal(createIndex.Element.TableName, knownTemps))
                    return StatementOutcome.Unsupported;
                return StatementOutcome.SessionLocal;
            }

            case Statement.Drop drop:
            {
                if (drop.ObjectType == ObjectType.Table)
                {
                    if (drop.Names.Count == 0)
                        return StatementOutcome.Unsupported;
                    if (!drop.Names.All(name => IsSessionLocal(name, knownTemps)))
                        return StatementOutcome.Unsupported;
                    foreach (var name in drop.Names)
                    {
                        var key = ObjectKey(name);
                        if (key is not null)
                            knownTemps.Remove(key);
                    }

                    return StatementOutcome.SessionLocal;
                }

                if (drop.ObjectType == ObjectType.Index)
                {
                    // Index drops do not name the table; Temporary is the only session-local signal.
                    if (!drop.Temporary)
                        return StatementOutcome.Unsupported;
                    return StatementOutcome.SessionLocal;
                }

                return StatementOutcome.Unsupported;
            }

            case Statement.StartTransaction:
            case Statement.Commit:
            case Statement.Rollback:
            case Statement.Savepoint:
            case Statement.ReleaseSavepoint:
            case Statement.Prepare:
            case Statement.Execute:
            case Statement.Deallocate:
            case Statement.Copy:
            case Statement.Call:
            case Statement.SetVariable:
            case Statement.SetNames:
            case Statement.SetNamesDefault:
            case Statement.SetRole:
            case Statement.SetTimeZone:
            case Statement.SetTransaction:
            case Statement.Listen:
            case Statement.Notify:
            case Statement.Load:
            case Statement.Grant:
            case Statement.Revoke:
            case Statement.Analyze:
            case Statement.Truncate:
            case Statement.AlterTable:
            case Statement.AlterIndex:
            case Statement.AlterView:
            case Statement.CreateView:
            case Statement.CreateSchema:
            case Statement.CreateSequence:
            case Statement.CreateFunction:
            case Statement.CreateProcedure:
            case Statement.CreateExtension:
            case Statement.CreateRole:
            case Statement.CreateType:
            case Statement.CreateTrigger:
            case Statement.CreateDatabase:
                return StatementOutcome.Unsupported;

            default:
                return StatementOutcome.Unsupported;
        }
    }

    private static StatementOutcome ClassifyQueryNode(Query query, HashSet<string> knownTemps)
    {
        if (HasSelectInto(query))
            return StatementOutcome.SelectInto;

        var writeTargets = new List<ObjectName>();
        CollectWriteTargets(query, writeTargets);

        if (writeTargets.Count == 0)
            return StatementOutcome.ReadOnly;

        var allSessionLocal = true;
        foreach (var target in writeTargets)
        {
            if (!IsSessionLocal(target, knownTemps))
                allSessionLocal = false;
        }

        return allSessionLocal ? StatementOutcome.SessionLocal : StatementOutcome.Mutation;
    }

    private static StatementOutcome ClassifyWriteTarget(ObjectName target, HashSet<string> knownTemps) =>
        IsSessionLocal(target, knownTemps) ? StatementOutcome.SessionLocal : StatementOutcome.Mutation;

    private static void CollectWriteTargets(Query query, List<ObjectName> targets)
    {
        if (query.With is { } with)
        {
            foreach (var cte in with.CteTables)
                CollectWriteTargets(cte.Query, targets);
        }

        CollectWriteTargets(query.Body, targets);
    }

    private static void CollectWriteTargets(SetExpression body, List<ObjectName> targets)
    {
        switch (body)
        {
            case SetExpression.Insert insertBody:
                if (insertBody.Statement is Statement.Insert insert)
                    targets.Add(insert.InsertOperation.Name);
                else
                    CollectNestedStatementWrites(insertBody.Statement, targets);
                break;

            case SetExpression.SelectExpression:
                break;

            case SetExpression.QueryExpression queryExpression:
                CollectWriteTargets(queryExpression.Query, targets);
                break;

            case SetExpression.SetOperation setOperation:
                CollectWriteTargets(setOperation.Left, targets);
                CollectWriteTargets(setOperation.Right, targets);
                break;

            case SetExpression.ValuesExpression:
            case SetExpression.TableExpression:
                break;
        }
    }

    private static void CollectNestedStatementWrites(Statement statement, List<ObjectName> targets)
    {
        switch (statement)
        {
            case Statement.Insert insert:
                targets.Add(insert.InsertOperation.Name);
                break;
            case Statement.Update update when GetRelationName(update.Table.Relation) is { } name:
                targets.Add(name);
                break;
            case Statement.Delete delete when GetDeleteTarget(delete.DeleteOperation) is { } name:
                targets.Add(name);
                break;
            case Statement.Merge merge when GetRelationName(merge.Table) is { } name:
                targets.Add(name);
                break;
            case Statement.Select select:
                CollectWriteTargets(select.Query, targets);
                break;
        }
    }

    private static bool HasSelectInto(Query query)
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

    private static bool HasSelectInto(SetExpression body) => body switch
    {
        SetExpression.SelectExpression selectExpression => selectExpression.Select.Into is not null,
        SetExpression.QueryExpression queryExpression => HasSelectInto(queryExpression.Query),
        SetExpression.SetOperation setOperation =>
            HasSelectInto(setOperation.Left) || HasSelectInto(setOperation.Right),
        SetExpression.Insert insertBody when insertBody.Statement is Statement.Select select =>
            HasSelectInto(select.Query),
        _ => false,
    };

    private static bool IsSessionLocal(ObjectName name, IReadOnlySet<string> knownTemps)
    {
        if (name.Values.Count == 0)
            return false;

        var first = FoldIdent(name.Values[0]);
        if (first == "pg_temp" || first.StartsWith("pg_temp_", StringComparison.Ordinal))
            return true;

        if (name.Values.Count != 1)
            return false;

        var key = ObjectKey(name);
        return key is not null && knownTemps.Contains(key);
    }

    private static string? ObjectKey(ObjectName name)
    {
        if (name.Values.Count == 0)
            return null;

        // Record/lookup by the relation name (final identifier).
        return FoldIdent(name.Values[^1]);
    }

    private static string FoldIdent(Ident ident) =>
        ident.QuoteStyle is null ? ident.Value.ToLowerInvariant() : ident.Value;

    private static ObjectName? GetRelationName(TableFactor? relation) => relation switch
    {
        TableFactor.Table table => table.Name,
        _ => null,
    };

    private static ObjectName? GetDeleteTarget(DeleteOperation delete) => delete.From switch
    {
        FromTable.WithFromKeyword withFrom when withFrom.From.Count > 0 =>
            GetRelationName(withFrom.From[0].Relation),
        FromTable.WithoutKeyword without when without.From.Count > 0 =>
            GetRelationName(without.From[0].Relation),
        _ => delete.Tables is { Count: > 0 } ? delete.Tables[0] : null,
    };

    private static SqlSafetyDecision Allowed(
        bool hasMutation = false,
        bool hasSessionLocal = false,
        IReadOnlySet<string>? sessionTemps = null) =>
        new(true, SqlSafetyReason.Allowed, hasMutation)
        {
            HasSessionLocalWork = hasSessionLocal,
            SessionTempTables = sessionTemps is null || sessionTemps.Count == 0
                ? EmptyTemps
                : new HashSet<string>(sessionTemps, StringComparer.Ordinal),
        };

    private static SqlSafetyDecision Denied(SqlSafetyReason reason) =>
        new(false, reason);

    private enum StatementKind
    {
        ReadOnly,
        SessionLocal,
        Mutation,
        SelectInto,
        Unsupported,
        NonTemporaryWrite,
    }

    private readonly record struct StatementOutcome(StatementKind Kind)
    {
        public static StatementOutcome ReadOnly { get; } = new(StatementKind.ReadOnly);
        public static StatementOutcome SessionLocal { get; } = new(StatementKind.SessionLocal);
        public static StatementOutcome Mutation { get; } = new(StatementKind.Mutation);
        public static StatementOutcome SelectInto { get; } = new(StatementKind.SelectInto);
        public static StatementOutcome Unsupported { get; } = new(StatementKind.Unsupported);
        public static StatementOutcome NonTemporaryWrite { get; } = new(StatementKind.NonTemporaryWrite);
    }

    private sealed class ProhibitedConstructVisitor : Visitor
    {
        internal bool HasProhibitedFunction { get; private set; }

        public override ControlFlow PreVisitExpression(Expression expression)
        {
            if (expression is Expression.Function function)
            {
                var name = FunctionName(function.Name);
                if (IsDeniedFunction(name))
                {
                    HasProhibitedFunction = true;
                    return ControlFlow.Break;
                }
            }

            return ControlFlow.Continue;
        }

        private static string FunctionName(ObjectName name)
        {
            if (name.Values.Count == 0)
                return string.Empty;
            return name.Values[^1].Value;
        }

        private static bool IsDeniedFunction(string name)
        {
            if (DeniedExactFunctions.Contains(name))
                return true;

            return name.StartsWith("dblink", StringComparison.OrdinalIgnoreCase);
        }
    }
}
