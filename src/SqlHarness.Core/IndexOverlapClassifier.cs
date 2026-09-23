namespace SqlHarness.Core;

internal sealed record IndexOverlapMatch(
    IndexOverlapClassification Classification,
    ExistingIndex? BestIndex,
    int MatchedKeyColumnCount,
    int CandidateKeyColumnCount,
    IReadOnlyList<string> MissingIncludeColumns);

internal static class IndexOverlapClassifier
{
    internal static IndexOverlapMatch FindBest(
        IndexCandidate candidate,
        IReadOnlyList<ExistingIndex> indexes)
    {
        var candidateKeyCount = candidate.EqualityColumns.Count + candidate.InequalityColumns.Count;
        Ranked? best = null;
        foreach (var index in indexes)
        {
            if (IsIgnored(candidate, index))
                continue;

            var ranked = Classify(candidate, index);
            if (ranked is null)
                continue;

            if (best is null || Outranks(ranked, best))
                best = ranked;
        }

        if (best is null)
        {
            return new IndexOverlapMatch(
                IndexOverlapClassification.NewShape,
                null,
                0,
                candidateKeyCount,
                []);
        }

        return new IndexOverlapMatch(
            best.Classification,
            best.Index,
            best.MatchedKeyColumnCount,
            candidateKeyCount,
            best.MissingIncludeColumns);
    }

    private static bool IsIgnored(IndexCandidate candidate, ExistingIndex index) =>
        StringComparer.OrdinalIgnoreCase.Equals(index.Type, "HEAP")
        || !StringComparer.OrdinalIgnoreCase.Equals(candidate.Schema, index.Schema)
        || !StringComparer.OrdinalIgnoreCase.Equals(candidate.Table, index.Table);

    private static Ranked? Classify(IndexCandidate candidate, ExistingIndex index)
    {
        // Equality columns may lead the key in any order. The first other column ends that segment.
        var remainingEquality = new List<string>(candidate.EqualityColumns);
        var keyPosition = 0;
        var eqMatched = 0;
        while (keyPosition < index.KeyColumns.Count)
        {
            var keyColumn = index.KeyColumns[keyPosition];
            var found = -1;
            for (var i = 0; i < remainingEquality.Count; i++)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(remainingEquality[i], keyColumn))
                    continue;

                found = i;
                break;
            }

            if (found < 0)
                break;

            remainingEquality.RemoveAt(found);
            eqMatched++;
            keyPosition++;
        }

        var ineqMatched = 0;
        while (ineqMatched < candidate.InequalityColumns.Count
            && keyPosition < index.KeyColumns.Count
            && StringComparer.OrdinalIgnoreCase.Equals(
                candidate.InequalityColumns[ineqMatched],
                index.KeyColumns[keyPosition]))
        {
            ineqMatched++;
            keyPosition++;
        }

        var matchedKey = eqMatched + ineqMatched;
        var fullKey = eqMatched == candidate.EqualityColumns.Count
            && ineqMatched == candidate.InequalityColumns.Count;
        IReadOnlyList<string> missing;
        IndexOverlapClassification classification;
        if (fullKey)
        {
            missing = MissingIncludes(candidate, index);
            classification = missing.Count == 0
                ? IndexOverlapClassification.Covered
                : IndexOverlapClassification.IncludeGap;
        }
        else if (matchedKey > 0)
        {
            missing = [];
            classification = IndexOverlapClassification.PartialKey;
        }
        else
        {
            return null;
        }

        // Filter implication is unknown, so a structural covered or include-gap cannot stay above partial-key.
        if (index.HasFilter
            && classification is IndexOverlapClassification.Covered or IndexOverlapClassification.IncludeGap)
        {
            classification = IndexOverlapClassification.PartialKey;
        }

        return new Ranked(index, classification, matchedKey, Strength(classification), missing);
    }

    private static string[] MissingIncludes(IndexCandidate candidate, ExistingIndex index)
    {
        var coverage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in index.KeyColumns)
            coverage.Add(column);

        foreach (var column in index.IncludeColumns)
            coverage.Add(column);

        var missing = new List<string>();
        foreach (var column in candidate.IncludeColumns)
        {
            if (!coverage.Contains(column))
                missing.Add(column);
        }

        return missing.ToArray();
    }

    private static int Strength(IndexOverlapClassification classification) => classification switch
    {
        IndexOverlapClassification.Covered => 4,
        IndexOverlapClassification.IncludeGap => 3,
        IndexOverlapClassification.PartialKey => 2,
        IndexOverlapClassification.NewShape => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    private static bool Outranks(Ranked next, Ranked current)
    {
        if (next.Strength != current.Strength)
            return next.Strength > current.Strength;

        if (next.MatchedKeyColumnCount != current.MatchedKeyColumnCount)
            return next.MatchedKeyColumnCount > current.MatchedKeyColumnCount;

        if (next.MissingIncludeColumns.Count != current.MissingIncludeColumns.Count)
            return next.MissingIncludeColumns.Count < current.MissingIncludeColumns.Count;

        if (next.Index.Disabled != current.Index.Disabled)
            return !next.Index.Disabled;

        return next.Index.IndexId < current.Index.IndexId;
    }

    private sealed record Ranked(
        ExistingIndex Index,
        IndexOverlapClassification Classification,
        int MatchedKeyColumnCount,
        int Strength,
        IReadOnlyList<string> MissingIncludeColumns);
}