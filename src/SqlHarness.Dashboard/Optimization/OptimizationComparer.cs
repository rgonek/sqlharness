using System.Globalization;

namespace SqlHarness.Dashboard;

public static class OptimizationComparer
{
    public static OptimizationComparison Compare(ConditionsGroup group, IReadOnlyList<CompareEvidence> compares,
        string a, string b, string selectedCase, string aggregate)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(compares);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(selectedCase);
        ArgumentNullException.ThrowIfNull(aggregate);

        var versionsA = VersionsFor(group, a);
        var versionsB = VersionsFor(group, b);
        var sameSql = versionsA.Count == 1 && versionsB.Count == 1
            && versionsA[0].Scope == versionsB[0].Scope && versionsA[0].Hash == versionsB[0].Hash;
        if (sameSql)
            return Empty(a, b, PairBasis.SameSql, "same SQL", selectedCase, aggregate);

        if (versionsA.Count != 1 || versionsB.Count != 1 || versionsA[0].Scope != versionsB[0].Scope)
            return Empty(a, b, PairBasis.ConditionsDiffer, "conditions differ", selectedCase, aggregate);

        var versionA = versionsA[0];
        var versionB = versionsB[0];
        var hashA = versionA.Hash;
        var hashB = versionB.Hash;
        var scopeCompares = compares.Where(compare =>
                string.Equals(compare.Fingerprint ?? string.Empty, group.Fingerprint, StringComparison.Ordinal)
                && string.Equals(NormalizeScope(compare.Scope), versionA.Scope, StringComparison.Ordinal))
            .OrderBy(compare => compare.StartedAt, StringComparer.Ordinal)
            .ThenBy(compare => compare.OperationId)
            .ToArray();
        if (!TryResolveIdentity(group, scopeCompares, out var subject, out var database))
            return Empty(a, b, PairBasis.ConditionsDiffer, "conditions differ", selectedCase, aggregate);

        var groupCompares = scopeCompares.Where(compare => compare.Subject == subject
                && string.Equals(compare.Database ?? string.Empty, database, StringComparison.Ordinal))
            .ToArray();
        var direct = groupCompares.Where(compare => Connects(compare, hashA, hashB))
            .LastOrDefault();

        var path = EquivalentPath(groupCompares, hashA, hashB);
        var equivalence = EquivalenceLabel(group, versionA.Scope, direct, path);
        var directForward = direct is not null && direct.BaselineHash == hashA;
        var basis = direct is not null ? PairBasis.MeasuredTogether : PairBasis.DifferentRunsSameConditions;

        VersionSnapshot? before;
        VersionSnapshot? after;
        if (direct is not null)
        {
            before = Snapshot(direct, directForward ? "baseline" : "candidate");
            after = Snapshot(direct, directForward ? "candidate" : "baseline");
        }
        else
        {
            before = LatestSnapshot(groupCompares, hashA);
            after = LatestSnapshot(groupCompares, hashB);
        }

        if (before is null || after is null)
            return Empty(a, b, PairBasis.ConditionsDiffer, "conditions differ", selectedCase, aggregate);

        var totalCaseIds = groupCompares.SelectMany(compare => compare.Cases)
            .Select(item => item.CaseId).Distinct(StringComparer.Ordinal).ToArray();
        var caseLabels = groupCompares.SelectMany(compare => compare.Cases)
            .GroupBy(item => item.CaseId, StringComparer.Ordinal)
            .ToDictionary(grouping => grouping.Key,
                grouping => grouping.Select(item => item.Label).LastOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? grouping.Key,
                StringComparer.Ordinal);
        var commonIds = before.Metrics.Keys.Intersect(after.Metrics.Keys, StringComparer.Ordinal)
            .Where(id => before.Metrics[id].LogicalReadsMedian.HasValue && after.Metrics[id].LogicalReadsMedian.HasValue)
            .OrderBy(id => CaseOrder(id, caseLabels), StringComparer.Ordinal)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var differingCases = direct?.Cases.Where(item => item.ResultsEquivalent == false)
            .Select(item => item.CaseId).ToHashSet(StringComparer.Ordinal) ?? [];
        var caseOptions = commonIds.Select(id => new CaseOption(id, caseLabels.GetValueOrDefault(id, id),
            differingCases.Contains(id) ? "results differ" : Outcome(before.Metrics[id].LogicalReadsMedian!.Value,
                after.Metrics[id].LogicalReadsMedian!.Value))).ToArray();
        var validSelection = selectedCase == "all" || commonIds.Contains(selectedCase, StringComparer.Ordinal);
        var resolvedSelection = validSelection ? selectedCase : "all";
        var selectedIds = resolvedSelection == "all" ? commonIds : [resolvedSelection];
        var coverage = commonIds.Length < totalCaseIds.Length
            ? $"{commonIds.Length.ToString(CultureInfo.InvariantCulture)} of {totalCaseIds.Length.ToString(CultureInfo.InvariantCulture)} cases"
            : string.Empty;
        var normalizedAggregate = aggregate.Equals("mean", StringComparison.OrdinalIgnoreCase) ? "mean" : "sum";
        var tableIoUnavailable = IsPostgres(before.Engine ?? after.Engine);
        var tableRows = tableIoUnavailable
            ? []
            : BuildTableRows(before, after, selectedIds, normalizedAggregate);
        var collapsed = tableIoUnavailable ? null : Collapse(tableRows);
        var visibleTables = tableIoUnavailable ? tableRows : tableRows.Where(row => !IsCollapsed(row)).ToArray();
        var elapsedBefore = Aggregate(before.Metrics, selectedIds, metric => metric.ElapsedMedianMs, normalizedAggregate);
        var elapsedAfter = Aggregate(after.Metrics, selectedIds, metric => metric.ElapsedMedianMs, normalizedAggregate);
        var cpuBefore = Aggregate(before.Metrics, selectedIds, metric => metric.CpuMedianMs, normalizedAggregate);
        var cpuAfter = Aggregate(after.Metrics, selectedIds, metric => metric.CpuMedianMs, normalizedAggregate);
        var buffersBefore = AggregateBuffers(before.Metrics, selectedIds, normalizedAggregate);
        var buffersAfter = AggregateBuffers(after.Metrics, selectedIds, normalizedAggregate);
        var mixed = Mixed(caseOptions, before.Metrics, after.Metrics);

        return new OptimizationComparison(a, b, basis, direct?.OperationId, equivalence, caseOptions,
            resolvedSelection, normalizedAggregate, coverage, mixed, visibleTables, collapsed,
            new ElapsedInfo(elapsedBefore, elapsedAfter, direct?.RegressionVerdict, basis == PairBasis.DifferentRunsSameConditions))
        {
            BeforeAction = Action(before),
            AfterAction = Action(after),
            TableIoUnavailable = tableIoUnavailable,
            CpuBeforeMs = cpuBefore,
            CpuAfterMs = cpuAfter,
            BuffersBefore = buffersBefore,
            BuffersAfter = buffersAfter,
        };

        static OptimizationActionReference Action(VersionSnapshot snapshot) =>
            new(snapshot.OperationId, snapshot.Variant, snapshot.Metrics.Keys.Order(StringComparer.Ordinal).ToArray());

    }

    private static OptimizationComparison Empty(string a, string b, PairBasis basis, string equivalence,
        string selectedCase, string aggregate) =>
        new(a, b, basis, null, equivalence, [], selectedCase, aggregate, string.Empty, null, [], null,
            new ElapsedInfo(null, null, null, false));

    private static IReadOnlyList<VersionIdentity> VersionsFor(ConditionsGroup group, string version) => group.Scopes
        .SelectMany(scope => scope.Versions.Where(row => row.Version == version)
            .Select(row => new VersionIdentity(NormalizeScope(scope.Scope)!, row.Hash)))
        .Distinct().ToArray();

    private static bool TryResolveIdentity(ConditionsGroup group, IReadOnlyList<CompareEvidence> compares,
        out string? subject, out string? database)
    {
        subject = group.Subject;
        database = group.Database;
        if (subject is not null && database is not null)
            return true;

        var knownSubject = subject;
        var knownDatabase = database;
        var candidates = compares.Where(compare =>
                (knownSubject is null || compare.Subject == knownSubject)
                && (knownDatabase is null || string.Equals(compare.Database ?? string.Empty, knownDatabase, StringComparison.Ordinal)))
            .Select(compare => (compare.Subject, Database: compare.Database ?? string.Empty))
            .Distinct().ToArray();
        if (candidates.Length != 1 || candidates[0].Subject is null)
            return false;

        subject ??= candidates[0].Subject;
        database ??= candidates[0].Database;
        return true;
    }

    private static string? NormalizeScope(string? scope) => scope == "whole query" ? "whole" : scope;

    private static bool Connects(CompareEvidence compare, string a, string b) =>
        compare.BaselineHash == a && compare.CandidateHash == b
        || compare.BaselineHash == b && compare.CandidateHash == a;

    private static IReadOnlyList<string>? EquivalentPath(IReadOnlyList<CompareEvidence> compares, string start, string end)
    {
        var pending = new Queue<(string Hash, IReadOnlyList<string> Path)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        pending.Enqueue((start, [start]));
        while (pending.TryDequeue(out var node))
        {
            foreach (var compare in compares.Where(IsEquivalent))
            {
                var nextHash = compare.BaselineHash == node.Hash ? compare.CandidateHash
                    : compare.CandidateHash == node.Hash ? compare.BaselineHash : null;
                if (nextHash is null || !visited.Add(nextHash))
                    continue;
                var path = node.Path.Append(nextHash).ToArray();
                if (nextHash == end)
                    return path;
                pending.Enqueue((nextHash, path));
            }
        }
        return null;
    }

    private static string EquivalenceLabel(ConditionsGroup group, string selectedScope,
        CompareEvidence? direct, IReadOnlyList<string>? path)
    {
        if (direct is not null)
        {
            if (IsEquivalent(direct))
                return "results equivalent (direct)";
            if (direct.ResultsEquivalent == false || direct.Cases.Any(item => item.ResultsEquivalent == false))
                return "results differ";
            return "results not compared";
        }
        return path is { Count: > 2 }
            ? $"results equivalent via {group.Scopes
                .FirstOrDefault(scope => NormalizeScope(scope.Scope) == selectedScope)?.Versions
                .FirstOrDefault(version => version.Hash == path[1])?.Version ?? VersionName(path[1])}"
            : "results not compared";
    }

    private static string VersionName(string hash) => hash.StartsWith("h", StringComparison.Ordinal) &&
        int.TryParse(hash.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? $"v{number.ToString(CultureInfo.InvariantCulture)}" : hash;

    private static bool IsEquivalent(CompareEvidence compare) =>
        compare.Status == "succeeded"
        && !string.Equals(compare.ComparisonMode, "Off", StringComparison.OrdinalIgnoreCase)
        && compare.ResultsEquivalent == true
        && compare.Cases.Count > 0
        && compare.Cases.All(item => item.ResultsEquivalent == true);

    private static VersionSnapshot? LatestSnapshot(IReadOnlyList<CompareEvidence> compares, string hash)
    {
        foreach (var compare in compares.Reverse())
        {
            var variant = compare.BaselineHash == hash ? "baseline"
                : compare.CandidateHash == hash ? "candidate" : null;
            if (variant is not null)
                return Snapshot(compare, variant);
        }
        return null;
    }

    private static VersionSnapshot Snapshot(CompareEvidence compare, string variant)
    {
        var metrics = compare.Metrics.Where(metric => metric.Variant.Equals(variant, StringComparison.OrdinalIgnoreCase))
            .GroupBy(metric => metric.CaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return new VersionSnapshot(compare.Engine, compare.OperationId, variant, metrics);
    }

    private static string Outcome(long before, long after)
    {
        var difference = (decimal)before - after;
        var threshold = Math.Max(10m, before * 0.01m);
        if (difference >= threshold) return "better";
        if (difference <= -threshold) return "worse";
        return "same";
    }

    private static string CaseOrder(string id, IReadOnlyDictionary<string, string> labels) => labels.GetValueOrDefault(id, id);

    private static IReadOnlyList<TableRow> BuildTableRows(VersionSnapshot before, VersionSnapshot after,
        IReadOnlyList<string> caseIds, string aggregate)
    {
        var tables = caseIds.SelectMany(id => before.Metrics.GetValueOrDefault(id)?.TableRows.Select(row => row.Table) ?? [])
            .Concat(caseIds.SelectMany(id => after.Metrics.GetValueOrDefault(id)?.TableRows.Select(row => row.Table) ?? []))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return tables.Select(table =>
        {
            var readsBefore = AggregateTable(before.Metrics, caseIds, table, aggregate, Reads);
            var readsAfter = AggregateTable(after.Metrics, caseIds, table, aggregate, Reads);
            var scansBefore = AggregateTable(before.Metrics, caseIds, table, aggregate, Scans);
            var scansAfter = AggregateTable(after.Metrics, caseIds, table, aggregate, Scans);
            return new TableRow(table, scansBefore, scansAfter, readsBefore, readsAfter,
                Change(readsBefore, readsAfter));
        })
        .OrderByDescending(row => AbsoluteChange(row.ReadsBefore, row.ReadsAfter))
        .ThenBy(row => row.Table, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static long? AggregateTable(IReadOnlyDictionary<string, CompareMetricEvidence> metrics,
        IReadOnlyList<string> caseIds, string table, string aggregate, Func<CompareTableIoEvidence, long?> value)
    {
        var values = new List<long>();
        foreach (var id in caseIds)
        {
            if (!metrics.TryGetValue(id, out var metric)) return null;
            var rows = metric.TableRows.Where(row => row.Table.Equals(table, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (rows.Length == 0)
            {
                if (metric.TableRows.Count == 0) return null;
                values.Add(0);
                continue;
            }
            var rowValues = rows.Select(value).ToArray();
            if (rowValues.Any(item => !item.HasValue)) return null;
            values.Add(SaturatingSum(rowValues.Select(item => item!.Value)));
        }
        return Reduce(values, aggregate);
    }

    private static long? Reads(CompareTableIoEvidence row) => row.LobLogicalReads is null
        ? null
        : SaturatingSum([row.LogicalReads, row.LobLogicalReads.Value]);

    private static long? Scans(CompareTableIoEvidence row) => row.Scans;

    private static string Change(long? before, long? after)
    {
        if (before is null || after is null) return "—";
        if (before == 0)
            return after == 0 ? "0%" : $"+{after.Value.ToString(CultureInfo.InvariantCulture)} pages";
        var delta = (decimal)after.Value - before.Value;
        var percent = Math.Round(delta * 100m / before.Value, MidpointRounding.AwayFromZero);
        if (percent == 0) return "0%";
        var amount = Math.Abs(percent).ToString("0", CultureInfo.InvariantCulture);
        return percent < 0 ? $"−{amount}%" : $"+{amount}%";
    }

    private static decimal AbsoluteChange(long? before, long? after) => before is null || after is null
        ? decimal.MinValue
        : Math.Abs((decimal)after.Value - before.Value);

    private static CollapsedRow? Collapse(IReadOnlyList<TableRow> rows)
    {
        var collapsed = rows.Where(IsCollapsed).ToArray();
        if (collapsed.Length == 0) return null;
        var reads = collapsed.All(row => row.ReadsAfter.HasValue)
            ? SaturatingSum(collapsed.Select(row => row.ReadsAfter!.Value))
            : (long?)null;
        var readsLabel = reads?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        return new CollapsedRow(collapsed.Length, $"table variables / worktables ({collapsed.Length.ToString(CultureInfo.InvariantCulture)}, {readsLabel} reads)");
    }

    private static bool IsCollapsed(TableRow row) =>
        IsTemporaryName(row.Table) || row.ReadsBefore == 0 && row.ReadsAfter == 0;

    private static bool IsTemporaryName(string table)
    {
        var name = table.Trim();
        return name.StartsWith("@", StringComparison.Ordinal)
            || name.Contains("table variable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("worktable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("workfile", StringComparison.OrdinalIgnoreCase);
    }

    private static double? Aggregate(IReadOnlyDictionary<string, CompareMetricEvidence> metrics,
        IReadOnlyList<string> caseIds, Func<CompareMetricEvidence, double?> value, string aggregate)
    {
        var values = new List<double>();
        foreach (var id in caseIds)
        {
            if (!metrics.TryGetValue(id, out var metric) || value(metric) is not { } item) return null;
            values.Add(item);
        }
        if (values.Count == 0) return null;
        return aggregate == "mean" ? values.Average() : values.Sum();
    }

    private static PostgresBuffers? AggregateBuffers(IReadOnlyDictionary<string, CompareMetricEvidence> metrics,
        IReadOnlyList<string> caseIds, string aggregate)
    {
        if (caseIds.Count == 0 || caseIds.Any(id => !metrics.TryGetValue(id, out var metric) || metric.Postgres is null))
            return null;
        var buffers = caseIds.Select(id => metrics[id].Postgres!).ToArray();
        return new PostgresBuffers(Buffer(buffers, item => item.SharedHit, aggregate),
            Buffer(buffers, item => item.SharedRead, aggregate),
            Buffer(buffers, item => item.SharedDirtied, aggregate),
            Buffer(buffers, item => item.SharedWritten, aggregate),
            Buffer(buffers, item => item.TempRead, aggregate),
            Buffer(buffers, item => item.TempWritten, aggregate));
    }

    private static long? Buffer(IReadOnlyList<PostgresBuffers> buffers, Func<PostgresBuffers, long?> selector, string aggregate)
    {
        var values = buffers.Select(selector).ToArray();
        if (values.Any(value => !value.HasValue)) return null;
        return Reduce(values.Select(value => value!.Value).ToList(), aggregate);
    }

    private static long? Reduce(IReadOnlyList<long> values, string aggregate) => values.Count == 0 ? null
        : aggregate == "mean" ? (long)Math.Round(values.Average(), MidpointRounding.AwayFromZero)
        : SaturatingSum(values);

    private static long SaturatingSum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            if (value > 0 && total > long.MaxValue - value) return long.MaxValue;
            if (value < 0 && total < long.MinValue - value) return long.MinValue;
            total += value;
        }
        return total;
    }

    private static bool IsPostgres(string? engine) =>
        engine?.Contains("postgres", StringComparison.OrdinalIgnoreCase) == true;

    private static MixedWarning? Mixed(IReadOnlyList<CaseOption> cases,
        IReadOnlyDictionary<string, CompareMetricEvidence> before,
        IReadOnlyDictionary<string, CompareMetricEvidence> after)
    {
        var better = cases.Count(item => item.Outcome == "better");
        var worse = cases.Count(item => item.Outcome == "worse");
        if (better == 0 || worse == 0) return null;
        var increases = cases.Where(item => item.Outcome == "worse")
            .Select(item =>
            {
                var baseline = before[item.Id].LogicalReadsMedian!.Value;
                var candidate = after[item.Id].LogicalReadsMedian!.Value;
                var percent = baseline == 0 ? 100d : (candidate - (double)baseline) * 100d / baseline;
                return (Case: item, Percent: percent);
            })
            .OrderByDescending(item => item.Percent)
            .ThenBy(item => item.Case.Label, StringComparer.Ordinal)
            .First();
        return new MixedWarning(better, worse, increases.Case.Label, increases.Percent);
    }

    private sealed record VersionSnapshot(string? Engine, long OperationId, string Variant,
        IReadOnlyDictionary<string, CompareMetricEvidence> Metrics);

    private sealed record VersionIdentity(string Scope, string Hash);
}
