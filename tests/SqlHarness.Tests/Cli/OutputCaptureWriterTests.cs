using System.Collections;
using System.Reflection;
using System.Text;

using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Tests.Cli;

public sealed class OutputCaptureWriterTests
{
    [Fact]
    public void Unicode_and_ansi_split_across_writes_produce_exact_footprint()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);
        var mark = writer.Mark();

        writer.Write("a");
        writer.Write("\x1b[31m");
        writer.Write("zażółć");
        writer.Write("\x1b[0m");
        writer.Write("😀");
        writer.Write("\n");
        writer.Write("tail");

        var footprint = writer.GetAnsiFreeFootprint(mark);

        Assert.Equal(Encoding.UTF8.GetByteCount("azażółć😀\ntail"), footprint.Bytes);
        Assert.Equal(2, footprint.Lines);
        Assert.Equal("a\x1b[31mzażółć\x1b[0m😀\ntail", sink.ToString());
    }

    [Fact]
    public void Escape_sequences_split_across_writes_are_stripped()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);
        var mark = writer.Mark();

        writer.Write("\x1b[3");
        writer.Write("1m");
        writer.Write("RED");
        writer.Write("\x1b[0");
        writer.Write("m");

        var footprint = writer.GetAnsiFreeFootprint(mark);

        Assert.Equal(new OutputFootprint(3, 1), footprint);
    }

    [Fact]
    public void Osc_sequence_split_across_writes_is_stripped()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);
        var mark = writer.Mark();

        writer.Write("go\x1b]8;;https://exam");
        writer.Write("ple.example\x07now");

        var footprint = writer.GetAnsiFreeFootprint(mark);

        Assert.Equal(Encoding.UTF8.GetByteCount("gonow"), footprint.Bytes);
        Assert.Equal(1, footprint.Lines);
    }

    [Fact]
    public void Surrogate_pair_split_across_string_writes_counts_once()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);
        var mark = writer.Mark();

        writer.Write("\ud83d");
        writer.Write("\ude00AB");

        var footprint = writer.GetAnsiFreeFootprint(mark);

        Assert.Equal(6, footprint.Bytes);
        Assert.Equal(1, footprint.Lines);
    }

    [Fact]
    public void Incomplete_tail_counts_as_visible_once_observed()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);
        var mark = writer.Mark();

        // A lone ESC never completes: it must still be counted exactly once.
        writer.Write("ab\x1b");

        var footprint = writer.GetAnsiFreeFootprint(mark);

        Assert.Equal(Encoding.UTF8.GetByteCount("ab\x1b"), footprint.Bytes);
        Assert.Equal(1, footprint.Lines);
    }

    [Fact]
    public void Multiple_sequential_marks_track_independent_differentials()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);

        var first = writer.Mark();
        writer.Write("ab");
        var second = writer.Mark();
        writer.Write("c\n");
        var third = writer.Mark();
        writer.Write("d");

        var thirdFootprint = writer.GetAnsiFreeFootprint(third);
        var secondFootprint = writer.GetAnsiFreeFootprint(second);
        var firstFootprint = writer.GetAnsiFreeFootprint(first);

        Assert.Equal(new OutputFootprint(1, 1), thirdFootprint);
        Assert.Equal(new OutputFootprint(Encoding.UTF8.GetByteCount("c\nd"), 2), secondFootprint);
        Assert.Equal(new OutputFootprint(Encoding.UTF8.GetByteCount("abc\nd"), 2), firstFootprint);
        Assert.Equal("abc\nd", sink.ToString());
    }

    [Fact]
    public void Unknown_or_reused_mark_throws()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetAnsiFreeFootprint(42));

        var mark = writer.Mark();
        writer.Write("x");
        writer.GetAnsiFreeFootprint(mark);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.GetAnsiFreeFootprint(mark));
    }

    [Fact]
    public void Writer_retains_no_copy_of_printed_text()
    {
        using var sink = new StringWriter();
        var writer = new OutputCaptureWriter(sink);

        // About a megabyte across many writes, drained mark by mark.
        var chunk = new string('x', 4096);
        for (var index = 0; index < 256; index++)
        {
            var mark = writer.Mark();
            writer.Write(chunk);
            if (index % 2 == 0)
                writer.Write("\x1b[32m✓\x1b[0m");
            writer.GetAnsiFreeFootprint(mark);
        }
        // An over-long unterminated ESC tail is counted, not retained.
        var tailMark = writer.Mark();
        writer.Write("\x1b[" + new string('1', 5000));
        var tailFootprint = writer.GetAnsiFreeFootprint(tailMark);
        Assert.Equal(5002, tailFootprint.Bytes);

        foreach (var field in typeof(OutputCaptureWriter).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.NotEqual(typeof(StringBuilder), field.FieldType);
            if (field.FieldType == typeof(string) && field.GetValue(writer) is string text)
                Assert.True(text.Length <= 4096, $"Field {field.Name} retains {text.Length} characters.");
            if (field.GetValue(writer) is IDictionary retained)
                Assert.Empty(retained);
        }
    }
}
