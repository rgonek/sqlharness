using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class StatementSpanPairerTests
{
    [Fact]
    public void Exact_statement_pairs_to_executed_text_span()
    {
        const string sql = "SELECT 1; SELECT 2;";
        var pair = Assert.Single(StatementSpanPairer.Pair(sql, Plan("SELECT 1", 7, 3)));

        Assert.Equal("exact", pair.MatchKind);
        Assert.Equal("SELECT 1;", sql.Substring(pair.SpanStart!.Value, pair.SpanLength!.Value));
        Assert.Equal(7, pair.StatementId);
        Assert.Equal(3, pair.StatementCompId);
    }

    [Fact]
    public void Psp_suffix_is_stripped()
    {
        const string sql = "SELECT * FROM dbo.T WHERE Id = @id;";
        var pair = Assert.Single(StatementSpanPairer.Pair(sql,
            Plan("SELECT * FROM dbo.T WHERE Id = @id option (PLAN PER VALUE(ObjectID = 1, QueryVariantID = 2, predicate_range([dbo].[T].[Id] = @id)))", 1, 1, dispatcher: true)));

        Assert.Equal("psp_suffix", pair.MatchKind);
        Assert.Equal(0, pair.SpanStart);
        Assert.Equal(sql.Length, pair.SpanLength);
    }

    [Fact]
    public void Normalized_whitespace_matches_without_changing_literals()
    {
        const string sql = "SELECT 'a  b' AS [v]\r\nFROM dbo.T;";
        var pair = Assert.Single(StatementSpanPairer.Pair(sql, Plan("SELECT   'a  b' AS [v] FROM   dbo.T", 1, 1)));

        Assert.Equal("normalized", pair.MatchKind);
        Assert.Equal(sql, sql.Substring(pair.SpanStart!.Value, pair.SpanLength!.Value));
    }

    [Fact]
    public void Truncated_statement_text_pairs_by_prefix()
    {
        var sql = "SELECT N'" + new string('a', 4100) + "';";
        var pair = Assert.Single(StatementSpanPairer.Pair(sql, Plan(sql[..3999], 1, 1)));

        Assert.Equal("prefix", pair.MatchKind);
        Assert.Equal(sql.Length, pair.SpanLength);
    }

    [Fact]
    public void Two_statements_sharing_a_3999_character_prefix_are_ambiguous()
    {
        var common = "SELECT N'" + new string('a', 3990);
        var sql = common + "x';\n" + common + "y';";
        var pair = Assert.Single(StatementSpanPairer.Pair(sql, Plan(common, 1, 1)));

        Assert.Equal("ambiguous", pair.MatchKind);
        Assert.Null(pair.SpanStart);
        Assert.Null(pair.SpanLength);
    }

    [Fact]
    public void Planless_statements_do_not_shift_pairing()
    {
        const string sql = "DECLARE @x int; CREATE TABLE #t(i int); SELECT 1; SELECT 2;";
        var pairs = StatementSpanPairer.Pair(sql, Plans(("SELECT 1", 1, 1), ("SELECT 2", 2, 2)));

        Assert.Equal(["exact", "exact"], pairs.Select(pair => pair.MatchKind));
        Assert.Equal("SELECT 1;", sql.Substring(pairs[0].SpanStart!.Value, pairs[0].SpanLength!.Value));
        Assert.Equal("SELECT 2;", sql.Substring(pairs[1].SpanStart!.Value, pairs[1].SpanLength!.Value));
    }

    [Fact]
    public void Unparsable_batch_leaves_all_documents_unmatched()
    {
        var pairs = StatementSpanPairer.Pair("SELECT FROM ;", Plans(("SELECT 1", 1, 1), ("SELECT 2", 2, 2)));

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, pair => Assert.Equal("unmatched", pair.MatchKind));
    }

    private static string Plan(string text, int id, int compId, bool dispatcher = false) =>
        Plans((text, id, compId), dispatcher: dispatcher);

    private static string Plans((string Text, int Id, int CompId) first, (string Text, int Id, int CompId)? second = null, bool dispatcher = false)
    {
        var statements = string.Join(string.Empty, new[] { first }.Concat(second is null ? [] : [second.Value]).Select(item =>
            $"<StmtSimple StatementText=\"{System.Security.SecurityElement.Escape(item.Text)}\" StatementId=\"{item.Id}\" StatementCompId=\"{item.CompId}\"><QueryPlan /></StmtSimple>"));
        return $"<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>{statements}</Statements></Batch>{(dispatcher ? "<Dispatcher />" : "")}</BatchSequence></ShowPlanXML>";
    }

    private static string Plans(params (string Text, int Id, int CompId)[] statements)
    {
        var body = string.Join(string.Empty, statements.Select(item =>
            $"<StmtSimple StatementText=\"{System.Security.SecurityElement.Escape(item.Text)}\" StatementId=\"{item.Id}\" StatementCompId=\"{item.CompId}\"><QueryPlan /></StmtSimple>"));
        return $"<ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\"><BatchSequence><Batch><Statements>{body}</Statements></Batch></BatchSequence></ShowPlanXML>";
    }
}
