using System.IO;
using System.Text;

using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHarness.Core;

internal sealed record ExpandedSql(string Text, IReadOnlyList<SqlHarnessParameter> Parameters);

internal sealed record ListValues(string Name, string Type, IReadOnlyList<string?> Values);

internal static class ListParameterExpander
{
    internal static ExpandedSql Expand(string sql, IReadOnlyList<ListValues> lists)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(lists);

        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        var tokens = parser.GetTokenStream(new StringReader(sql), out _);
        var referencedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (token.TokenType == TSqlTokenType.Variable)
                referencedNames.Add(token.Text);
            else if (token.TokenType == TSqlTokenType.Identifier && token.Text.StartsWith("IN@", StringComparison.OrdinalIgnoreCase))
                referencedNames.Add("@" + token.Text[3..]);
        }

        var parameters = new List<SqlHarnessParameter>();
        var listNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expandedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists.Where(list => referencedNames.Contains("@" + SqlParameterNames.Normalize(list.Name))))
        {
            var baseName = "@" + SqlParameterNames.Normalize(list.Name);
            if (!listNames.Add(baseName))
                throw new SqlHarnessSafetyException($"Duplicate SQL list parameter '{baseName}'.");
            if (list.Values.Count == 0)
            {
                var parameter = SqlParameterParser.Bind(new SqlHarnessParameterInput(baseName, list.Type, null));
                if (!expandedNames.Add(parameter.Name))
                    throw new SqlHarnessSafetyException($"List expansion parameter '{parameter.Name}' collides with another list parameter.");
                replacements.Add(baseName, "(SELECT " + parameter.Name + " WHERE 1 = 0)");
                parameters.Add(parameter);
                continue;
            }

            var expanded = new List<string>(list.Values.Count);
            for (var index = 0; index < list.Values.Count; index++)
            {
                if (parameters.Count >= ParameterSetResolver.MaximumCommandParameters)
                    throw new SqlHarnessSafetyException($"SQL parameter '{baseName}' exceeds the {ParameterSetResolver.MaximumCommandParameters} final command parameter limit.");
                var name = baseName + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var parameter = SqlParameterParser.Bind(new SqlHarnessParameterInput(name, list.Type, list.Values[index]));
                if (!expandedNames.Add(parameter.Name))
                    throw new SqlHarnessSafetyException($"List expansion parameter '{parameter.Name}' collides with another list parameter.");
                parameters.Add(parameter);
                expanded.Add(parameter.Name);
            }
            replacements.Add(baseName, string.Join(", ", expanded));
        }

        if (parameters.Count > ParameterSetResolver.MaximumCommandParameters)
        {
            var list = lists.LastOrDefault();
            throw new SqlHarnessSafetyException($"SQL parameter '@{SqlParameterNames.Normalize(list?.Name)}' exceeds the {ParameterSetResolver.MaximumCommandParameters} final command parameter limit.");
        }

        var significant = tokens.Select((token, index) => (token, index))
            .Where(item => item.token.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile))
            .ToArray();
        var tokenToSignificant = significant
            .Select((item, ordinal) => new { TokenIndex = item.index, SignificantIndex = ordinal })
            .ToDictionary(item => item.TokenIndex, item => item.SignificantIndex);
        var edits = new List<(int Start, int Length, string Text)>();

        foreach (var (token, tokenIndex) in tokens.Select((token, index) => (token, index)))
        {
            if (token.TokenType == TSqlTokenType.Identifier)
            {
                var joinedList = lists.FirstOrDefault(list =>
                    string.Equals(token.Text, "IN@" + SqlParameterNames.Normalize(list.Name), StringComparison.OrdinalIgnoreCase));
                if (joinedList is not null)
                {
                    var joinedReplacement = replacements["@" + SqlParameterNames.Normalize(joinedList.Name)];
                    if (joinedList.Values.Count > 0)
                        joinedReplacement = "(" + joinedReplacement + ")";
                    edits.Add((token.Offset, token.Text.Length, "IN " + joinedReplacement));
                }
            }
            if (token.TokenType != TSqlTokenType.Variable || !replacements.TryGetValue(token.Text, out var replacement))
                continue;
            var significantIndex = tokenToSignificant[tokenIndex];
            var previous = significantIndex > 0 ? significant[significantIndex - 1].token : null;
            var beforePrevious = significantIndex > 1 ? significant[significantIndex - 2].token : null;
            var next = significantIndex + 1 < significant.Length ? significant[significantIndex + 1].token : null;
            var wrappedInClause = previous?.TokenType == TSqlTokenType.LeftParenthesis
                && beforePrevious?.TokenType == TSqlTokenType.In
                && next?.TokenType == TSqlTokenType.RightParenthesis;
            var listValues = lists.First(list => string.Equals("@" + SqlParameterNames.Normalize(list.Name), token.Text, StringComparison.OrdinalIgnoreCase));
            if (listValues.Values.Count == 0 && wrappedInClause)
            {
                edits.Add((token.Offset, token.Text.Length, replacement[1..^1]));
            }
            else if (listValues.Values.Count == 0)
            {
                var prefix = beforePrevious?.TokenType == TSqlTokenType.In && token.Offset > 0 && !char.IsWhiteSpace(sql[token.Offset - 1]) ? " " : string.Empty;
                edits.Add((token.Offset, token.Text.Length, prefix + replacement));
            }
            else if (wrappedInClause)
            {
                edits.Add((token.Offset, token.Text.Length, replacement));
            }
            else
            {
                var prefix = beforePrevious?.TokenType == TSqlTokenType.In && token.Offset > 0 && !char.IsWhiteSpace(sql[token.Offset - 1]) ? " " : string.Empty;
                edits.Add((token.Offset, token.Text.Length, prefix + "(" + replacement + ")"));
            }
        }

        var output = new StringBuilder(sql);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            output.Remove(edit.Start, edit.Length);
            output.Insert(edit.Start, edit.Text);
        }
        return new ExpandedSql(output.ToString(), parameters);
    }
}
