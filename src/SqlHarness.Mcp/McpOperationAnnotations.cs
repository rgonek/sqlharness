using System.Buffers;
using System.Globalization;
using System.Text;

using SqlHarness.Core;

namespace SqlHarness.Mcp;

internal static class McpOperationAnnotations
{
    internal static T Apply<T>(T operation, string? label, bool? keep, bool enabled)
        where T : SqlHarnessOperation
    {
        ValidateLabel(label);
        return (T)(operation switch
        {
            SqlHarnessQueryOperation value => (SqlHarnessOperation)(value with { AgentLabel = label, AgentKeep = keep, OperationAnnotationsEnabled = enabled }),
            SqlHarnessMeasureOperation value => (SqlHarnessOperation)(value with { AgentLabel = label, AgentKeep = keep, OperationAnnotationsEnabled = enabled }),
            SqlHarnessCompareOperation value => (SqlHarnessOperation)(value with { AgentLabel = label, AgentKeep = keep, OperationAnnotationsEnabled = enabled }),
            SqlHarnessCompareMatrixOperation value => (SqlHarnessOperation)(value with { AgentLabel = label, AgentKeep = keep, OperationAnnotationsEnabled = enabled }),
            SqlHarnessWatchOperation value => (SqlHarnessOperation)(value with { AgentLabel = label, AgentKeep = keep, OperationAnnotationsEnabled = enabled }),
            SqlHarnessSnapshotOperation value => (SqlHarnessOperation)(value with { AgentLabel = label, AgentKeep = keep, OperationAnnotationsEnabled = enabled }),
            _ => operation,
        });
    }

    internal static void ValidateLabel(string? label)
    {
        if (label is null)
            return;

        var count = 0;
        for (var index = 0; index < label.Length;)
        {
            var status = Rune.DecodeFromUtf16(label.AsSpan(index), out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new McpMappingException("Operation label must contain valid Unicode text.");
            index += consumed;
            count++;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                throw new McpMappingException("Operation label must not contain control characters or newlines.");
        }
        if (count > 120)
            throw new McpMappingException("Operation label must contain at most 120 Unicode code points.");
    }
}