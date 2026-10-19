using System.Globalization;

namespace SqlHarness.Dashboard;

public static class OptimizationSummaryBuilder
{
    public static OptimizationSection Build(IReadOnlyList<CompareEvidence> compares)
    {
        ArgumentNullException.ThrowIfNull(compares);

        var ordered = compares.OrderBy(compare => compare.StartedAt, StringComparer.Ordinal)
            .ThenBy(compare => compare.OperationId).ToArray();
        var versionMaps = new Dictionary<(string Subject, string Scope), Dictionary<string, string>>();
        var nextVersion = new Dictionary<(string Subject, string Scope), int>();
        var versioned = ordered.Select(compare => new VersionedCompare(compare,
            Version(compare.Subject, compare.Scope, compare.BaselineHash),
            Version(compare.Subject, compare.Scope, compare.CandidateHash))).ToArray();

        var subjects = versioned.Where(item => IsAssigned(item.Compare) && (!item.Compare.IsMeasure || item.Compare.BaselineHash is not null))
            .GroupBy(item => (item.Compare.Subject!, Database: item.Compare.Database ?? string.Empty))
            .OrderBy(group => group.Key.Item1, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Database, StringComparer.Ordinal)
            .Select(group => new OptimizationSubject(
                group.Key.Item1,
                group.Key.Database,
                group.Select(item => item.Compare.Engine).FirstOrDefault(engine => !string.IsNullOrEmpty(engine)) ?? string.Empty,
                group.GroupBy(item => item.Compare.Fingerprint ?? string.Empty, StringComparer.Ordinal)
                    .OrderBy(fingerprint => fingerprint.Key, StringComparer.Ordinal)
                    .Select(fingerprint => BuildGroup(fingerprint.Key, fingerprint.ToArray()))
                    .ToArray()))
            .ToArray();

        var unassigned = ordered.Where(compare => !compare.IsMeasure && !IsAssigned(compare))
            .Select(compare => new UnassignedCompare(compare.OperationId, compare.StartedAt, compare.Status))
            .ToArray();
        return new OptimizationSection(subjects, unassigned);

        string? Version(string? subject, string? scope, string? hash)
        {
            if (subject is null || scope is null || hash is null)
                return null;
            var key = (subject, scope);
            if (!versionMaps.TryGetValue(key, out var map))
                versionMaps.Add(key, map = new Dictionary<string, string>(StringComparer.Ordinal));
            if (!map.TryGetValue(hash, out var version))
            {
                nextVersion.TryGetValue(key, out var ordinal);
                version = $"v{ordinal.ToString(CultureInfo.InvariantCulture)}";
                map.Add(hash, version);
                nextVersion[key] = ordinal + 1;
            }
            return version;
        }
    }

    private static ConditionsGroup BuildGroup(string fingerprint, IReadOnlyList<VersionedCompare> compares)
    {
        var scopes = compares.GroupBy(item => item.Compare.Scope!, StringComparer.Ordinal)
            .OrderBy(scope => scope.Key == "whole" ? 0 : 1)
            .ThenBy(scope => scope.Key, StringComparer.Ordinal)
            .Select(scope =>
            {
                var scopeCompares = scope.OrderBy(item => item.Compare.StartedAt, StringComparer.Ordinal)
                    .ThenBy(item => item.Compare.OperationId).ToArray();
                var defaultA = scopeCompares.Select(item => item.BaselineVersion).FirstOrDefault(version => version is not null)
                    ?? scopeCompares.Select(item => item.CandidateVersion).FirstOrDefault(version => version is not null)
                    ?? "v0";
                var defaultB = DefaultB(scopeCompares, defaultA);
                return new ScopeVersions(scope.Key == "whole" ? "whole query" : scope.Key, BuildVersions(scopeCompares))
                {
                    DefaultA = defaultA,
                    DefaultB = defaultB == defaultA ? null : defaultB,
                };
            })
            .ToArray();

        var primary = compares.Where(item => item.Compare.Scope == "whole")
            .OrderBy(item => item.Compare.StartedAt, StringComparer.Ordinal).ThenBy(item => item.Compare.OperationId).ToArray();
        if (primary.Length == 0)
            primary = compares.OrderBy(item => item.Compare.StartedAt, StringComparer.Ordinal).ThenBy(item => item.Compare.OperationId).ToArray();

        var defaultA = primary.Select(item => item.BaselineVersion).FirstOrDefault(version => version is not null)
            ?? primary.Select(item => item.CandidateVersion).FirstOrDefault(version => version is not null)
            ?? "v0";
        var defaultB = DefaultB(primary, defaultA);

        return new ConditionsGroup(fingerprint, GroupLabel(compares), scopes, defaultA,
            defaultB == defaultA ? null : defaultB)
        {
            Subject = compares.Select(item => item.Compare.Subject).FirstOrDefault(subject => subject is not null),
            Database = compares.Select(item => item.Compare.Database ?? string.Empty).FirstOrDefault(),
        };
    }

    private static string DefaultB(IReadOnlyList<VersionedCompare> observations, string start)
    {
        var equivalent = NewestEquivalentReachable(observations.Where(item => !item.Compare.IsMeasure).ToArray(), start);
        if (equivalent != start)
            return equivalent;
        return observations.SelectMany(item => new[] { item.BaselineVersion, item.CandidateVersion })
            .Where(version => version is not null).Select(version => version!)
            .OrderBy(NumericVersion).ThenBy(version => version, StringComparer.Ordinal).LastOrDefault() ?? start;
    }

    private static string NewestEquivalentReachable(IReadOnlyList<VersionedCompare> compares, string start)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Queue<string>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var version))
        {
            foreach (var attempt in compares)
            {
                if (attempt.BaselineVersion != version || attempt.CandidateVersion is null || !Equivalent(attempt.Compare))
                    continue;
                if (reachable.Add(attempt.CandidateVersion))
                    pending.Enqueue(attempt.CandidateVersion);
            }
        }

        return reachable.OrderBy(NumericVersion).ThenBy(version => version, StringComparer.Ordinal).Last();
    }

    private static IReadOnlyList<VersionRow> BuildVersions(IReadOnlyList<VersionedCompare> compares)
    {
        var versions = compares.SelectMany(item => item.Compare.IsMeasure
                ? new[] { (item.BaselineVersion, item.Compare.BaselineHash, item.Compare, Variant: "measure") }
                : new[]
            {
                (item.BaselineVersion, item.Compare.BaselineHash, item.Compare, Variant: "baseline"),
                (item.CandidateVersion, item.Compare.CandidateHash, item.Compare, Variant: "candidate"),
            })
            .Where(item => item.Item1 is not null && item.Item2 is not null)
            .GroupBy(item => (Version: item.Item1!, Hash: item.Item2!), EqualityComparer<(string Version, string Hash)>.Default)
            .OrderBy(group => NumericVersion(group.Key.Version))
            .ThenBy(group => group.Key.Version, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Hash, StringComparer.Ordinal)
            .Select(group =>
            {
                var measurements = group.OrderBy(item => item.Compare.StartedAt, StringComparer.Ordinal)
                    .ThenBy(item => item.Compare.OperationId).ToArray();
                var latest = measurements[^1];
                var metrics = MetricsFor(latest.Compare, latest.Variant);
                var reads = metrics.Select(metric => metric.LogicalReadsMedian).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
                var scans = metrics.Select(metric => metric.ScansSum).ToArray();
                var elapsed = metrics.Select(metric => metric.ElapsedMedianMs).Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
                var latestCompare = measurements.LastOrDefault(item => !item.Compare.IsMeasure);
                var outcome = latest.Compare.IsMeasure
                    ? latestCompare.Compare is null ? "measured only" : CaseOutcome(latestCompare.Compare, latestCompare.Variant)
                    : CaseOutcome(latest.Compare, latest.Variant);
                var verdictCompare = latest.Compare.IsMeasure ? latestCompare.Compare : latest.Compare;
                var verdictVariant = latest.Compare.IsMeasure ? latestCompare.Variant : latest.Variant;
                var verdictMetricsBefore = verdictCompare is null ? [] : MetricsFor(verdictCompare, "baseline");
                var verdictMetricsAfter = verdictCompare is null ? [] : MetricsFor(verdictCompare, "candidate");
                var verdictBasis = verdictCompare is not null && verdictVariant is not null
                    && verdictVariant.Equals("candidate", StringComparison.OrdinalIgnoreCase)
                    && verdictCompare.Cases.Count > 0
                    && verdictCompare.Cases.All(item =>
                    {
                        var before = verdictMetricsBefore.FirstOrDefault(metric => metric.CaseId == item.CaseId);
                        var after = verdictMetricsAfter.FirstOrDefault(metric => metric.CaseId == item.CaseId);
                        return before is not null && after is not null
                            && OptimizationComparer.CompareCase(before, after).VerdictBasis == "changed statements";
                    }) ? "changed statements" : "whole batch";
                return new VersionRow(
                    group.Key.Version,
                    group.Key.Hash,
                    reads.Length == 0 ? null : Sum(reads),
                    reads.Length == 0 ? null : Sum(reads) / reads.Length,
                    scans.Length == 0 || scans.Any(value => !value.HasValue) ? null : Sum(scans.Select(value => value!.Value)),
                    Median(elapsed),
                    outcome,
                    measurements.Select(item => item.Compare.OperationId).Distinct().Order().ToArray(),
                    latest.Compare.OperationId)
                {
                    VerdictBasis = verdictBasis,
                };
            })
            .ToArray();
        return versions;
    }

    private static IReadOnlyList<CompareMetricEvidence> MetricsFor(CompareEvidence compare, string variant) =>
        compare.Metrics.Where(metric => metric.Variant.Equals(variant, StringComparison.OrdinalIgnoreCase))
            .GroupBy(metric => metric.CaseId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(metric => metric.CaseId, StringComparer.Ordinal)
            .ToArray();

    private static string CaseOutcome(CompareEvidence compare, string variant)
    {
        if (compare.Status != "succeeded")
            return "incomplete";
        if (string.Equals(compare.ComparisonMode, "Off", StringComparison.OrdinalIgnoreCase))
            return "equivalence off";

        var cases = compare.Cases;
        if (compare.ResultsEquivalent == false || cases.Any(item => item.ResultsEquivalent == false))
            return "results differ";
        if (cases.Count == 0 || cases.Any(item => item.ResultsEquivalent is null))
            return "incomplete";

        var baseline = MetricsFor(compare, "baseline").ToDictionary(metric => metric.CaseId, StringComparer.Ordinal);
        var candidate = MetricsFor(compare, "candidate").ToDictionary(metric => metric.CaseId, StringComparer.Ordinal);
        if (cases.Any(item => !baseline.TryGetValue(item.CaseId, out var before) || before.LogicalReadsMedian is null
            || !candidate.TryGetValue(item.CaseId, out var after) || after.LogicalReadsMedian is null))
            return "incomplete";

        if (variant.Equals("baseline", StringComparison.OrdinalIgnoreCase))
            return $"{cases.Count.ToString(CultureInfo.InvariantCulture)}/{cases.Count.ToString(CultureInfo.InvariantCulture)} ok";

        var better = 0;
        var worse = 0;
        foreach (var item in cases)
        {
            var attribution = OptimizationComparer.CompareCase(baseline[item.CaseId], candidate[item.CaseId]);
            var beforeReads = attribution.VerdictBasis == "changed statements"
                ? attribution.ChangedBefore!.Value : baseline[item.CaseId].LogicalReadsMedian!.Value;
            var afterReads = attribution.VerdictBasis == "changed statements"
                ? attribution.ChangedAfter!.Value : candidate[item.CaseId].LogicalReadsMedian!.Value;
            if (Change(beforeReads, afterReads) == "better") better++;
            else if (Change(beforeReads, afterReads) == "worse") worse++;
        }
        return better + worse == 0
            ? $"{cases.Count.ToString(CultureInfo.InvariantCulture)}/{cases.Count.ToString(CultureInfo.InvariantCulture)} ok"
            : string.Join(" · ", new[]
            {
                better > 0 ? $"{better.ToString(CultureInfo.InvariantCulture)} better" : null,
                worse > 0 ? $"{worse.ToString(CultureInfo.InvariantCulture)} worse" : null,
            }.Where(value => value is not null));
    }

    private static string Change(long baseline, long candidate)
    {
        var difference = (decimal)baseline - candidate;
        if (baseline <= 0)
            return "same";
        var threshold = Math.Max(10m, baseline * 0.01m);
        if (difference >= threshold)
            return "better";
        if (difference <= -threshold)
            return "worse";
        return "same";
    }

    private static bool Equivalent(CompareEvidence compare) =>
        compare.Status == "succeeded"
        && !string.Equals(compare.ComparisonMode, "Off", StringComparison.OrdinalIgnoreCase)
        && compare.ResultsEquivalent == true
        && compare.Cases.Count > 0
        && compare.Cases.All(item => item.ResultsEquivalent == true);

    private static string GroupLabel(IReadOnlyList<VersionedCompare> compares)
    {
        var latestCases = compares.OrderBy(item => item.Compare.StartedAt, StringComparer.Ordinal)
            .ThenBy(item => item.Compare.OperationId).Last().Compare.Cases;
        if (latestCases.Count == 0)
            return "0 cases (values not stored)";
        var labels = latestCases.Select(item => item.Label).Where(label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.Ordinal).ToArray();
        var count = latestCases.Count.ToString(CultureInfo.InvariantCulture);
        if (labels.Length > 0 && labels.Any(label => !label.StartsWith("Case ", StringComparison.Ordinal)))
            return $"{count} cases: {CompactLabels(labels)}";
        var sets = latestCases.Select(item => item.SetName).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToArray();
        return sets.Length > 0 ? $"{count} cases: {string.Join(", ", sets)}" : $"{count} cases (values not stored)";
    }

    private static string CompactLabels(string[] labels)
    {
        var parts = labels.Select(label =>
        {
            var separator = label.LastIndexOf(" = ", StringComparison.Ordinal);
            return separator < 0 ? (Prefix: (string?)null, Value: label) : (Prefix: label[..separator], Value: label[(separator + 3)..]);
        }).ToArray();
        return parts.All(part => part.Prefix is not null && part.Prefix.Equals(parts[0].Prefix, StringComparison.Ordinal))
            ? $"{parts[0].Prefix} = {string.Join(", ", parts.Select(part => part.Value))}"
            : string.Join(", ", labels);
    }

    private static bool IsAssigned(CompareEvidence compare) => compare.Subject is not null && compare.Scope is not null;

    private static long NumericVersion(string version) =>
        version.Length > 1 && version[0] == 'v' && long.TryParse(version.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : long.MaxValue;

    private static long Sum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
            total = value > long.MaxValue - total ? long.MaxValue : total + value;
        return total;
    }

    private static double? Median(long[] values) => values.Length switch
    {
        0 => null,
        _ when values.Length % 2 == 1 => values[values.Length / 2],
        _ => ((double)values[values.Length / 2 - 1] + values[values.Length / 2]) / 2,
    };

    private sealed record VersionedCompare(CompareEvidence Compare, string? BaselineVersion, string? CandidateVersion);
}
