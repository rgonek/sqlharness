using System.Globalization;

namespace SqlHarness.Dashboard;

public static class OptimizationComparer
{
    public static OptimizationComparison Compare(ConditionsGroup group, IReadOnlyList<CompareEvidence> compares,
        string a, string b, string selectedCase, string aggregate, string view = "changed")
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
        var direct = groupCompares.Where(compare => !compare.IsMeasure && Connects(compare, hashA, hashB))
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
            .Select(CaseKey).Distinct(StringComparer.Ordinal).ToArray();
        var caseLabels = groupCompares.SelectMany(compare => compare.Cases)
            .GroupBy(CaseKey, StringComparer.Ordinal)
            .ToDictionary(grouping => grouping.Key,
                grouping => grouping.Select(item => item.Label).LastOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? grouping.Key,
                StringComparer.Ordinal);
        var postgresMetrics = IsPostgres(before.Engine ?? after.Engine);
        var commonIds = before.Metrics.Keys.Intersect(after.Metrics.Keys, StringComparer.Ordinal)
            .Where(id => postgresMetrics
                ? HasComparablePostgresMetric(before.Metrics[id], after.Metrics[id])
                : before.Metrics[id].LogicalReadsMedian.HasValue && after.Metrics[id].LogicalReadsMedian.HasValue)
            .OrderBy(id => CaseOrder(id, caseLabels), StringComparer.Ordinal)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var differingCases = direct?.Cases.Where(item => item.ResultsEquivalent == false)
            .Select(CaseKey).ToHashSet(StringComparer.Ordinal) ?? [];
        var caseComparisons = commonIds.ToDictionary(id => id, id => CompareCase(before.Metrics[id], after.Metrics[id]));
        var caseOptions = commonIds.Select(id => new CaseOption(id, caseLabels.GetValueOrDefault(id, id),
            differingCases.Contains(id) ? "results differ"
                : caseComparisons[id].VerdictBasis == "changed statements"
                    ? Outcome(caseComparisons[id].ChangedBefore!.Value, caseComparisons[id].ChangedAfter!.Value)
                    : before.Metrics[id].LogicalReadsMedian is { } readsBefore && after.Metrics[id].LogicalReadsMedian is { } readsAfter
                        ? Outcome(readsBefore, readsAfter) : "unavailable")).ToArray();
        var validSelection = selectedCase == "all" || commonIds.Contains(selectedCase, StringComparer.Ordinal);
        var resolvedSelection = validSelection ? selectedCase : "all";
        var selectedIds = resolvedSelection == "all" ? commonIds : [resolvedSelection];
        var coverage = commonIds.Length < totalCaseIds.Length
            ? $"{commonIds.Length.ToString(CultureInfo.InvariantCulture)} of {totalCaseIds.Length.ToString(CultureInfo.InvariantCulture)} cases"
            : string.Empty;
        var normalizedAggregate = aggregate.Equals("mean", StringComparison.OrdinalIgnoreCase) ? "mean" : "sum";
        var allCasesConfirmed = selectedIds.Length > 0 && selectedIds.All(id => caseComparisons.TryGetValue(id, out var item)
            && item.VerdictBasis == "changed statements");
        var verdictBasis = allCasesConfirmed ? "changed statements" : "whole batch";
        var requestedView = view is "whole" or "statements" ? view : "changed";
        var effectiveView = !allCasesConfirmed && requestedView != "statements" ? "whole" : requestedView;
        var tableIoUnavailable = postgresMetrics;
        var tableRows = tableIoUnavailable
            ? []
            : allCasesConfirmed && effectiveView is not "whole"
                ? BuildStatementTableRows(before.Metrics, after.Metrics, selectedIds, caseComparisons, normalizedAggregate)
                : BuildTableRows(before, after, selectedIds, normalizedAggregate);
        var collapsed = tableIoUnavailable ? null : Collapse(tableRows);
        var visibleTables = tableIoUnavailable ? tableRows : tableRows.Where(row => !IsCollapsed(row)).ToArray();
        var elapsedBefore = Aggregate(before.Metrics, selectedIds, metric => metric.ElapsedMedianMs, normalizedAggregate);
        var elapsedAfter = Aggregate(after.Metrics, selectedIds, metric => metric.ElapsedMedianMs, normalizedAggregate);
        var cpuBefore = Aggregate(before.Metrics, selectedIds, metric => metric.CpuMedianMs, normalizedAggregate);
        var cpuAfter = Aggregate(after.Metrics, selectedIds, metric => metric.CpuMedianMs, normalizedAggregate);
        var buffersBefore = AggregateBuffers(before.Metrics, selectedIds, normalizedAggregate);
        var buffersAfter = AggregateBuffers(after.Metrics, selectedIds, normalizedAggregate);
        var mixed = Mixed(caseOptions, before.Metrics, after.Metrics, caseComparisons);
        var allStatementRows = BuildStatementRows(before.Metrics, after.Metrics, selectedIds, caseComparisons, normalizedAggregate, resolvedSelection != "all");
        var statementRows = effectiveView == "changed"
            ? allStatementRows.Where(row => row.Status != "shared").ToArray()
            : allStatementRows;
        var sharedBefore = ReduceNullable(selectedIds.Select(id => caseComparisons.GetValueOrDefault(id).SharedBefore), normalizedAggregate);
        var sharedAfter = ReduceNullable(selectedIds.Select(id => caseComparisons.GetValueOrDefault(id).SharedAfter), normalizedAggregate);
        var batchBefore = AggregateReads(before.Metrics, selectedIds, normalizedAggregate);
        var batchAfter = AggregateReads(after.Metrics, selectedIds, normalizedAggregate);
        var wholeBatch = new WholeBatchLine(batchBefore, batchAfter, sharedBefore,
            batchBefore is { } wb && batchAfter is { } wa && sharedBefore is { } sh
                ? $"whole batch: {wb.ToString(CultureInfo.InvariantCulture)} → {wa.ToString(CultureInfo.InvariantCulture)} reads (shared statements: {sh.ToString(CultureInfo.InvariantCulture)})"
                : "whole batch: reads unavailable")
        { SharedBefore = sharedBefore, SharedAfter = sharedAfter };
        wholeBatch = wholeBatch with
        {
            ChangedBefore = allCasesConfirmed && batchBefore.HasValue && sharedBefore.HasValue
                ? batchBefore.Value - sharedBefore.Value : null,
            ChangedAfter = allCasesConfirmed && batchAfter.HasValue && sharedAfter.HasValue
                ? batchAfter.Value - sharedAfter.Value : null,
        };

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
            VerdictBasis = verdictBasis,
            View = effectiveView,
            WholeBatch = wholeBatch,
            Statements = statementRows,
            FallbackNote = allCasesConfirmed ? null : "per-statement attribution incomplete",
            StatementMetricsUnavailableReason = resolvedSelection == "all" ? "Select a single case for statement CPU, elapsed, LOB logical reads, grants and spill warnings." : null,
            OmittedStatementsBefore = selectedIds.Where(before.Metrics.ContainsKey).Sum(id => before.Metrics[id].OmittedStatements),
            OmittedStatementsAfter = selectedIds.Where(after.Metrics.ContainsKey).Sum(id => after.Metrics[id].OmittedStatements),
        };

        static OptimizationActionReference Action(VersionSnapshot snapshot)
        {
            var caseIdsByFingerprint = snapshot.Metrics.ToDictionary(pair => pair.Key,
                pair => pair.Value.CaseId, StringComparer.Ordinal);
            return new OptimizationActionReference(snapshot.OperationId, snapshot.Variant,
                caseIdsByFingerprint.Values.Order(StringComparer.Ordinal).ToArray())
            {
                CaseIdsByFingerprint = caseIdsByFingerprint,
            };
        }

    }

    internal static (string VerdictBasis, long? ChangedBefore, long? ChangedAfter, long? SharedBefore, long? SharedAfter)
        CompareCase(CompareMetricEvidence before, CompareMetricEvidence after)
    {
        if (before.StatementAttribution != "confirmed" || after.StatementAttribution != "confirmed"
            || before.OmittedStatements > 0 || after.OmittedStatements > 0
            || before.Statements.Count == 0 || after.Statements.Count == 0
            || before.Statements.Any(row => row.Status != "confirmed") || after.Statements.Any(row => row.Status != "confirmed"))
            return ("whole batch", null, null, null, null);

        var a = before.Statements.ToDictionary(row => (row.Key, row.Occurrence));
        var b = after.Statements.ToDictionary(row => (row.Key, row.Occurrence));
        long sharedA = 0, sharedB = 0, changedA = 0, changedB = 0;
        foreach (var key in a.Keys.Union(b.Keys))
        {
            if (a.TryGetValue(key, out var left) && b.TryGetValue(key, out var right) && IsShared(left, right))
            {
                if (left.Reads is null || right.Reads is null) return ("whole batch", null, null, null, null);
                sharedA = SaturatingAdd(sharedA, left.Reads.Value);
                sharedB = SaturatingAdd(sharedB, right.Reads.Value);
            }
            else
            {
                if (a.TryGetValue(key, out left))
                {
                    if (left.Reads is null) return ("whole batch", null, null, null, null);
                    changedA = SaturatingAdd(changedA, left.Reads.Value);
                }
                if (b.TryGetValue(key, out right))
                {
                    if (right.Reads is null) return ("whole batch", null, null, null, null);
                    changedB = SaturatingAdd(changedB, right.Reads.Value);
                }
            }
        }
        return ("changed statements", changedA, changedB, sharedA, sharedB);
    }

    private static bool IsShared(CompareStatementEvidence a, CompareStatementEvidence b)
    {
        if (a.PlanHash is null || b.PlanHash is null || a.PlanHash != b.PlanHash || a.Scans != b.Scans
            || a.Reads is null || b.Reads is null) return false;
        var difference = Math.Abs((decimal)a.Reads.Value - b.Reads.Value);
        return difference < 10m || difference < Math.Max(a.Reads.Value, b.Reads.Value) * 0.01m;
    }

    private static IReadOnlyList<StatementRow> BuildStatementRows(
        IReadOnlyDictionary<string, CompareMetricEvidence> before, IReadOnlyDictionary<string, CompareMetricEvidence> after,
        IReadOnlyList<string> cases,
        IReadOnlyDictionary<string, (string VerdictBasis, long? ChangedBefore, long? ChangedAfter, long? SharedBefore, long? SharedAfter)> caseComparisons,
        string aggregate, bool singleCase)
    {
        var keys = cases.SelectMany(id => (before.GetValueOrDefault(id)?.Statements ?? []).Select(row => (row.Key, row.Occurrence)))
            .Concat(cases.SelectMany(id => (after.GetValueOrDefault(id)?.Statements ?? []).Select(row => (row.Key, row.Occurrence))))
            .Distinct().ToArray();
        return keys.Select(key =>
        {
            var left = cases.Select(id => before.GetValueOrDefault(id)?.Statements.FirstOrDefault(row => (row.Key, row.Occurrence) == key)).ToArray();
            var right = cases.Select(id => after.GetValueOrDefault(id)?.Statements.FirstOrDefault(row => (row.Key, row.Occurrence) == key)).ToArray();
            var l = left.Where(row => row is not null).Cast<CompareStatementEvidence>().ToArray();
            var r = right.Where(row => row is not null).Cast<CompareStatementEvidence>().ToArray();
            var sharedEverywhere = cases.Count > 0 && cases.Select((id, index) => (id, index))
                .All(pair => left[pair.index] is { } x && right[pair.index] is { } y && IsShared(x, y));
            var presentSome = l.Length < cases.Count || r.Length < cases.Count;
            var status = sharedEverywhere ? "shared"
                : l.Length == 0 ? "only-candidate"
                : r.Length == 0 ? "only-baseline"
                : presentSome ? "not-executed" : "different-work";
            var readsBefore = ReduceNullable(cases.Select((id, index) => StatementCounter(before.GetValueOrDefault(id), left[index], null,
                static row => row.Reads, static _ => null)), aggregate);
            var readsAfter = ReduceNullable(cases.Select((id, index) => StatementCounter(after.GetValueOrDefault(id), right[index], null,
                static row => row.Reads, static _ => null)), aggregate);
            var scansBefore = ReduceNullable(cases.Select((id, index) => StatementCounter(before.GetValueOrDefault(id), left[index], null,
                static row => row.Scans, static _ => null)), aggregate);
            var scansAfter = ReduceNullable(cases.Select((id, index) => StatementCounter(after.GetValueOrDefault(id), right[index], null,
                static row => row.Scans, static _ => null)), aggregate);
            var objectNames = l.Concat(r).SelectMany(row => row.Objects).Select(row => row.Table)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var objects = objectNames.Select(name =>
            {
                var objectReadsBefore = ReduceNullable(cases.Select((id, index) => StatementCounter(before.GetValueOrDefault(id), left[index], name,
                    static _ => null, static row => row.ReadsAfter ?? row.ReadsBefore)), aggregate);
                var objectReadsAfter = ReduceNullable(cases.Select((id, index) => StatementCounter(after.GetValueOrDefault(id), right[index], name,
                    static _ => null, static row => row.ReadsAfter ?? row.ReadsBefore)), aggregate);
                var objectScansBefore = ReduceNullable(cases.Select((id, index) => StatementCounter(before.GetValueOrDefault(id), left[index], name,
                    static _ => null, static row => row.ScansAfter ?? row.ScansBefore)), aggregate);
                var objectScansAfter = ReduceNullable(cases.Select((id, index) => StatementCounter(after.GetValueOrDefault(id), right[index], name,
                    static _ => null, static row => row.ScansAfter ?? row.ScansBefore)), aggregate);
                return new TableRow(name, objectScansBefore, objectScansAfter, objectReadsBefore, objectReadsAfter,
                    Change(objectReadsBefore, objectReadsAfter))
                {
                    LobLogicalReadsBefore = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed"
                        ? LobForObject(left[0]!, right[0], name, beforeSide: true) : null,
                    LobLogicalReadsAfter = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed"
                        ? LobForObject(right[0]!, left[0], name, beforeSide: false) : null,
                };
            }).ToArray();
            var attribution = cases.All(id => caseComparisons.GetValueOrDefault(id).VerdictBasis == "changed statements") ? "confirmed" : "partial";
            var text = l.Concat(r).Select(row => row.Text).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? $"statement {key.Key}";
            return new StatementRow(key.Key, text, status, readsBefore, readsAfter, scansBefore, scansAfter,
                l.Concat(r).Select(row => row.Executions).FirstOrDefault(value => value.HasValue),
                Change(readsBefore, readsAfter), attribution,
                l.Concat(r).SelectMany(row => row.HiddenWork).Distinct().ToArray(),
                objects)
            {
                CpuBeforeMs = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed" ? left[0]!.CpuMs : null,
                CpuAfterMs = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed" ? right[0]!.CpuMs : null,
                ElapsedBeforeMs = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed" ? left[0]!.ElapsedMs : null,
                ElapsedAfterMs = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed" ? right[0]!.ElapsedMs : null,
                GrantRequestedKbBefore = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed" ? left[0]!.GrantRequestedKb : null,
                GrantRequestedKbAfter = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed" ? right[0]!.GrantRequestedKb : null,
                GrantGrantedKbBefore = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed" ? left[0]!.GrantGrantedKb : null,
                GrantGrantedKbAfter = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed" ? right[0]!.GrantGrantedKb : null,
                GrantMaxUsedKbBefore = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed" ? left[0]!.GrantMaxUsedKb : null,
                GrantMaxUsedKbAfter = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed" ? right[0]!.GrantMaxUsedKb : null,
                HasSpillWarningBefore = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed" ? left[0]!.HasSpillWarning : null,
                HasSpillWarningAfter = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed" ? right[0]!.HasSpillWarning : null,
                PlanRepetitionBefore = singleCase && left[0]?.Status == "confirmed" && before[cases[0]].StatementAttribution == "confirmed" ? left[0]!.PlanRepetition : null,
                PlanRepetitionAfter = singleCase && right[0]?.Status == "confirmed" && after[cases[0]].StatementAttribution == "confirmed" ? right[0]!.PlanRepetition : null,
                Occurrence = key.Occurrence,
                PlanHashBefore = l.Select(row => row.PlanHash).FirstOrDefault(value => value is not null),
                PlanHashAfter = r.Select(row => row.PlanHash).FirstOrDefault(value => value is not null),
                PlanLinkBefore = l.Select(row => row.PlanLink).FirstOrDefault(value => value is not null),
                PlanLinkAfter = r.Select(row => row.PlanLink).FirstOrDefault(value => value is not null),
                UdfElapsedMilliseconds = l.Concat(r).Select(row => row.UdfElapsedMilliseconds).FirstOrDefault(value => value.HasValue),
            };
        }).OrderBy(row => row.Status == "shared" ? 1 : 0)
            .ThenByDescending(row => AbsoluteChange(row.ReadsBefore, row.ReadsAfter)).ToArray();
    }

    private static long? LobForObject(CompareStatementEvidence statement, CompareStatementEvidence? other, string name, bool beforeSide)
    {
        var rows = statement.Objects.Where(row => row.Table.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var otherRows = other?.Objects.Where(row => row.Table.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        if (rows.Length != 1 || otherRows.Length > 1
            || (otherRows.Length == 1 && !string.Equals(rows[0].ObjectKind, otherRows[0].ObjectKind, StringComparison.Ordinal)))
            return null;
        return beforeSide ? rows[0].LobLogicalReadsBefore : rows[0].LobLogicalReadsAfter;
    }

    private static long? StatementCounter(CompareMetricEvidence? metric, CompareStatementEvidence? statement,
        string? objectName, Func<CompareStatementEvidence, long?> statementValue, Func<TableRow, long?> objectValue)
    {
        if (metric is null || !string.Equals(metric.StatementAttribution, "confirmed", StringComparison.Ordinal)) return null;
        if (statement is null) return 0;
        if (objectName is null) return statement.Status == "confirmed" ? statementValue(statement) : null;
        var matchingObjects = statement.Objects.Where(row => row.Table.Equals(objectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingObjects.Length == 0) return statement.Status == "confirmed" ? 0 : null;
        if (matchingObjects.Any(row => objectValue(row) is null)) return null;
        return matchingObjects.Aggregate(0L, (sum, row) => SaturatingAdd(sum, objectValue(row)!.Value));
    }

    private static long SaturatingAdd(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;
    private static long? ReduceNullable(IEnumerable<long?> values, string aggregate)
    {
        var rows = values.ToArray();
        if (rows.Length == 0 || rows.Any(value => value is null)) return null;
        var sum = rows.Aggregate(0L, (total, value) => SaturatingAdd(total, value!.Value));
        return aggregate == "mean" ? (long)Math.Round((double)sum / rows.Length, MidpointRounding.AwayFromZero) : sum;
    }

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var rows = values.ToArray();
        return rows.Any(value => value is null) ? null : rows.Aggregate(0L, (sum, value) => SaturatingAdd(sum, value!.Value));
    }

    private static long? AggregateReads(IReadOnlyDictionary<string, CompareMetricEvidence> metrics,
        IReadOnlyList<string> caseIds, string aggregate) =>
        caseIds.Any(id => !metrics.TryGetValue(id, out var metric) || metric.LogicalReadsMedian is null)
            ? null
            : ReduceNullable(caseIds.Select(id => metrics[id].LogicalReadsMedian), aggregate);

    private static string CaseKey(CompareCaseEvidence evidence) => evidence.MeasurementFingerprint ?? evidence.CaseId;

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
            var variant = compare.IsMeasure && compare.BaselineHash == hash ? "measure"
                : compare.BaselineHash == hash ? "baseline"
                : compare.CandidateHash == hash ? "candidate" : null;
            if (variant is not null)
                return Snapshot(compare, variant);
        }
        return null;
    }

    private static VersionSnapshot Snapshot(CompareEvidence compare, string variant)
    {
        var cases = compare.Cases.ToDictionary(item => item.CaseId, CaseKey, StringComparer.Ordinal);
        var metrics = compare.Metrics.Where(metric => metric.Variant.Equals(variant, StringComparison.OrdinalIgnoreCase))
            .GroupBy(metric => cases.GetValueOrDefault(metric.CaseId, metric.MeasurementFingerprint ?? metric.CaseId), StringComparer.Ordinal)
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

    private static IReadOnlyList<TableRow> BuildStatementTableRows(
        IReadOnlyDictionary<string, CompareMetricEvidence> before,
        IReadOnlyDictionary<string, CompareMetricEvidence> after,
        IReadOnlyList<string> caseIds,
        IReadOnlyDictionary<string, (string VerdictBasis, long? ChangedBefore, long? ChangedAfter, long? SharedBefore, long? SharedAfter)> comparisons,
        string aggregate)
    {
        var tables = caseIds.SelectMany(id => (before.GetValueOrDefault(id)?.Statements ?? [])
                .SelectMany(statement => statement.Objects.Select(row => row.Table)))
            .Concat(caseIds.SelectMany(id => (after.GetValueOrDefault(id)?.Statements ?? [])
                .SelectMany(statement => statement.Objects.Select(row => row.Table))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return tables.Select(table =>
        {
            var readsBefore = StatementObjectAggregate(before, after, caseIds, comparisons, table, aggregate, true, true);
            var readsAfter = StatementObjectAggregate(before, after, caseIds, comparisons, table, aggregate, false, true);
            var scansBefore = StatementObjectAggregate(before, after, caseIds, comparisons, table, aggregate, true, false);
            var scansAfter = StatementObjectAggregate(before, after, caseIds, comparisons, table, aggregate, false, false);
            return new TableRow(table, scansBefore, scansAfter, readsBefore, readsAfter, Change(readsBefore, readsAfter));
        }).OrderByDescending(row => AbsoluteChange(row.ReadsBefore, row.ReadsAfter))
          .ThenBy(row => row.Table, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static long? StatementObjectAggregate(
        IReadOnlyDictionary<string, CompareMetricEvidence> before,
        IReadOnlyDictionary<string, CompareMetricEvidence> after,
        IReadOnlyList<string> caseIds,
        IReadOnlyDictionary<string, (string VerdictBasis, long? ChangedBefore, long? ChangedAfter, long? SharedBefore, long? SharedAfter)> comparisons,
        string table, string aggregate, bool baseline, bool reads)
    {
        var totals = new List<long>();
        foreach (var id in caseIds)
        {
            var metrics = baseline ? before : after;
            if (!metrics.TryGetValue(id, out var metric)) return null;
            var other = (baseline ? after : before).GetValueOrDefault(id);
            var excluded = comparisons[id].VerdictBasis == "changed statements" && other is not null
                ? metric.Statements.Join(other.Statements, row => (row.Key, row.Occurrence), row => (row.Key, row.Occurrence),
                    (left, right) => (left, right)).Where(pair => IsShared(pair.left, pair.right))
                    .Select(pair => (pair.left.Key, pair.left.Occurrence)).ToHashSet()
                : [];
            var matching = metric.Statements.Where(row => !excluded.Contains((row.Key, row.Occurrence)))
                .SelectMany(row => row.Objects.Where(obj => obj.Table.Equals(table, StringComparison.OrdinalIgnoreCase)));
            var values = matching.Select(obj => reads ? obj.ReadsBefore : obj.ScansBefore).ToArray();
            if (values.Any(value => value is null)) return null;
            totals.Add(values.Length == 0 ? 0 : values.Aggregate(0L, (sum, value) => SaturatingAdd(sum, value!.Value)));
        }
        return ReduceNullable(totals.Select(value => (long?)value), aggregate);
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

    private static bool HasComparablePostgresMetric(CompareMetricEvidence before, CompareMetricEvidence after) =>
        before.ElapsedMedianMs.HasValue && after.ElapsedMedianMs.HasValue
        || before.CpuMedianMs.HasValue && after.CpuMedianMs.HasValue
        || before.Postgres is not null && after.Postgres is not null;

    private static MixedWarning? Mixed(IReadOnlyList<CaseOption> cases,
        IReadOnlyDictionary<string, CompareMetricEvidence> before,
        IReadOnlyDictionary<string, CompareMetricEvidence> after,
        IReadOnlyDictionary<string, (string VerdictBasis, long? ChangedBefore, long? ChangedAfter, long? SharedBefore, long? SharedAfter)> comparisons)
    {
        var comparableCases = cases.Where(item => item.Outcome is "better" or "worse" or "same").ToArray();
        var better = comparableCases.Count(item => item.Outcome == "better");
        var worse = comparableCases.Count(item => item.Outcome == "worse");
        if (better == 0 || worse == 0) return null;
        var increases = cases.Where(item => item.Outcome == "worse")
            .Select(item =>
            {
                var statement = comparisons[item.Id];
                var baseline = statement.VerdictBasis == "changed statements"
                    ? statement.ChangedBefore!.Value : before[item.Id].LogicalReadsMedian!.Value;
                var candidate = statement.VerdictBasis == "changed statements"
                    ? statement.ChangedAfter!.Value : after[item.Id].LogicalReadsMedian!.Value;
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