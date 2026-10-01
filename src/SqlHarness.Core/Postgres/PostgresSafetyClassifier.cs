using SqlParser;
using SqlParser.Ast;

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
        if (!PostgresDocument.TryParse(sql, out var statements) || statements is null)
            return Denied(SqlSafetyReason.ParseError);

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

        var knownTemps = new SessionTemps(sessionTempTables);
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
        SessionTemps knownTemps)
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
                case StatementKind.SessionLocalMutation:
                    hasMutation = true;
                    hasSessionLocal = true;
                    break;
                case StatementKind.SelectInto:
                    return Denied(SqlSafetyReason.SelectIntoNotAllowed);
                case StatementKind.Unsupported:
                    return Denied(SqlSafetyReason.UnsupportedStatement);
                case StatementKind.NonTemporaryWrite:
                    return DeniedWrite(SqlSafetyReason.NonTemporaryWrite, knownTemps);
                default:
                    return Denied(SqlSafetyReason.UnsupportedStatement);
            }
        }

        if (!hasMutation)
            return Allowed(hasSessionLocal: hasSessionLocal, sessionTemps: knownTemps);

        if (!allowMutation)
            return DeniedWrite(SqlSafetyReason.MutationNotAllowed, knownTemps);

        if (confirmDatabase is null)
            return Denied(SqlSafetyReason.DatabaseConfirmationRequired);

        if (!string.Equals(database, confirmDatabase, StringComparison.Ordinal))
            return Denied(SqlSafetyReason.DatabaseConfirmationMismatch);

        return Allowed(hasMutation: true, hasSessionLocal: hasSessionLocal, sessionTemps: knownTemps);
    }

    private static SqlSafetyDecision ClassifyCompareSetup(
        List<(Statement Statement, StatementEffect Effect)> statements,
        SessionTemps knownTemps)
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
                case StatementKind.SessionLocalMutation:
                case StatementKind.NonTemporaryWrite:
                case StatementKind.SelectInto:
                    return DeniedWrite(SqlSafetyReason.NonTemporaryWrite, knownTemps);
                default:
                    return Denied(SqlSafetyReason.UnsupportedStatement);
            }
        }

        return Allowed(hasSessionLocal: hasSessionLocal, sessionTemps: knownTemps);
    }

    private static StatementOutcome ClassifyStatement(
        Statement statement,
        StatementEffect effect,
        SessionTemps knownTemps)
    {
        if (effect.HasSelectInto && statement is not Statement.Select)
            return StatementOutcome.SelectInto;
        if (effect.UnresolvedWrite)
            return StatementOutcome.Unsupported;

        switch (statement)
        {
            case Statement.Select select:
                return ClassifySelect(select, effect, knownTemps);

            case Statement.Explain explain:
                return ClassifyExplain(explain, knownTemps);

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

                    // An ON COMMIT DROP temp does not outlive its transaction, and the
                    // classifier does not know where that transaction ends, so its
                    // name never proves a later target of any statement kind.
                    // A qualified declaration proves its relation name only through
                    // the pg_temp alias: the server rejects any other schema for a
                    // temp, except a pg_temp_<N> that cannot be told offline from
                    // another session's.
                    var declared = create.Element.Name.Values;
                    knownTemps.Record(
                        key,
                        declared[^1],
                        survivesCommit: create.Element.OnCommit != OnCommit.Drop,
                        ifNotExists: create.Element.IfNotExists,
                        provesName: declared.Count == 1 ||
                            (declared.Count == 2 && IsPgTempAlias(declared[0])));
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
                        // Revocation must cover every temp the server may have
                        // dropped. With a known stored name that is one key. With
                        // an unknown one (reachable through pg_temp.<name>) the
                        // match cannot be narrowed offline, so every proof goes.
                        foreach (var name in drop.Names)
                        {
                            var relation = name.Values[^1];
                            if (FoldsLikeServer(relation))
                                knownTemps.Forget(FoldIdent(relation));
                            else
                                knownTemps.ForgetAll();
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

            case Statement.Truncate truncate:
                return ClassifyTruncate(truncate, knownTemps);

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

    private static StatementOutcome ClassifyTruncate(Statement.Truncate truncate, SessionTemps knownTemps)
    {
        // Closed option allowlist: anything whose effect reaches past the named
        // rows stays unsupported, even over proven temps. CASCADE follows foreign
        // keys to tables that are not named; RESTART IDENTITY resets sequences
        // whose session locality temp tracking does not prove; ON CLUSTER and
        // PARTITION are not PostgreSQL shapes.
        if (truncate.Cascade is not (null or TruncateCascadeOption.Restrict))
            return StatementOutcome.Unsupported;
        if (truncate.Identity is not (null or TruncateIdentityOption.Continue))
            return StatementOutcome.Unsupported;
        if (truncate.OnCluster is not null || truncate.Partitions is not null)
            return StatementOutcome.Unsupported;
        if (truncate.Names is not { Count: > 0 })
            return StatementOutcome.Unsupported;

        // Every target must be proven; one unproven name denies the statement.
        // Never approvable: persistent TRUNCATE does not join the mutation path.
        foreach (var target in truncate.Names)
        {
            if (!IsProvenSessionTemp(target.Name, knownTemps))
                return StatementOutcome.NonTemporaryWrite;
        }

        return StatementOutcome.SessionLocal;
    }

    // Stricter than IsSessionLocal, and used by TRUNCATE only: the sole proof is
    // a relation name that this session flow recorded from CREATE TEMP /
    // SELECT INTO TEMP. A name that only a caller-built set supplied proves
    // nothing. Both the target and the recorded name must be spelled so that
    // the server's fold is known offline; see FoldsLikeServer.
    // The target is that name unqualified, or qualified with exactly the
    // pg_temp alias (011/final I2). The alias is no proof by itself here: it
    // only pins the proven name to the session's temp schema, so the qualified
    // spelling does not depend on search_path. No other qualifier and no
    // three-part name is accepted.
    private static bool IsProvenSessionTemp(ObjectName name, SessionTemps knownTemps)
    {
        Ident relation;
        if (name.Values.Count == 1)
            relation = name.Values[0];
        else if (name.Values.Count == 2 && IsPgTempAlias(name.Values[0]))
            relation = name.Values[1];
        else
            return false;

        return FoldsLikeServer(relation) &&
            knownTemps.TruncateProven.Contains(FoldIdent(relation));
    }

    // The server's alias for the current session's temp schema: unquoted in
    // any ASCII case, or quoted exactly "pg_temp". Never pg_temp_<N>.
    private static bool IsPgTempAlias(Ident schema) =>
        FoldIdent(schema) == "pg_temp";

    // The server keeps a quoted identifier as written and lower-cases ASCII A-Z
    // in an unquoted one. What it does to a non-ASCII character of an unquoted
    // identifier depends on the server encoding (left alone under UTF-8, may be
    // lower-cased under a single-byte encoding), which is unknown offline. So
    // the stored name is known only for a quoted identifier and for an all-ASCII
    // unquoted one; any other identifier neither proves nor is proven.
    // The server also truncates an identifier to 63 bytes, so two longer
    // spellings can address one relation: a truncated identifier has no known
    // stored name either.
    private static bool FoldsLikeServer(Ident ident) =>
        !IsTruncatedByServer(ident) &&
        (ident.QuoteStyle is not null || ident.Value.All(char.IsAscii));

    // NAMEDATALEN - 1 in a stock server build, counted in UTF-8 bytes.
    private const int MaxIdentifierBytes = 63;

    private static bool IsTruncatedByServer(Ident ident) =>
        System.Text.Encoding.UTF8.GetByteCount(ident.Value) > MaxIdentifierBytes;

    private static StatementOutcome ClassifySelect(
        Statement.Select select,
        StatementEffect effect,
        SessionTemps knownTemps)
    {
        var into = PostgresQueryShape.TopLevelSelectInto(select.Query);
        if (into is null)
            return effect.HasSelectInto ? StatementOutcome.SelectInto : FromWrites(effect.Targets, knownTemps);
        // Unambiguous session locality only: TEMP with a single-part name and no
        // other INTO in the batch. Writes inside (for example a data-modifying
        // CTE) still count through the normal target analysis.
        if (!into.Temporary || into.Name.Values.Count != 1 || effect.SelectIntoCount != 1)
            return StatementOutcome.SelectInto;
        var key = ObjectKey(into.Name);
        if (key is null)
            return StatementOutcome.Unsupported;
        knownTemps.Record(key, into.Name.Values[^1], survivesCommit: true, ifNotExists: false, provesName: true);
        if (FromWrites(effect.Targets, knownTemps).Kind == StatementKind.Mutation)
            return StatementOutcome.SessionLocalMutation;
        return StatementOutcome.SessionLocal;
    }

    private static StatementOutcome ClassifyExplain(Statement.Explain explain, SessionTemps knownTemps)
    {
        var innerEffect = StatementEffectVisitor.Inspect(explain.Statement);
        if (innerEffect.HasProhibitedFunction)
            return StatementOutcome.Unsupported;
        if (!IsExplainAnalyze(explain))
        {
            // A plan-only EXPLAIN never executes: allow it only over a safe SELECT
            // with no writes and no result-into of its own. A throwaway temp set
            // keeps the pure plan from registering session objects it never creates.
            if (explain.Statement is not Statement.Select innerSelect ||
                PostgresQueryShape.HasSelectInto(innerSelect.Query) ||
                PostgresQueryShape.HasWrite(innerSelect.Query))
                return StatementOutcome.Unsupported;
            var inner = ClassifyStatement(
                explain.Statement, innerEffect, new SessionTemps(knownTemps));
            return inner.Kind is StatementKind.ReadOnly or StatementKind.SessionLocal
                ? StatementOutcome.ReadOnly
                : StatementOutcome.Unsupported;
        }
        // EXPLAIN ANALYZE executes the inner statement: full effect analysis, never auto-read.
        return ClassifyStatement(explain.Statement, innerEffect, knownTemps);
    }

    private static bool IsExplainAnalyze(Statement.Explain explain)
    {
        if (explain.Analyze)
            return true;
        foreach (var option in explain.Options ?? Enumerable.Empty<UtilityOption>())
        {
            if (!option.Name.Value.Equals("ANALYZE", StringComparison.OrdinalIgnoreCase))
                continue;
            // A bare ANALYZE option means TRUE in PostgreSQL; only an explicit
            // FALSE keeps the plan-only path.
            if (option.Arg is Expression.LiteralValue { Value: Value.Boolean { Value: false } })
                continue;
            return true;
        }
        return false;
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

    private static bool IsSessionLocal(ObjectName name, IReadOnlySet<string> knownTemps)
    {
        if (name.Values.Count == 0)
            return false;

        // schema.relation: only the pg_temp alias, which the server resolves to
        // the current session's temp schema. A pg_temp_<N> schema can belong to
        // another session, so that prefix proves nothing.
        if (name.Values.Count == 2)
            return IsPgTempAlias(name.Values[0]);

        // An unqualified name gets no credit for how it is spelled (a persistent
        // table may be called pg_temp_stuff, or pg_temp): it must be proven.
        if (name.Values.Count != 1 || !FoldsLikeServer(name.Values[0]))
            return false;

        return knownTemps.Contains(FoldIdent(name.Values[0]));
    }

    private static string? ObjectKey(ObjectName name)
    {
        if (name.Values.Count == 0)
            return null;

        // Record/lookup by the relation name (final identifier).
        return FoldIdent(name.Values[^1]);
    }

    // ASCII-only fold, the part of the server's fold that holds in every
    // encoding. No Unicode case mapping takes part in a name comparison.
    private static string FoldIdent(Ident ident) =>
        ident.QuoteStyle is null ? FoldAscii(ident.Value) : ident.Value;

    private static string FoldAscii(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            }
        });

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
            // An empty set is still carried when it remembers an ON COMMIT DROP name.
            SessionTempTables = sessionTemps is null ||
                (sessionTemps.Count == 0 && sessionTemps is not SessionTemps { RemembersCommitDrop: true })
                ? EmptyTemps
                : new SessionTemps(sessionTemps),
        };

    private static SqlSafetyDecision Denied(SqlSafetyReason reason) =>
        new(false, reason);

    // Fixed text, never SQL: shown with a write denial when the flow declared
    // a TEMP table that recorded no proof. Reason and exit code are unchanged.
    private const string UnprovenTempHint =
        "A TEMP table declared in this session flow is not a proven session temp " +
        "(unquoted non-ASCII name, name over 63 UTF-8 bytes, ON COMMIT DROP, or a schema other than pg_temp). " +
        "If it is the write target, quote or shorten its name and keep it past commit; mutation approval is not the remedy.";

    private static SqlSafetyDecision DeniedWrite(SqlSafetyReason reason, SessionTemps knownTemps) =>
        new(false, reason, Detail: knownTemps.HasUnprovenDeclaration ? UnprovenTempHint : null);

    // The session temp set plus the subset that is strong enough to prove a
    // TRUNCATE target. The base set holds every name that proves a DML / DROP /
    // CREATE INDEX target: names this classifier recorded, plus names a caller
    // supplied in a plain set. The subset holds only the recorded ones and
    // travels only inside a set this classifier produced, so a set built
    // anywhere else proves no TRUNCATE.
    private sealed class SessionTemps : HashSet<string>
    {
        internal SessionTemps(IEnumerable<string> names)
            : base(names, StringComparer.Ordinal)
        {
            if (names is SessionTemps prior)
            {
                TruncateProven.UnionWith(prior.TruncateProven);
                _commitDropped.UnionWith(prior._commitDropped);
                _commitDroppedUnknownName = prior._commitDroppedUnknownName;
                _commitDroppedAnyName = prior._commitDroppedAnyName;
                _unprovenDeclaration = prior._unprovenDeclaration;
            }
        }

        // Set when a TEMP declaration recorded no proof. It only words a
        // denial (UnprovenTempHint); no verdict reads it.
        private bool _unprovenDeclaration;

        internal bool HasUnprovenDeclaration => _unprovenDeclaration || RemembersCommitDrop;

        // Names declared ON COMMIT DROP in this session flow. Such a temp may
        // still exist, so a later CREATE TEMP TABLE IF NOT EXISTS of the same
        // name can be a no-op on the server and proves nothing.
        private readonly HashSet<string> _commitDropped = new(StringComparer.Ordinal);

        // Set when an ON COMMIT DROP declaration had an unknown stored name
        // (unquoted non-ASCII). The stored name may even be all-ASCII: a
        // single-byte Turkish locale folds U+0130 to i. So it may be any name.
        private bool _commitDroppedUnknownName;

        // Set when an ON COMMIT DROP declaration was longer than the server's
        // identifier limit: after truncation it may be any name at all.
        private bool _commitDroppedAnyName;

        internal HashSet<string> TruncateProven { get; } = new(StringComparer.Ordinal);

        internal bool RemembersCommitDrop =>
            _commitDropped.Count > 0 || _commitDroppedUnknownName || _commitDroppedAnyName;

        // provesName false: the declaration can still withhold or revoke proof
        // (the branches below), but it never adds any.
        internal void Record(string key, Ident declaredAs, bool survivesCommit, bool ifNotExists, bool provesName)
        {
            if (!FoldsLikeServer(declaredAs))
            {
                // The stored name is unknown, so nothing is recorded. Both
                // spellings this declaration was ever keyed under lose their proof.
                _unprovenDeclaration = true;
                Forget(key);
                Forget(declaredAs.Value.ToLowerInvariant());
                if (survivesCommit)
                    return;

                if (IsTruncatedByServer(declaredAs))
                {
                    // The truncated name may be a tracked temp that an
                    // IF NOT EXISTS ... ON COMMIT DROP left in place, or may
                    // become one later: revoke everything, prove nothing after.
                    ForgetAll();
                    _commitDroppedAnyName = true;
                }
                else
                {
                    _commitDroppedUnknownName = true;
                }

                return;
            }

            if (!survivesCommit)
            {
                Forget(key);
                _commitDropped.Add(key);
                return;
            }

            if ((ifNotExists && MayBeCommitDropped(key)) || !provesName)
            {
                _unprovenDeclaration = true;
                return;
            }

            // A plain CREATE TEMP TABLE fails on the server when the name is
            // still taken, so reaching the next statement means this one exists.
            _commitDropped.Remove(key);
            Add(key);
            TruncateProven.Add(key);
        }

        private bool MayBeCommitDropped(string key) =>
            _commitDroppedAnyName ||
            _commitDroppedUnknownName ||
            _commitDropped.Contains(key);

        internal void Forget(string key)
        {
            Remove(key);
            TruncateProven.Remove(key);
        }

        // Drops every proof, including names a caller supplied. The ON COMMIT
        // DROP record stays: it only ever withholds proof.
        internal void ForgetAll()
        {
            Clear();
            TruncateProven.Clear();
        }
    }

    private enum StatementKind
    {
        ReadOnly,
        SessionLocal,
        Mutation,
        SessionLocalMutation,
        SelectInto,
        Unsupported,
        NonTemporaryWrite,
    }

    private readonly record struct StatementOutcome(StatementKind Kind)
    {
        public static StatementOutcome ReadOnly { get; } = new(StatementKind.ReadOnly);
        public static StatementOutcome SessionLocal { get; } = new(StatementKind.SessionLocal);
        public static StatementOutcome Mutation { get; } = new(StatementKind.Mutation);
        public static StatementOutcome SessionLocalMutation { get; } = new(StatementKind.SessionLocalMutation);
        public static StatementOutcome SelectInto { get; } = new(StatementKind.SelectInto);
        public static StatementOutcome Unsupported { get; } = new(StatementKind.Unsupported);
        public static StatementOutcome NonTemporaryWrite { get; } = new(StatementKind.NonTemporaryWrite);
    }

    private readonly record struct StatementEffect(
        bool HasProhibitedFunction,
        bool HasSelectInto,
        int SelectIntoCount,
        bool UnresolvedWrite,
        IReadOnlyList<ObjectName> Targets);

    private sealed class StatementEffectVisitor : Visitor
    {
        private readonly List<ObjectName> _targets = [];
        private int _selectIntoCount;

        internal bool HasProhibitedFunction { get; private set; }
        internal bool UnresolvedWrite { get; private set; }

        internal static StatementEffect Inspect(Statement statement)
        {
            var visitor = new StatementEffectVisitor();
            ((IElement)statement).Visit(visitor);
            visitor.VisitSkippedChildren(statement);
            return new StatementEffect(
                visitor.HasProhibitedFunction,
                visitor._selectIntoCount > 0,
                visitor._selectIntoCount,
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
                    if (PostgresQueryShape.HasSelectInto(select.Query))
                        _selectIntoCount++;
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
            if (PostgresQueryShape.HasSelectInto(query))
                _selectIntoCount++;
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