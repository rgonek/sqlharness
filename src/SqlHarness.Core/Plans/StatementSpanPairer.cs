using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core.Dialect;

namespace SqlHarness.Core;

internal sealed record StatementSpanPair(
    int? SpanStart,
    int? SpanLength,
    string MatchKind,
    int? StatementId,
    int? StatementCompId);

/// <summary>Pairs ordered Showplan documents to spans in the exact SQL text sent to the SQL Server session.</summary>
internal static class StatementSpanPairer
{
    private const int MaximumPlanCharacters = 16 * 1024 * 1024;
    private const int SqlServerStatementTextPrefixLength = 3999;
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static IReadOnlyList<StatementSpanPair> Pair(string? executedText, string document)
    {
        var paired = PairDocuments(executedText, [document]);
        return paired.Count == 0 ? [] : paired[0];
    }

    internal static IReadOnlyList<IReadOnlyList<StatementSpanPair>> PairDocuments(
        string? executedText,
        IReadOnlyList<string> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var planStatements = documents.Select(ReadPlanStatements).ToArray();
        if (planStatements.All(statements => statements.Length == 0))
            return planStatements.Select(_ => (IReadOnlyList<StatementSpanPair>)Array.Empty<StatementSpanPair>()).ToArray();

        if (string.IsNullOrEmpty(executedText))
            return UnmatchedDocuments(planStatements);

        var parsed = SqlServerDocument.Parse(executedText);
        var script = parsed.Fragment as TSqlScript;
        if (parsed.HasErrors || script is null)
            return UnmatchedDocuments(planStatements);

        var statements = script.Batches.SelectMany(batch => batch.Statements)
            .Where(statement => statement.StartOffset >= 0 && statement.FragmentLength > 0
                && statement.StartOffset <= executedText.Length - statement.FragmentLength)
            .Select(statement =>
            {
                var text = executedText.Substring(statement.StartOffset, statement.FragmentLength);
                return new ExecutedStatement(statement.StartOffset, statement.FragmentLength, text, TokenKey(TrimTerminator(text)));
            })
            .ToArray();
        if (statements.Length == 0)
            return UnmatchedDocuments(planStatements);

        var pairs = planStatements.Select(items => new StatementSpanPair?[items.Length]).ToArray();
        var orderedPlans = planStatements.SelectMany((items, documentIndex) => items.Select((plan, statementIndex) =>
            new PlanReference(documentIndex, statementIndex, plan))).ToArray();
        var pending = new List<PendingPlan>();
        var nextStatement = 0;
        foreach (var reference in orderedPlans)
        {
            var candidates = FindCandidates(reference.Plan.Text, statements, nextStatement);
            if (candidates.Indices.Length == 0)
            {
                pairs[reference.DocumentIndex][reference.StatementIndex] = Unmatched(reference.Plan);
                continue;
            }

            if (candidates.Indices.Length > 1)
            {
                pending.Add(new PendingPlan(reference, candidates));
                continue;
            }

            var anchor = candidates.Indices[0];
            ResolvePending(pending, anchor, statements, pairs);
            pairs[reference.DocumentIndex][reference.StatementIndex] = Pair(reference.Plan, statements[anchor], candidates.MatchKind);
            nextStatement = anchor + 1;
        }

        ResolvePending(pending, statements.Length, statements, pairs);
        return planStatements.Select((items, documentIndex) => (IReadOnlyList<StatementSpanPair>)items
            .Select((plan, statementIndex) => pairs[documentIndex][statementIndex] ?? Unmatched(plan))
            .ToArray()).ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<StatementSpanPair>> UnmatchedDocuments(PlanStatement[][] planStatements) =>
        planStatements.Select(items => (IReadOnlyList<StatementSpanPair>)items.Select(Unmatched).ToArray()).ToArray();

    private static CandidateSet FindCandidates(string planText, ExecutedStatement[] statements, int start)
    {
        var plan = TrimTerminator(planText);
        var exact = statements.Select((statement, index) => (statement, index))
            .Where(item => item.index >= start
                && string.Equals(TrimTerminator(item.statement.Text), plan, StringComparison.Ordinal))
            .Select(item => item.index).ToArray();
        if (exact.Length > 0)
            return new CandidateSet(exact, "exact", "exact:" + plan);

        if (TryStripPspSuffix(plan, out var stripped))
        {
            var pspKey = TokenKey(stripped);
            if (pspKey is not null)
            {
                var psp = MatchingTokenKeys(statements, start, pspKey);
                if (psp.Length > 0)
                    return new CandidateSet(psp, "psp_suffix", "psp:" + pspKey);
            }
        }

        var normalizedKey = TokenKey(plan);
        if (normalizedKey is not null)
        {
            var normalized = MatchingTokenKeys(statements, start, normalizedKey);
            if (normalized.Length > 0)
                return new CandidateSet(normalized, "normalized", "normalized:" + normalizedKey);
        }

        if (plan.Length >= SqlServerStatementTextPrefixLength)
        {
            var prefix = plan[..SqlServerStatementTextPrefixLength];
            var prefixes = statements.Select((statement, index) => (statement, index))
                .Where(item => item.index >= start
                    && TrimTerminator(item.statement.Text).StartsWith(prefix, StringComparison.Ordinal))
                .Select(item => item.index).ToArray();
            if (prefixes.Length > 0)
                return new CandidateSet(prefixes, "prefix", "prefix:" + prefix);
        }

        return CandidateSet.None;
    }

    private static int[] MatchingTokenKeys(ExecutedStatement[] statements, int start, string key) =>
        statements.Select((statement, index) => (statement, index))
            .Where(item => item.index >= start && string.Equals(item.statement.TokenKey, key, StringComparison.Ordinal))
            .Select(item => item.index).ToArray();

    private static void ResolvePending(
        List<PendingPlan> pending,
        int beforeStatement,
        ExecutedStatement[] statements,
        StatementSpanPair?[][] pairs)
    {
        if (pending.Count == 0)
            return;

        var options = pending.Select(item => item.Candidates.Indices.Where(index => index < beforeStatement).ToArray()).ToArray();
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            if (options[index].Length != 0)
                continue;
            var reference = pending[index].Reference;
            pairs[reference.DocumentIndex][reference.StatementIndex] = Unmatched(reference.Plan);
            pending.RemoveAt(index);
            options = pending.Select(item => item.Candidates.Indices.Where(candidate => candidate < beforeStatement).ToArray()).ToArray();
        }

        if (pending.Count == 0)
            return;

        if (options.All(item => item.Length == 1)
            && options.Select(item => item[0]).Zip(options.Select(item => item[0]).Skip(1), (left, right) => left < right).All(ordered => ordered))
        {
            for (var index = 0; index < pending.Count; index++)
            {
                var reference = pending[index].Reference;
                pairs[reference.DocumentIndex][reference.StatementIndex] = Pair(
                    reference.Plan, statements[options[index][0]], pending[index].Candidates.MatchKind);
            }
            pending.Clear();
            return;
        }

        var sameSignature = pending.All(item =>
            string.Equals(item.Candidates.MatchKind, pending[0].Candidates.MatchKind, StringComparison.Ordinal)
            && string.Equals(item.Candidates.Signature, pending[0].Candidates.Signature, StringComparison.Ordinal));
        if (sameSignature && options.All(item => item.SequenceEqual(options[0])) && options[0].Length == pending.Count)
        {
            for (var index = 0; index < pending.Count; index++)
            {
                var reference = pending[index].Reference;
                pairs[reference.DocumentIndex][reference.StatementIndex] = Pair(
                    reference.Plan, statements[options[0][index]], pending[index].Candidates.MatchKind);
            }
        }
        else
        {
            foreach (var item in pending)
                pairs[item.Reference.DocumentIndex][item.Reference.StatementIndex] = Ambiguous(item.Reference.Plan);
        }
        pending.Clear();
    }

    private static bool TryStripPspSuffix(string text, out string stripped)
    {
        var tokens = Tokens(text);
        if (tokens is not null)
        {
            var nestingDepth = 0;
            for (var index = 0; index + 5 < tokens.Count; index++)
            {
                var suffixStart = nestingDepth == 0
                    && TokenTextEquals(tokens[index], "OPTION")
                    && TokenTextEquals(tokens[index + 1], "(")
                    && TokenTextEquals(tokens[index + 2], "PLAN")
                    && TokenTextEquals(tokens[index + 3], "PER")
                    && TokenTextEquals(tokens[index + 4], "VALUE")
                    && TokenTextEquals(tokens[index + 5], "(");
                if (suffixStart)
                {
                    var depth = 0;
                    var balanced = true;
                    for (var cursor = index + 1; cursor < tokens.Count; cursor++)
                    {
                        if (TokenTextEquals(tokens[cursor], "("))
                            depth++;
                        else if (TokenTextEquals(tokens[cursor], ")"))
                        {
                            depth--;
                            if (depth < 0)
                            {
                                balanced = false;
                                break;
                            }
                            if (depth == 0)
                            {
                                if (cursor != tokens.Count - 1)
                                    balanced = false;
                                break;
                            }
                        }
                    }

                    if (balanced && depth == 0 && tokens[^1].Text == ")")
                    {
                        var offset = tokens[index].Offset;
                        if (offset >= 0 && offset <= text.Length)
                        {
                            stripped = text[..offset].TrimEnd();
                            return true;
                        }
                    }
                }

                if (TokenTextEquals(tokens[index], "("))
                    nestingDepth++;
                else if (TokenTextEquals(tokens[index], ")"))
                    nestingDepth = Math.Max(0, nestingDepth - 1);
            }
        }

        stripped = text;
        return false;
    }

    private static bool TokenTextEquals(TSqlParserToken token, string expected) =>
        string.Equals(token.Text, expected, StringComparison.OrdinalIgnoreCase);

    private static string? TokenKey(string sql)
    {
        var tokens = Tokens(sql);
        if (tokens is null)
            return null;

        var key = new StringBuilder();
        foreach (var token in tokens)
            key.Append((int)token.TokenType).Append(':').Append(token.Text.Length).Append(':').Append(token.Text);
        return key.ToString();
    }

    private static IReadOnlyList<TSqlParserToken>? Tokens(string sql)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(new StringReader(sql), out var errors);
        if (errors.Count > 0)
            return null;
        return tokens.Where(token => token.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile)).ToArray();
    }

    private static string TrimTerminator(string text) => text.Trim().TrimEnd(';').TrimEnd();

    private static PlanStatement[] ReadPlanStatements(string document)
    {
        if (string.IsNullOrWhiteSpace(document) || document.Length > MaximumPlanCharacters
            || document.AsSpan().TrimStart()[0] is '{' or '[')
            return [];
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumPlanCharacters,
                MaxCharactersFromEntities = 0,
            };
            using var text = new StringReader(document);
            using var reader = XmlReader.Create(text, settings);
            var xml = XDocument.Load(reader, LoadOptions.None);
            if (xml.Root?.Name != Showplan + "ShowPlanXML")
                return [];
            return xml.Descendants(Showplan + "StmtSimple").Select(statement => new PlanStatement(
                (string?)statement.Attribute("StatementText") ?? string.Empty,
                Integer(statement, "StatementId"), Integer(statement, "StatementCompId"))).ToArray();
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or FormatException or OverflowException)
        {
            return [];
        }
    }

    private static int? Integer(XElement element, string name) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static StatementSpanPair Pair(PlanStatement plan, ExecutedStatement executed, string matchKind) =>
        new(executed.Start, executed.Length, matchKind, plan.StatementId, plan.StatementCompId);

    private static StatementSpanPair Unmatched(PlanStatement statement) =>
        new(null, null, "unmatched", statement.StatementId, statement.StatementCompId);

    private static StatementSpanPair Ambiguous(PlanStatement statement) =>
        new(null, null, "ambiguous", statement.StatementId, statement.StatementCompId);

    private sealed record PlanStatement(string Text, int? StatementId, int? StatementCompId);
    private sealed record ExecutedStatement(int Start, int Length, string Text, string? TokenKey);
    private sealed record PlanReference(int DocumentIndex, int StatementIndex, PlanStatement Plan);
    private sealed record PendingPlan(PlanReference Reference, CandidateSet Candidates);
    private sealed record CandidateSet(int[] Indices, string MatchKind, string Signature)
    {
        internal static CandidateSet None { get; } = new([], "unmatched", string.Empty);
    }
}