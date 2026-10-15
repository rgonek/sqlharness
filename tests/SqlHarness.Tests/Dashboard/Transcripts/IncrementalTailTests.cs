using System.Text;
using SqlHarness.Dashboard.Transcripts;

namespace SqlHarness.Tests.Dashboard.Transcripts;

public sealed class IncrementalTailTests
{
    [Fact]
    public void Reads_only_complete_lines_and_resumes_from_the_cursor()
    {
        using var dir = new TempHome();
        var path = Path.Combine(dir.Path, "t.jsonl");
        File.WriteAllText(path, "a\nb\npart");
        var lines = new List<string>();

        var first = IncrementalTail.Read(path, null, 1024, l => lines.Add(Encoding.UTF8.GetString(l)));
        File.AppendAllText(path, "ial\nc\n");
        var second = IncrementalTail.Read(path, first.Cursor, 1024, l => lines.Add(Encoding.UTF8.GetString(l)));

        Assert.Equal(["a", "b", "partial", "c"], lines);
        Assert.Equal(4, first.Cursor.Offset);
        Assert.True(second.Completed);
    }

    [Fact]
    public void Unchanged_file_is_not_read_and_a_shrunk_file_restarts()
    {
        using var dir = new TempHome();
        var path = Path.Combine(dir.Path, "t.jsonl");
        File.WriteAllText(path, "a\nb\n");
        var first = IncrementalTail.Read(path, null, 1024, _ => { });
        Assert.Equal(0, IncrementalTail.Read(path, first.Cursor, 1024, _ => { }).BytesRead);

        File.WriteAllText(path, "z\n");
        var lines = new List<string>();
        IncrementalTail.Read(path, first.Cursor, 1024, l => lines.Add(Encoding.UTF8.GetString(l)));
        Assert.Equal(["z"], lines);
    }

    [Fact]
    public void Budget_stops_at_a_line_boundary_and_reports_incomplete()
    {
        using var dir = new TempHome();
        var path = Path.Combine(dir.Path, "t.jsonl");
        File.WriteAllText(path, "aaaa\nbbbb\ncccc\n");
        var lines = new List<string>();

        var read = IncrementalTail.Read(path, null, 6, l => lines.Add(Encoding.UTF8.GetString(l)));

        Assert.False(read.Completed);
        Assert.Equal(["aaaa"], lines);
        Assert.Equal(5, read.Cursor.Offset);
    }

    [Fact]
    public void Unchanged_file_resumes_when_previous_cycle_left_budget_remainder()
    {
        using var dir = new TempHome();
        var path = Path.Combine(dir.Path, "t.jsonl");
        File.WriteAllText(path, "aa\nbb\ncc\n");
        var lines = new List<string>();

        var first = IncrementalTail.Read(path, null, 4, l => lines.Add(Encoding.UTF8.GetString(l)));
        var second = IncrementalTail.Read(path, first.Cursor, 4, l => lines.Add(Encoding.UTF8.GetString(l)));
        var third = IncrementalTail.Read(path, second.Cursor, 4, l => lines.Add(Encoding.UTF8.GetString(l)));

        Assert.Equal(["aa", "bb", "cc"], lines);
        Assert.Equal(4, first.BytesRead);
        Assert.Equal(4, second.BytesRead);
        Assert.Equal(3, third.BytesRead);
        Assert.True(third.Completed);
    }

    [Fact]
    public void Shrink_restarts_even_when_cursor_offset_remains_in_range()
    {
        using var dir = new TempHome();
        var path = Path.Combine(dir.Path, "t.jsonl");
        File.WriteAllText(path, "a\nb\nc\n");
        var first = IncrementalTail.Read(path, null, 4, _ => { });
        Assert.Equal(4, first.Cursor.Offset);

        File.WriteAllText(path, "z\n");
        var lines = new List<string>();
        var restarted = IncrementalTail.Read(path, first.Cursor, 1024, l => lines.Add(Encoding.UTF8.GetString(l)));

        Assert.Equal(["z"], lines);
        Assert.Equal(2, restarted.Cursor.Offset);
    }

    [Fact]
    public void A_file_open_for_writing_by_another_process_can_be_read()
    {
        using var dir = new TempHome();
        var path = Path.Combine(dir.Path, "t.jsonl");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("x\n"u8);
        writer.Flush();
        var lines = new List<string>();
        IncrementalTail.Read(path, null, 1024, l => lines.Add(Encoding.UTF8.GetString(l)));
        Assert.Equal(["x"], lines);
    }
}
