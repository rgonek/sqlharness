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
        "set_config",
        "pg_cancel_backend",
        "pg_terminate_backend",
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

        var parsed = new List<(Statement Statement, StatementEffect Effect)>(statements.Count);
        foreach (var statement in statements)
        {
            var effect = StatementEffectVisitor.Inspect(statement);
            if (effect.HasProhibitedFunction)
                return Denied(SqlSafetyReason.UnsupportedStatement);
            parsed.Add((statement, effect));
        }

        var knownTemps = new HashSet<string>(sessionTempTables, StringComparer.Ordinal);
        return usage switch
        {
            SqlUsage.Query => ClassifyQuery(parsed, database, allowMutation, confirmDatabase, knownTemps),
            SqlUsage.CompareSetup => ClassifyCompareSetup(parsed, knownTemps),
            _ => Denied(SqlSafetyReason.UnsupportedStatement),
        };
    }

    private static SqlSafetyDecision ClassifyQuery(
        List<(Statement Statement, StatementEffect Effect)> statements,
        string? database,
        bool allowMutation,
        string? confirmDatabase,
        HashSet<string> knownTemps)
    {
        var hasMutation = false;
        var hasSessionLocal = false;

        foreach (var (statement, effect) in statements)
        {
            var outcome = ClassifyStatement(statement, effect, knownTemps);
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
        List<(Statement Statement, StatementEffect Effect)> statements,
        HashSet<string> knownTemps)
    {
        var hasSessionLocal = false;
        foreach (var (statement, effect) in statements)
        {
            var outcome = ClassifyStatement(statement, effect, knownTemps);
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
        StatementEffect effect,
        HashSet<string> knownTemps)
    {
        if (effect.HasSelectInto)
            return StatementOutcome.SelectInto;
        if (effect.UnresolvedWrite)
            return StatementOutcome.Unsupported;

        switch (statement)
        {
            case Statement.Select:
                return FromWrites(effect.Targets, knownTemps);

            case Statement.Insert:
            case Statement.Update:
            case Statement.Delete:
            case Statement.Merge:
                if (effect.Targets.Count == 0)
                    return StatementOutcome.Unsupported;
                return FromWrites(effect.Targets, knownTemps);

            case Statement.CreateTable create:
                {
                    if (!create.Element.Temporary)
                        return StatementOutcome.Unsupported;

                    var key = ObjectKey(create.Element.Name);
                    if (key is null)
                        return StatementOutcome.Unsupported;

                    knownTemps.Add(key);
                    return FromWrites(effect.Targets, knownTemps, emptyIsSessionLocal: true);
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

    private static StatementOutcome FromWrites(
        IReadOnlyList<ObjectName> targets,
        IReadOnlySet<string> knownTemps,
        bool emptyIsSessionLocal = false)
    {
        if (targets.Count == 0)
            return emptyIsSessionLocal ? StatementOutcome.SessionLocal : StatementOutcome.ReadOnly;

        foreach (var target in targets)
        {
            if (!IsSessionLocal(target, knownTemps))
                return StatementOutcome.Mutation;
        }

        return StatementOutcome.SessionLocal;
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

    private readonly record struct StatementEffect(
        bool HasProhibitedFunction,
        bool HasSelectInto,
        bool UnresolvedWrite,
        IReadOnlyList<ObjectName> Targets);

    private sealed class StatementEffectVisitor : Visitor
    {
        private readonly List<ObjectName> _targets = [];
        private bool _selectInto;

        internal bool HasProhibitedFunction { get; private set; }
        internal bool UnresolvedWrite { get; private set; }

        internal static StatementEffect Inspect(Statement statement)
        {
            var visitor = new StatementEffectVisitor();
            ((IElement)statement).Visit(visitor);
            visitor.VisitSkippedChildren(statement);
            return new StatementEffect(
                visitor.HasProhibitedFunction,
                visitor._selectInto,
                visitor.UnresolvedWrite,
                visitor._targets);
        }

        // Ast.CreateTable is not an IElement, so Visit never enters column defaults,
        // GENERATED, CHECK, WITH/OPTIONS/TBLPROPERTIES, ORDER BY, or CREATE INDEX
        // key, WHERE, and WITH expressions.
        private void VisitSkippedChildren(Statement statement)
        {
            switch (statement)
            {
                case Statement.CreateTable create:
                    VisitCreateTable(create.Element);
                    break;
                case Statement.CreateIndex index:
                    VisitCreateIndex(index.Element);
                    break;
                case Statement.Delete delete:
                    if (delete.DeleteOperation.Selection is { } selection)
                        ((IElement)selection).Visit(this);
                    if (delete.DeleteOperation.Using is { } usingFactor)
                        ((IElement)usingFactor).Visit(this);
                    break;
            }
        }

        private void VisitCreateTable(CreateTable table)
        {
            if (table.Query is { } query)
                VisitQuery(query);

            if (table.Columns is not null)
            {
                foreach (var column in table.Columns)
                    ((IElement)column).Visit(this);
            }

            if (table.Constraints is not null)
            {
                foreach (var constraint in table.Constraints)
                    ((IElement)constraint).Visit(this);
            }

            if (table.PartitionBy is { } partitionBy)
                ((IElement)partitionBy).Visit(this);
            if (table.PrimaryKey is { } primaryKey)
                ((IElement)primaryKey).Visit(this);
            VisitElement(table.WithOptions);
            VisitElement(table.Options);
            VisitElement(table.TableProperties);
            VisitOrderBy(table.OrderBy);
        }

        private void VisitCreateIndex(CreateIndex index)
        {
            if (index.Columns is not null)
            {
                foreach (var column in index.Columns)
                    ((IElement)column).Visit(this);
            }

            if (index.Include is not null)
            {
                foreach (var include in index.Include)
                {
                    if (include is IElement element)
                        element.Visit(this);
                }
            }

            if (index.Predicate is { } predicate)
                ((IElement)predicate).Visit(this);
            VisitElement(index.With);
        }

        private void VisitElement(IElement? element) => element?.Visit(this);

        private void VisitOrderBy(OneOrManyWithParens<Expression>? orderBy)
        {
            switch (orderBy)
            {
                case OneOrManyWithParens<Expression>.One one:
                    ((IElement)one.Value).Visit(this);
                    break;
                case OneOrManyWithParens<Expression>.Many many:
                    VisitElement(many.Values);
                    break;
            }
        }

        public override ControlFlow PreVisitStatement(Statement statement)
        {
            switch (statement)
            {
                case Statement.Select select:
                    if (HasSelectInto(select.Query))
                        _selectInto = true;
                    break;
                case Statement.Insert insert:
                    _targets.Add(insert.InsertOperation.Name);
                    break;
                case Statement.Update update:
                    AddRelation(GetRelationName(update.Table.Relation));
                    break;
                case Statement.Delete delete:
                    AddRelation(GetDeleteTarget(delete.DeleteOperation));
                    break;
                case Statement.Merge merge:
                    AddRelation(GetRelationName(merge.Table));
                    break;
            }

            return ControlFlow.Continue;
        }

        public override ControlFlow PreVisitExpression(Expression expression)
        {
            if (expression is not Expression.Function function)
                return ControlFlow.Continue;

            if (IsDeniedObjectName(function.Name))
            {
                HasProhibitedFunction = true;
                return ControlFlow.Break;
            }

            // Args live on FunctionArgumentList, which is not IElement, so the library walk never enters them.
            VisitFunctionArguments(function.Args);
            VisitFunctionArguments(function.Parameters);
            return HasProhibitedFunction ? ControlFlow.Break : ControlFlow.Continue;
        }

        public override ControlFlow PreVisitTableFactor(TableFactor tableFactor)
        {
            // Library Visit reports a derived factor and does not enter its subquery.
            if (tableFactor is TableFactor.Derived derived)
                VisitQuery(derived.SubQuery);

            switch (tableFactor)
            {
                case TableFactor.Table table when table.Args is not null && IsDeniedObjectName(table.Name):
                    HasProhibitedFunction = true;
                    return ControlFlow.Break;
                case TableFactor.Function function:
                    if (IsDeniedObjectName(function.Name))
                    {
                        HasProhibitedFunction = true;
                        return ControlFlow.Break;
                    }

                    VisitFunctionArgs(function.Args);
                    return HasProhibitedFunction ? ControlFlow.Break : ControlFlow.Continue;
                case TableFactor.TableFunction tableFunction:
                    ((IElement)tableFunction.Expression).Visit(this);
                    return HasProhibitedFunction ? ControlFlow.Break : ControlFlow.Continue;
            }

            return ControlFlow.Continue;
        }

        private void VisitFunctionArguments(FunctionArguments? arguments)
        {
            if (HasProhibitedFunction || arguments is not FunctionArguments.List { ArgumentList: { } list })
                return;

            VisitFunctionArgs(list.Args);
            if (list.Clauses is null)
                return;

            foreach (var clause in list.Clauses)
            {
                VisitArgumentClause(clause);
                if (HasProhibitedFunction)
                    return;
            }
        }

        private void VisitFunctionArgs(IEnumerable<FunctionArg>? args)
        {
            if (args is null || HasProhibitedFunction)
                return;

            foreach (var arg in args)
            {
                ((IElement)arg).Visit(this);
                if (HasProhibitedFunction)
                    return;
            }
        }

        private void VisitArgumentClause(FunctionArgumentClause clause)
        {
            switch (clause)
            {
                case FunctionArgumentClause.OrderBy orderBy:
                    foreach (var item in orderBy.OrderByExpressions)
                        ((IElement)item).Visit(this);
                    break;
                case FunctionArgumentClause.Limit limit:
                    ((IElement)limit.LimitExpression).Visit(this);
                    break;
                case FunctionArgumentClause.Having having:
                    ((IElement)having.Bound.Expression).Visit(this);
                    break;
            }
        }

        private void VisitQuery(Query query)
        {
            if (HasSelectInto(query))
                _selectInto = true;
            ((IElement)query).Visit(this);
        }

        private void AddRelation(ObjectName? name)
        {
            if (name is null)
                UnresolvedWrite = true;
            else
                _targets.Add(name);
        }

        private static bool IsDeniedObjectName(ObjectName name)
        {
            if (name.Values.Count == 0)
                return false;

            var functionName = name.Values[^1].Value;
            if (DeniedExactFunctions.Contains(functionName))
                return true;

            return functionName.StartsWith("dblink", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_read_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_ls_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("lo_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_advisory_", StringComparison.OrdinalIgnoreCase)
                || functionName.StartsWith("pg_try_advisory_", StringComparison.OrdinalIgnoreCase);
        }
    }
}