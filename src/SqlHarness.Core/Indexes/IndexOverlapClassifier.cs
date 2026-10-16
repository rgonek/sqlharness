using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHarness.Core;

[JsonConverter(typeof(IndexOverlapVerdictJsonConverter))]
public enum IndexOverlapVerdict
{
    Covered,
    IncludeGap,
    PartialKey,
    NewShape,
}

internal sealed class IndexOverlapVerdictJsonConverter : JsonStringEnumConverter<IndexOverlapVerdict>
{
    public IndexOverlapVerdictJsonConverter()
        : base(JsonNamingPolicy.KebabCaseLower)
    {
    }
}

public static class IndexOverlapClassifier
{
    public static IndexOverlap Classify(MissingIndex hint, InventoryTable table)
    {
        var hintKeyColumns = hint.EqualityColumns.Count + hint.InequalityColumns.Count;
        var rowLocator = RowLocatorColumns(table);
        Ranked? best = null;
        foreach (var index in table.Indexes)
        {
            if (IsIgnored(index))
                continue;

            var ranked = Match(hint, index, rowLocator);
            if (ranked is null)
                continue;

            if (best is null || Outranks(ranked, best))
                best = ranked;
        }

        if (best is null)
        {
            return new IndexOverlap(
                IndexOverlapVerdict.NewShape,
                null,
                0,
                hintKeyColumns,
                []);
        }

        return new IndexOverlap(
            best.Verdict,
            new MatchedIndex(
                best.Index.Name,
                best.Index.KeyColumns,
                best.Index.IncludeColumns,
                best.Index.HasFilter),
            best.MatchedKeyColumns,
            hintKeyColumns,
            best.MissingIncludeColumns);
    }

    private static bool IsIgnored(InventoryIndex index) =>
        index.Disabled
        || index.Hypothetical
        || StringComparer.OrdinalIgnoreCase.Equals(index.Type, "HEAP")
        || index.Type.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> RowLocatorColumns(InventoryTable table)
    {
        foreach (var index in table.Indexes)
        {
            if (index.Disabled || index.Hypothetical)
                continue;
            if (!StringComparer.OrdinalIgnoreCase.Equals(index.Type, "CLUSTERED"))
                continue;

            return index.KeyColumns;
        }

        return [];
    }

    private static Ranked? Match(MissingIndex hint, InventoryIndex index, IReadOnlyList<string> rowLocator)
    {
        // Equality columns may lead the key in any order. The first other column ends that segment.
        var remainingEquality = new List<string>(hint.EqualityColumns);
        var keyPosition = 0;
        var equalityMatched = 0;
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
            equalityMatched++;
            keyPosition++;
        }

        var inequalityMatched = 0;
        while (inequalityMatched < hint.InequalityColumns.Count
            && keyPosition < index.KeyColumns.Count
            && StringComparer.OrdinalIgnoreCase.Equals(
                hint.InequalityColumns[inequalityMatched],
                index.KeyColumns[keyPosition]))
        {
            inequalityMatched++;
            keyPosition++;
        }

        var matchedKey = equalityMatched + inequalityMatched;
        var fullKey = equalityMatched == hint.EqualityColumns.Count
            && inequalityMatched == hint.InequalityColumns.Count;
        IReadOnlyList<string> missing;
        IndexOverlapVerdict verdict;
        if (fullKey)
        {
            missing = MissingIncludes(hint, index, rowLocator);
            verdict = missing.Count == 0
                ? IndexOverlapVerdict.Covered
                : IndexOverlapVerdict.IncludeGap;
        }
        else if (matchedKey > 0)
        {
            missing = [];
            verdict = IndexOverlapVerdict.PartialKey;
        }
        else
        {
            return null;
        }

        // Filter implication is unknown, so a structural covered or include-gap cannot stay above partial-key.
        if (index.HasFilter && verdict is IndexOverlapVerdict.Covered or IndexOverlapVerdict.IncludeGap)
            verdict = IndexOverlapVerdict.PartialKey;

        return new Ranked(index, verdict, matchedKey, Strength(verdict), missing);
    }

    private static string[] MissingIncludes(
        MissingIndex hint,
        InventoryIndex index,
        IReadOnlyList<string> rowLocator)
    {
        // A clustered leaf contains every table column, so a full key covers every include.
        if (StringComparer.OrdinalIgnoreCase.Equals(index.Type, "CLUSTERED"))
            return [];

        var coverage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in index.KeyColumns)
            coverage.Add(column);
        foreach (var column in index.IncludeColumns)
            coverage.Add(column);

        // A nonclustered rowstore leaf repeats the enabled clustered key as its row locator.
        if (StringComparer.OrdinalIgnoreCase.Equals(index.Type, "NONCLUSTERED"))
        {
            foreach (var column in rowLocator)
                coverage.Add(column);
        }

        var missing = new List<string>();
        foreach (var column in hint.IncludeColumns)
        {
            if (!coverage.Contains(column))
                missing.Add(column);
        }

        return missing.ToArray();
    }

    private static int Strength(IndexOverlapVerdict verdict) => verdict switch
    {
        IndexOverlapVerdict.Covered => 4,
        IndexOverlapVerdict.IncludeGap => 3,
        IndexOverlapVerdict.PartialKey => 2,
        IndexOverlapVerdict.NewShape => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    private static bool Outranks(Ranked next, Ranked current)
    {
        if (next.Strength != current.Strength)
            return next.Strength > current.Strength;

        if (next.MatchedKeyColumns != current.MatchedKeyColumns)
            return next.MatchedKeyColumns > current.MatchedKeyColumns;

        if (next.MissingIncludeColumns.Count != current.MissingIncludeColumns.Count)
            return next.MissingIncludeColumns.Count < current.MissingIncludeColumns.Count;

        return next.Index.IndexId < current.Index.IndexId;
    }

    private sealed record Ranked(
        InventoryIndex Index,
        IndexOverlapVerdict Verdict,
        int MatchedKeyColumns,
        int Strength,
        IReadOnlyList<string> MissingIncludeColumns);
}