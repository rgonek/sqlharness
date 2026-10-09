using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHarness.Core.Dialect;

/// <summary>
/// Single T-SQL parse entry point for dialect analysis. Classify, parameter-reference
/// validation, and offline validation share this document so they never implement
/// diverging parse rules; each consumer keeps its own allow/deny decision on top.
/// </summary>
internal sealed class SqlServerDocument
{
    private SqlServerDocument(TSqlFragment fragment, IList<ParseError> errors)
    {
        Fragment = fragment;
        Errors = errors;
    }

    internal TSqlFragment Fragment { get; }

    internal IList<ParseError> Errors { get; }

    internal bool HasErrors => Errors.Count > 0;

    // 018: the node-coverage test reads the parser generation from here.
    // Parse still constructs TSql170Parser until Step 6.
    internal static Type ParserType => typeof(TSql170Parser);

    internal static SqlServerDocument Parse(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        return new SqlServerDocument(fragment, errors);
    }
}