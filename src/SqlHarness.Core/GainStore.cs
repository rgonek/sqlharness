using System.Text.Json;

namespace SqlHarness.Core;

internal sealed record GainRecord(
    DateTimeOffset Timestamp, string Command, bool Success, long DurationMilliseconds,
    long RawBytes, long RawLines, long EmittedBytes, long EmittedLines,
    long RawEstimatedTokens, long EmittedEstimatedTokens, long SavedEstimatedTokens);

public sealed record SqlHarnessGainSummary(
    long Executions, long Failures, long DurationMilliseconds,
    long RawBytes, long RawLines, long EmittedBytes, long EmittedLines,
    long RawEstimatedTokens, long EmittedEstimatedTokens, long SavedEstimatedTokens)
{
    public double SavingsPercentage =>
        RawEstimatedTokens == 0 ? 0 : (double)SavedEstimatedTokens / RawEstimatedTokens * 100;
}

public sealed record SqlHarnessGainReport(
    SqlHarnessGainSummary Total,
    SqlHarnessGainSummary Query,
    SqlHarnessGainSummary Compare)
{
    public SqlHarnessGainSummary Measure { get; init; } = Empty;
    private static SqlHarnessGainSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

internal interface IGainStore
{
    void Append(GainRecord record);
    SqlHarnessGainReport Aggregate();
}

internal sealed class GainStore : IGainStore
{
    private static readonly object FileLock = new();
    private readonly string _path;

    public GainStore() : this(SqlHarnessPaths.GainFile) { }
    internal GainStore(string path) => _path = path;

    public void Append(GainRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        lock (FileLock)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString("timestamp", record.Timestamp.ToString("O"));
            writer.WriteString("command", record.Command);
            writer.WriteBoolean("success", record.Success);
            writer.WriteNumber("durationMilliseconds", record.DurationMilliseconds);
            writer.WriteNumber("rawBytes", record.RawBytes);
            writer.WriteNumber("rawLines", record.RawLines);
            writer.WriteNumber("emittedBytes", record.EmittedBytes);
            writer.WriteNumber("emittedLines", record.EmittedLines);
            writer.WriteNumber("rawEstimatedTokens", record.RawEstimatedTokens);
            writer.WriteNumber("emittedEstimatedTokens", record.EmittedEstimatedTokens);
            writer.WriteNumber("savedEstimatedTokens", record.SavedEstimatedTokens);
            writer.WriteEndObject();
            writer.Flush();
            stream.WriteByte((byte)'\n');
        }
    }

    public SqlHarnessGainReport Aggregate()
    {
        lock (FileLock)
        {
            var total = new SummaryAccumulator();
            var query = new SummaryAccumulator();
            var compare = new SummaryAccumulator();
            var measure = new SummaryAccumulator();
            if (!File.Exists(_path))
                return CreateReport(total, query, compare, measure);

            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var record = ReadRecord(line);
                total.Add(record);
                GetAccumulator(record.Command, query, compare, measure)?.Add(record);
            }
            return CreateReport(total, query, compare, measure);
        }
    }

    private static SqlHarnessGainReport CreateReport(
        SummaryAccumulator total, SummaryAccumulator query,
        SummaryAccumulator compare, SummaryAccumulator measure) =>
        new(total.ToSummary(), query.ToSummary(), compare.ToSummary()) { Measure = measure.ToSummary() };

    private static SummaryAccumulator? GetAccumulator(
        string command, SummaryAccumulator query, SummaryAccumulator compare, SummaryAccumulator measure) =>
        command switch
        {
            "query" => query,
            "compare" => compare,
            "measure" => measure,
            "plan" or "schema" => null,
            _ => throw new ArgumentException("Gain command must be query, compare, measure, plan, or schema.", nameof(command)),
        };

    private static GainRecord ReadRecord(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var record = new GainRecord(
            root.GetProperty("timestamp").GetDateTimeOffset(),
            root.GetProperty("command").GetString() ?? string.Empty,
            root.GetProperty("success").GetBoolean(),
            root.GetProperty("durationMilliseconds").GetInt64(),
            root.GetProperty("rawBytes").GetInt64(), root.GetProperty("rawLines").GetInt64(),
            root.GetProperty("emittedBytes").GetInt64(), root.GetProperty("emittedLines").GetInt64(),
            root.GetProperty("rawEstimatedTokens").GetInt64(),
            root.GetProperty("emittedEstimatedTokens").GetInt64(),
            root.GetProperty("savedEstimatedTokens").GetInt64());
        Validate(record);
        return record;
    }

    private static void Validate(GainRecord record)
    {
        if (record.Command is not ("query" or "compare" or "measure" or "plan" or "schema"))
            throw new ArgumentException("Gain command must be query, compare, measure, plan, or schema.", nameof(record));

        ArgumentOutOfRangeException.ThrowIfNegative(record.DurationMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(record.RawBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(record.RawLines);
        ArgumentOutOfRangeException.ThrowIfNegative(record.EmittedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(record.EmittedLines);
        ArgumentOutOfRangeException.ThrowIfNegative(record.RawEstimatedTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(record.EmittedEstimatedTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(record.SavedEstimatedTokens);

        var raw = OutputFootprint.EstimateTokens(record.RawBytes);
        var emitted = OutputFootprint.EstimateTokens(record.EmittedBytes);
        if (record.RawEstimatedTokens != raw || record.EmittedEstimatedTokens != emitted ||
            record.SavedEstimatedTokens != Math.Max(raw - emitted, 0))
            throw new ArgumentException("Gain token counters do not match their byte-count equations.", nameof(record));
    }

    private sealed class SummaryAccumulator
    {
        private long _executions, _failures, _duration, _rawBytes, _rawLines, _emittedBytes,
            _emittedLines, _rawTokens, _emittedTokens, _savedTokens;

        public void Add(GainRecord record)
        {
            _executions++;
            if (!record.Success) _failures++;
            _duration += record.DurationMilliseconds;
            _rawBytes += record.RawBytes; _rawLines += record.RawLines;
            _emittedBytes += record.EmittedBytes; _emittedLines += record.EmittedLines;
            _rawTokens += record.RawEstimatedTokens; _emittedTokens += record.EmittedEstimatedTokens;
            _savedTokens += record.SavedEstimatedTokens;
        }

        public SqlHarnessGainSummary ToSummary() =>
            new(_executions, _failures, _duration, _rawBytes, _rawLines, _emittedBytes,
                _emittedLines, _rawTokens, _emittedTokens, _savedTokens);
    }
}