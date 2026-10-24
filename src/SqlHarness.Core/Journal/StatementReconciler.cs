namespace SqlHarness.Core;

public enum StatementStatus
{
    Confirmed,
    Partial,
    Unavailable,
}

/// <summary>Completeness of collected per-statement runtime counters; it does not describe hidden function work.</summary>
public enum StatementCounterAttribution
{
    Complete,
    Partial,
    Unavailable,
}

internal static class StatementAttributionClassifier
{
    internal static StatementCounterAttribution Classify(StatementStatus? status) => status switch
    {
        StatementStatus.Confirmed => StatementCounterAttribution.Complete,
        StatementStatus.Partial => StatementCounterAttribution.Partial,
        _ => StatementCounterAttribution.Unavailable,
    };
}

/// <summary>Compares plan-derived statement counters with the engine's batch IO counters.</summary>
internal static class StatementReconciler
{
    internal static IReadOnlyList<StatementStatus> Reconcile(
        IReadOnlyList<StatementMetrics> statements,
        IReadOnlyList<TableIoCounters> observed)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(observed);
        if (statements.Count == 0)
            return [];
        if (observed.Count == 0)
            return Enumerable.Repeat(StatementStatus.Unavailable, statements.Count).ToArray();

        var keyComparer = ReconciliationKeyComparer.Instance;
        var planObjects = new Dictionary<ReconciliationKey, CounterTotals>(keyComparer);
        foreach (var statement in statements)
        {
            foreach (var counters in statement.Objects ?? [])
            {
                var key = Normalize(counters.Object, counters.Kind);
                if (!planObjects.TryGetValue(key, out var total))
                    planObjects[key] = total = new CounterTotals();
                total.Add(counters.LogicalReads, counters.LobLogicalReads, counters.Scans);
            }
        }

        var observedObjects = new Dictionary<ReconciliationKey, CounterTotals>(keyComparer);
        foreach (var counters in observed)
        {
            var identity = counters.Identity ?? StatisticsIoTableIdentity.Unknown(counters.Table);
            var key = identity.Kind == StatisticsIoTableKind.AnonymousTableVariable
                ? ReconciliationKey.AnonymousVariables
                : ReconciliationKey.Object(NormalizeObservedName(counters.Table));
            if (!observedObjects.TryGetValue(key, out var total))
                observedObjects[key] = total = new CounterTotals();
            total.Add(counters.LogicalReads, counters.LobLogicalReads, counters.ScanCount);
        }

        var mismatched = new HashSet<ReconciliationKey>(keyComparer);
        foreach (var pair in planObjects)
        {
            if (!observedObjects.TryGetValue(pair.Key, out var actual) || !pair.Value.Equals(actual))
                mismatched.Add(pair.Key);
        }

        // Runtime IO that has no usable plan object identity cannot be attributed to a subset.
        var unknownIo = observedObjects.Any(pair => !planObjects.ContainsKey(pair.Key)
            && (pair.Value.LogicalReads != 0 || pair.Value.LobLogicalReads != 0 || pair.Value.Scans != 0));

        return statements.Select(statement =>
        {
            if (statement.Objects is null || statement.CountersComplete != true || statement.IdentityComplete != true)
                return StatementStatus.Partial;
            if (unknownIo || statement.Objects.Any(counters => mismatched.Contains(Normalize(counters.Object, counters.Kind))))
                return StatementStatus.Partial;
            return StatementStatus.Confirmed;
        }).ToArray();
    }

    private static string NormalizeObservedName(string name)
    {
        var unwrapped = name.Trim().Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);
        var dot = unwrapped.LastIndexOf('.');
        return dot >= 0 ? unwrapped[(dot + 1)..] : unwrapped;
    }

    private static ReconciliationKey Normalize(string name, string? kind)
    {
        var value = name.Trim();
        var unwrapped = value.Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);
        if (string.Equals(kind, "table-variable", StringComparison.OrdinalIgnoreCase)
            || unwrapped.StartsWith('@'))
            return ReconciliationKey.AnonymousVariables;
        if (unwrapped.Equals("Worktable", StringComparison.OrdinalIgnoreCase)
            || unwrapped.Equals("Workfile", StringComparison.OrdinalIgnoreCase))
            return ReconciliationKey.Object(unwrapped);

        var dot = unwrapped.LastIndexOf('.');
        return ReconciliationKey.Object(dot >= 0 ? unwrapped[(dot + 1)..] : unwrapped);
    }

    private readonly record struct ReconciliationKey(bool IsAnonymousVariableGroup, string Name)
    {
        internal static ReconciliationKey AnonymousVariables => new(true, string.Empty);
        internal static ReconciliationKey Object(string name) => new(false, name);
    }

    private sealed class ReconciliationKeyComparer : IEqualityComparer<ReconciliationKey>
    {
        internal static readonly ReconciliationKeyComparer Instance = new();

        public bool Equals(ReconciliationKey left, ReconciliationKey right) =>
            left.IsAnonymousVariableGroup == right.IsAnonymousVariableGroup
            && StringComparer.OrdinalIgnoreCase.Equals(left.Name, right.Name);

        public int GetHashCode(ReconciliationKey key) => HashCode.Combine(
            key.IsAnonymousVariableGroup,
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }

    private sealed class CounterTotals
    {
        internal long LogicalReads { get; private set; }
        internal long LobLogicalReads { get; private set; }
        internal long Scans { get; private set; }

        internal void Add(long logicalReads, long lobLogicalReads, long scans)
        {
            LogicalReads = SaturatingAdd(LogicalReads, logicalReads);
            LobLogicalReads = SaturatingAdd(LobLogicalReads, lobLogicalReads);
            Scans = SaturatingAdd(Scans, scans);
        }

        public override bool Equals(object? obj) => obj is CounterTotals other
            && LogicalReads == other.LogicalReads
            && LobLogicalReads == other.LobLogicalReads
            && Scans == other.Scans;

        public override int GetHashCode() => HashCode.Combine(LogicalReads, LobLogicalReads, Scans);

        private static long SaturatingAdd(long left, long right) => right > 0 && left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
    }
}