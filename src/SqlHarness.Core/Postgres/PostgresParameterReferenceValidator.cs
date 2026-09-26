using System.Text;

using SqlParser;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace SqlHarness.Core.Postgres;

internal static class PostgresParameterReferenceValidator
{
    internal static IReadOnlyList<string> CollectReferences(string batch)
    {
        var dialect = new PostgreSqlDialect();
        Parse(batch, dialect);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(batch, dialect, references);
        return references.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void Validate(
        IReadOnlyList<SqlHarnessParameter> parameters,
        params string?[] batches)
    {
        if (parameters.Count == 0)
            return;

        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
                continue;

            // Npgsql rewrites @name and :name before PostgreSQL sees them. SqlParserCS
            // folds a bare @name into the prefix absolute-value operator, so the
            // placeholder is the token pair, not that unary expression. The lexer
            // already keeps the name out of comments, strings, and dollar quotes.
            var dialect = new PostgreSqlDialect();
            Parse(batch, dialect);
            Collect(batch, dialect, references);
        }

        foreach (var parameter in parameters)
        {
            if (!references.Contains(Canonical(parameter.Name)))
            {
                throw new SqlHarnessSafetyException(
                    $"SQL parameter '{parameter.Name}' is not referenced by the applicable batch.");
            }
        }
    }

    private static void Parse(string batch, PostgreSqlDialect dialect)
    {
        try
        {
            var statements = new SqlQueryParser().Parse(batch.AsSpan(), dialect);
            if (statements.Count == 0)
                throw new SqlHarnessSafetyException("SQL parameter references could not be parsed.");
        }
        catch (SqlHarnessSafetyException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SqlHarnessSafetyException("SQL parameter references could not be parsed.");
        }
    }

    private static void Collect(string batch, PostgreSqlDialect dialect, HashSet<string> references)
    {
        IList<Token> tokens;
        try
        {
            tokens = new Tokenizer().Tokenize(batch.AsSpan(), dialect);
        }
        catch (Exception)
        {
            throw new SqlHarnessSafetyException("SQL parameter references could not be parsed.");
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index] is not AtSign and not Colon)
                continue;

            if (TryReadName(tokens, index + 1, out var name))
                references.Add(name);
        }
    }

    // Npgsql's named-placeholder scan: letters, digits, '_', and '.' ; '$' ends the name.
    // A quoted identifier is not a placeholder, and whitespace after the sigil is not one either.
    private static bool TryReadName(IList<Token> tokens, int index, out string name)
    {
        name = "";
        if ((uint)index >= (uint)tokens.Count || !TryPiece(tokens[index], out var piece, out var truncated))
            return false;

        var builder = new StringBuilder(piece);
        index++;
        while (!truncated && index + 1 < tokens.Count && tokens[index] is Period)
        {
            if (!TryPiece(tokens[index + 1], out var next, out truncated))
                break;

            builder.Append('.').Append(next);
            index += 2;
        }

        name = builder.ToString();
        return name.Length > 0;
    }

    private static bool TryPiece(Token token, out string piece, out bool truncated)
    {
        piece = "";
        truncated = false;
        var text = token switch
        {
            Word { QuoteStyle: null } word => word.Value,
            Number number => number.Value,
            _ => null,
        };
        if (text is null)
            return false;

        var length = 0;
        while (length < text.Length && IsParamNameChar(text[length]))
            length++;

        if (length == 0)
            return false;

        truncated = length < text.Length;
        piece = text[..length];
        return true;
    }

    private static bool IsParamNameChar(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '.';

    private static string Canonical(string name)
    {
        if (name.Length > 0 && name[0] is '@' or ':')
            return name[1..];

        return name;
    }
}
