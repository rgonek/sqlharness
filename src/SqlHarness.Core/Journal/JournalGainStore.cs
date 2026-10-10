using Microsoft.Data.Sqlite;

namespace SqlHarness.Core;

/// <summary>
/// Gain statistics from the activity journal: every completed emission except
/// operation gain (raw and emitted byte counts present). This includes newly
/// completed MCP results; historical raw-only rows remain unavailable.
/// Names in <see cref="CountedOperations"/> also fill that bucket; plan and schema
/// count only toward the total. An unreadable or negative row is skipped.
/// </summary>
internal sealed class JournalGainStore(string databasePath, Func<bool> journalEnabled) : IGainSource
{
    internal static readonly IReadOnlySet<string> CountedOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "query", "compare", "measure", "plan", "schema", "ping", "counts", "space", "watch", "snapshot", "qstop", "indexes",
    };

    public SqlHarnessGainReport Aggregate()
    {
        if (!journalEnabled())
            return Empty() with { JournalEnabled = false };
        if (!File.Exists(databasePath))
            return Empty();

        var buckets = CountedOperations.ToDictionary(name => name, _ => new Accumulator(), StringComparer.Ordinal);
        var total = new Accumulator();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        connection.Open();
        if (!HasFootprintColumns(connection))
            return Empty();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation, status, COALESCE(duration_ms, 0), raw_bytes, raw_lines, emitted_bytes, emitted_lines
            FROM operations
            WHERE raw_bytes IS NOT NULL AND emitted_bytes IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // 027: one unreadable or negative row is skipped. It must not fail Aggregate or be clamped into the totals.
            string operation;
            Row row;
            try
            {
                operation = reader.GetString(0);
                var duration = ReadStoredInt64(reader, 2);
                var rawBytes = ReadStoredInt64(reader, 3);
                var rawLines = reader.IsDBNull(4) ? 0 : ReadStoredInt64(reader, 4);
                var emittedBytes = ReadStoredInt64(reader, 5);
                var emittedLines = reader.IsDBNull(6) ? 0 : ReadStoredInt64(reader, 6);
                if (duration < 0 || rawBytes < 0 || rawLines < 0 || emittedBytes < 0 || emittedLines < 0)
                    continue;
                row = new Row(
                    reader.GetString(1) == "succeeded",
                    duration,
                    rawBytes,
                    rawLines,
                    emittedBytes,
                    emittedLines);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or InvalidOperationException)
            {
                continue;
            }

            if (string.Equals(operation, "gain", StringComparison.Ordinal))
                continue;
            total.Add(row);
            if (buckets.TryGetValue(operation, out var bucket))
                bucket.Add(row);
        }

        return new SqlHarnessGainReport(total.ToSummary(), buckets["query"].ToSummary(), buckets["compare"].ToSummary())
        {
            Measure = buckets["measure"].ToSummary(),
            Ping = buckets["ping"].ToSummary(),
            Counts = buckets["counts"].ToSummary(),
            Space = buckets["space"].ToSummary(),
            Watch = buckets["watch"].ToSummary(),
            Snapshot = buckets["snapshot"].ToSummary(),
            QueryStoreTop = buckets["qstop"].ToSummary(),
            Indexes = buckets["indexes"].ToSummary(),
        };
    }

    /// <summary>
    /// The journal migrates lazily on first write, so a database can still be at schema v1/v2
    /// (or have no operations table) when gain reads it; such a database has nothing to count.
    /// </summary>
    private static bool HasFootprintColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) < 3)
            return false;
        command.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('operations')
            WHERE name IN ('operation', 'status', 'duration_ms', 'raw_bytes', 'raw_lines', 'emitted_bytes', 'emitted_lines');
            """;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 7;
    }

    private static SqlHarnessGainReport Empty() =>
        new(SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty, SqlHarnessGainReport.Empty);

    /// <summary>
    /// <see cref="SqliteDataReader.GetInt64"/> coerces non-numeric text to 0. Only an integer storage class counts.
    /// </summary>
    private static long ReadStoredInt64(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is long value ? value : throw new InvalidCastException();

    private sealed record Row(bool Success, long DurationMilliseconds, long RawBytes, long RawLines, long EmittedBytes, long EmittedLines);

    private sealed class Accumulator
    {
        private long _executions, _failures, _duration, _rawBytes, _rawLines, _emittedBytes, _emittedLines, _rawTokens, _emittedTokens, _savedTokens;

        public void Add(Row row)
        {
            var raw = OutputFootprint.EstimateTokens(row.RawBytes);
            var emitted = OutputFootprint.EstimateTokens(row.EmittedBytes);
            _executions++;
            if (!row.Success)
                _failures++;
            _duration += Math.Max(row.DurationMilliseconds, 0);
            _rawBytes += row.RawBytes;
            _rawLines += row.RawLines;
            _emittedBytes += row.EmittedBytes;
            _emittedLines += row.EmittedLines;
            _rawTokens += raw;
            _emittedTokens += emitted;
            _savedTokens += Math.Max(raw - emitted, 0);
        }

        public SqlHarnessGainSummary ToSummary() => new(
            _executions, _failures, _duration, _rawBytes, _rawLines, _emittedBytes, _emittedLines, _rawTokens, _emittedTokens, _savedTokens);
    }
}