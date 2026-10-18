using System.Globalization;
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

/// <summary>Pairs Showplan statements to spans in the exact SQL text sent to the SQL Server session.</summary>
internal static class StatementSpanPairer
{
    private const int MaximumPlanCharacters = 16 * 1024 * 1024;
    private const int SqlServerStatementTextPrefixLength = 3999;
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    internal static IReadOnlyList<StatementSpanPair> Pair(string? executedText, string document)
    {
        var planStatements = ReadPlanStatements(document);
        if (planStatements.Length == 0)
            return [];

        if (string.IsNullOrEmpty(executedText))
            return planStatements.Select(statement => Unmatched(statement)).ToArray();

        var parsed = SqlServerDocument.Parse(executedText);
        var script = parsed.Fragment as TSqlScript;
        if (parsed.HasErrors || script is null)
            return planStatements.Select(statement => Unmatched(statement)).ToArray();

        var statements = script.Batches.SelectMany(batch => batch.Statements)
            .Where(statement => statement.StartOffset >= 0 && statement.FragmentLength > 0
                && statement.StartOffset <= executedText.Length - statement.FragmentLength)
            .Select(statement => new ExecutedStatement(statement.StartOffset, statement.FragmentLength,
                executedText.Substring(statement.StartOffset, statement.FragmentLength)))
            .ToArray();
        if (statements.Length == 0)
            return planStatements.Select(statement => Unmatched(statement)).ToArray();

        var pairs = new List<StatementSpanPair>(planStatements.Length);
        var nextStatement = 0;
        foreach (var plan in planStatements)
        {
            var candidate = FindCandidate(plan.Text, statements, nextStatement);
            if (candidate.Index is int index)
            {
                var statement = statements[index];
                pairs.Add(new StatementSpanPair(statement.Start, statement.Length, candidate.MatchKind,
                    plan.StatementId, plan.StatementCompId));
                nextStatement = index + 1;
            }
            else
                pairs.Add(new StatementSpanPair(null, null, candidate.MatchKind, plan.StatementId, plan.StatementCompId));
        }
        return pairs;
    }

    private static Candidate FindCandidate(string planText, ExecutedStatement[] statements, int start)
    {
        var remaining = statements.Select((statement, index) => (statement, index)).Where(item => item.index >= start).ToArray();
        if (remaining.Length == 0)
            return Candidate.Unmatched;

        var plan = TrimTerminator(planText);
        var exact = remaining.FirstOrDefault(item => string.Equals(TrimTerminator(item.statement.Text), plan, StringComparison.Ordinal));
        if (exact.statement is not null)
            return new Candidate(exact.index, "exact");

        if (TryStripPspSuffix(plan, out var stripped))
        {
            var psp = remaining.FirstOrDefault(item => NormalizedEquals(item.statement.Text, stripped));
            if (psp.statement is not null)
                return new Candidate(psp.index, "psp_suffix");
        }

        var normalized = remaining.FirstOrDefault(item => NormalizedEquals(item.statement.Text, plan));
        if (normalized.statement is not null)
            return new Candidate(normalized.index, "normalized");

        if (plan.Length >= SqlServerStatementTextPrefixLength)
        {
            var prefix = plan[..SqlServerStatementTextPrefixLength];
            var prefixes = remaining.Where(item => TrimTerminator(item.statement.Text).StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (prefixes.Length == 1)
                return new Candidate(prefixes[0].index, "prefix");
            if (prefixes.Length > 1)
                return Candidate.Ambiguous;
        }

        return Candidate.Unmatched;
    }

    private static bool NormalizedEquals(string source, string plan)
    {
        var sourceTokens = Tokens(TrimTerminator(source));
        var planTokens = Tokens(TrimTerminator(plan));
        return sourceTokens is not null && planTokens is not null
            && string.Equals(sourceTokens, planTokens, StringComparison.Ordinal);
    }

    private static string? Tokens(string sql)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(new StringReader(sql), out var errors);
        if (errors.Count > 0)
            return null;
        return string.Concat(tokens.Where(token => token.TokenType != TSqlTokenType.WhiteSpace)
            .Select(token => token.Text));
    }

    private static bool TryStripPspSuffix(string text, out string stripped)
    {
        var index = text.LastIndexOf(" option (PLAN PER VALUE", StringComparison.OrdinalIgnoreCase);
        if (index < 0 || !text.EndsWith(')'))
        {
            stripped = text;
            return false;
        }
        stripped = text[..index].TrimEnd();
        return true;
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

    private static StatementSpanPair Unmatched(PlanStatement statement) =>
        new(null, null, "unmatched", statement.StatementId, statement.StatementCompId);

    private sealed record PlanStatement(string Text, int? StatementId, int? StatementCompId);
    private sealed record ExecutedStatement(int Start, int Length, string Text);
    private sealed record Candidate(int? Index, string MatchKind)
    {
        internal static Candidate Unmatched { get; } = new(null, "unmatched");
        internal static Candidate Ambiguous { get; } = new(null, "ambiguous");
    }
}
