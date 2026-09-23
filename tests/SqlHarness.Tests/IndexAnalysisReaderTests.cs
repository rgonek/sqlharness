using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class IndexAnalysisReaderTests
{
    private const string MalformedMessage = "Index analysis result is malformed.";
    private const string SafetyMessage = "indexes object was not found or was ambiguous.";
    private const string ParserMessage = "Missing-index column metadata is malformed.";
    private const string PlantedObject = "PlantedObjectName";

    [Fact]
    public async Task Reader_builds_normalized_candidates_and_sensitive_indexes()
    {
        var collected = await IndexAnalysisQuery.ReadAsync(
            Fixture.Reader(filter: "[Status]=(1)"), objectRequested: true,
            CancellationToken.None);

        Assert.Equal(["TenantId"], Assert.Single(collected.Candidates).EqualityColumns);
        var index = Assert.Single(collected.ExistingIndexes);
        Assert.True(index.HasFilter);
        Assert.NotNull(index.FilterHash);
        Assert.DoesNotContain("[Status]", JsonSerializer.Serialize(index),
            StringComparison.Ordinal);
        Assert.Equal("[Status]=(1)",
            Assert.Single(collected.SensitiveIndexes).FilterDefinition);
    }

    [Fact]
    public async Task Reader_reads_metrics_timestamps_catalog_spelling_and_footprint()
    {
        var collected = await Read(Fixture.Reader());

        Assert.Equal(new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero), collected.ObservationSince);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero), collected.ObservedAt);
        Assert.True(collected.RawFootprint.Bytes > 0);

        var candidate = Assert.Single(collected.Candidates);
        Assert.Equal(11L, candidate.CandidateId);
        Assert.Equal("dbo", candidate.Schema);
        Assert.Equal("Contracts", candidate.Table);
        Assert.Equal(["TenantId"], candidate.EqualityColumns);
        Assert.Equal(["CreatedAt"], candidate.InequalityColumns);
        Assert.Equal(["Name", "Status"], candidate.IncludeColumns);
        Assert.Equal(3L, candidate.UserSeeks);
        Assert.Equal(4L, candidate.UserScans);
        Assert.Equal(2.5m, candidate.AverageTotalUserCost);
        Assert.Equal(50m, candidate.AverageUserImpactPercent);
        Assert.Equal(99.25m, candidate.CumulativeImpactScore);
        Assert.NotEqual(8.75m, candidate.CumulativeImpactScore);
        Assert.Equal(new DateTimeOffset(Fixture.LastSeekLocal).ToUniversalTime(), candidate.LastUserSeek);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 23, 30, 0, TimeSpan.Zero), candidate.LastUserScan);

        var index = Assert.Single(collected.ExistingIndexes);
        Assert.Equal("dbo", index.Schema);
        Assert.Equal("Contracts", index.Table);
        Assert.Equal(2, index.IndexId);
        Assert.Equal("IX_Contracts_Tenant", index.Name);
        Assert.Equal("NONCLUSTERED", index.Type);
        Assert.Equal(["TenantId"], index.KeyColumns);
        Assert.Equal([false], index.KeyDescending);
        Assert.Empty(index.IncludeColumns);
        Assert.False(index.Unique);
        Assert.False(index.PrimaryKey);
        Assert.False(index.UniqueConstraint);
        Assert.False(index.Disabled);
        Assert.Equal("PAGE", index.Compression);
        var sensitive = Assert.Single(collected.SensitiveIndexes);
        Assert.Equal(index.Schema, sensitive.Schema);
        Assert.Equal(index.Table, sensitive.Table);
        Assert.Equal(index.IndexId, sensitive.IndexId);
        Assert.Equal(index.Name, sensitive.Name);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2L)]
    public async Task Reader_missing_or_ambiguous_object_is_safety(long matchCount)
    {
        var ex = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() => Read(
            Fixture.Scenario(
                matchCount: matchCount,
                candidateSet: Fixture.CandidateSet(Fixture.Candidate(table: Fixture.Cell.Of(PlantedObject))),
                catalogSet: Fixture.CatalogSet(
                    Fixture.Catalog("dbo", PlantedObject, "TenantId"),
                    Fixture.Catalog("dbo", PlantedObject, "CreatedAt"),
                    Fixture.Catalog("dbo", PlantedObject, "Name"),
                    Fixture.Catalog("dbo", PlantedObject, "Status"))),
            objectRequested: true));

        Assert.Equal(SafetyMessage, ex.Message);
        Assert.DoesNotContain(PlantedObject, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(7L)]
    public async Task Reader_database_wide_mode_accepts_any_non_negative_match_count(long matchCount)
    {
        var collected = await Read(Fixture.Reader(objectMatchCount: matchCount), objectRequested: false);

        Assert.Equal(["TenantId"], Assert.Single(collected.Candidates).EqualityColumns);
        Assert.True(collected.RawFootprint.Bytes > 0);
    }

    [Fact]
    public async Task Reader_empty_candidates_are_success()
    {
        var collected = await Read(Fixture.Scenario(
            candidateSet: Fixture.CandidateSet(),
            catalogSet: Fixture.CatalogSet(),
            indexSet: Fixture.IndexSet(),
            indexColumnSet: Fixture.IndexColumnSet(),
            compressionSet: Fixture.CompressionSet()));

        Assert.Empty(collected.Candidates);
        Assert.Empty(collected.ExistingIndexes);
        Assert.Empty(collected.SensitiveIndexes);
        Assert.Equal(new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero), collected.ObservationSince);
        Assert.True(collected.RawFootprint.Bytes > 0);
    }

    [Fact]
    public async Task Reader_blank_column_lists_are_empty_even_when_the_catalog_is_ambiguous()
    {
        var collected = await Read(Fixture.Scenario(
            candidateSet: Fixture.CandidateSet(Fixture.Candidate(
                equality: Fixture.Cell.Of(null),
                inequality: Fixture.Cell.Of(""),
                included: Fixture.Cell.Of(" \t\r\n "))),
            catalogSet: Fixture.CatalogSet(
                Fixture.Catalog("dbo", "Contracts", "Status"),
                Fixture.Catalog("dbo", "Contracts", "STATUS"))));

        var candidate = Assert.Single(collected.Candidates);
        Assert.Empty(candidate.EqualityColumns);
        Assert.Empty(candidate.InequalityColumns);
        Assert.Empty(candidate.IncludeColumns);
    }

    [Fact]
    public async Task Reader_parses_commas_and_escaped_brackets_inside_names()
    {
        var collected = await Read(Fixture.Scenario(
            candidateSet: Fixture.CandidateSet(Fixture.Candidate(
                equality: Fixture.Cell.Of("[A,B], [C]]D]"),
                inequality: Fixture.Cell.Of(null),
                included: Fixture.Cell.Of(null))),
            catalogSet: Fixture.CatalogSet(
                Fixture.Catalog("dbo", "Contracts", "C]D"),
                Fixture.Catalog("dbo", "Contracts", "A,B"))));

        Assert.Equal(["A,B", "C]D"], Assert.Single(collected.Candidates).EqualityColumns);
    }

    [Theory]
    [InlineData("[NotInCatalog]")]
    [InlineData("[TenantId")]
    [InlineData("[TenantId],")]
    [InlineData("[tenantid], [TenantId]")]
    public async Task Reader_bad_column_list_propagates_the_parser_message(string list)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(Fixture.Scenario(
            candidateSet: Fixture.CandidateSet(Fixture.Candidate(
                equality: Fixture.Cell.Of(list),
                inequality: Fixture.Cell.Of(null),
                included: Fixture.Cell.Of(null))),
            catalogSet: Fixture.CatalogSet(
                Fixture.Catalog("sales", "Contracts", "NotInCatalog"),
                Fixture.Catalog("dbo", "Contracts", "TenantId")))));

        Assert.Equal(ParserMessage, ex.Message);
        Assert.DoesNotContain(list, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_keeps_repeated_candidate_ids_and_the_reported_score()
    {
        var collected = await Read(Fixture.Scenario(
            candidateSet: Fixture.CandidateSet(
                Fixture.Candidate(
                    equality: Fixture.Cell.Of("[tenantid]"),
                    inequality: Fixture.Cell.Of(null),
                    included: Fixture.Cell.Of(null),
                    userSeeks: Fixture.Cell.Of(1L),
                    userScans: Fixture.Cell.Of(1L),
                    averageCost: Fixture.Cell.Of(10m),
                    averageImpact: Fixture.Cell.Of(50m),
                    score: Fixture.Cell.Of(123.45m)),
                Fixture.Candidate(
                    equality: Fixture.Cell.Of("[name]"),
                    inequality: Fixture.Cell.Of(null),
                    included: Fixture.Cell.Of(null),
                    userSeeks: Fixture.Cell.Of(9L),
                    userScans: Fixture.Cell.Of(9L),
                    averageCost: Fixture.Cell.Of(9m),
                    averageImpact: Fixture.Cell.Of(9m),
                    score: Fixture.Cell.Of(7m))),
            catalogSet: Fixture.CatalogSet(
                Fixture.Catalog("dbo", "Contracts", "TenantId"),
                Fixture.Catalog("dbo", "Contracts", "Name"))));

        Assert.Equal(2, collected.Candidates.Count);
        Assert.All(collected.Candidates, candidate => Assert.Equal(11L, candidate.CandidateId));
        Assert.Equal(["TenantId"], collected.Candidates[0].EqualityColumns);
        Assert.Equal(["Name"], collected.Candidates[1].EqualityColumns);
        Assert.Equal(123.45m, collected.Candidates[0].CumulativeImpactScore);
        Assert.Equal(7m, collected.Candidates[1].CumulativeImpactScore);
        Assert.Equal(1L, collected.Candidates[0].UserSeeks);
        Assert.Equal(9L, collected.Candidates[1].UserSeeks);
    }

    [Fact]
    public async Task Reader_orders_keys_and_includes_without_merging_indexes()
    {
        var collected = await Read(Fixture.Scenario(
            indexSet: Fixture.IndexSet(
                Fixture.Index(indexId: Fixture.Cell.Of(5), name: Fixture.Cell.Of("IX_Contracts"), filter: Fixture.Cell.Of(null)),
                Fixture.Index(
                    schema: Fixture.Cell.Of("sales"),
                    table: Fixture.Cell.Of("Orders"),
                    indexId: Fixture.Cell.Of(5),
                    name: Fixture.Cell.Of("IX_Orders"),
                    filter: Fixture.Cell.Of(null))),
            indexColumnSet: Fixture.IndexColumnSet(
                Fixture.Column("C", keyOrdinal: 0, included: false, descending: false, indexColumnId: 9, indexId: 5),
                Fixture.Column("A,B", keyOrdinal: 2, included: false, descending: true, indexColumnId: 4, indexId: 5),
                Fixture.Column("Inc", keyOrdinal: 1, included: true, descending: true, indexColumnId: 2, indexId: 5),
                Fixture.Column("D", keyOrdinal: 7, included: true, descending: false, indexColumnId: 1, indexId: 5, schema: "DBO", table: "contracts"),
                Fixture.Column("OnlyOrders", keyOrdinal: 1, included: false, descending: false, indexColumnId: 1, indexId: 5, schema: "sales", table: "Orders")),
            compressionSet: Fixture.CompressionSet()));

        Assert.Equal(2, collected.ExistingIndexes.Count);
        var contracts = collected.ExistingIndexes[0];
        Assert.Equal("IX_Contracts", contracts.Name);
        Assert.Equal(["C", "A,B"], contracts.KeyColumns);
        Assert.Equal([false, true], contracts.KeyDescending);
        Assert.Equal(["D", "Inc"], contracts.IncludeColumns);
        Assert.DoesNotContain("Inc", contracts.KeyColumns);
        Assert.DoesNotContain("D", contracts.KeyColumns);
        Assert.DoesNotContain("OnlyOrders", contracts.KeyColumns);
        Assert.Equal("", contracts.Compression);

        var orders = collected.ExistingIndexes[1];
        Assert.Equal("sales", orders.Schema);
        Assert.Equal("Orders", orders.Table);
        Assert.Equal(["OnlyOrders"], orders.KeyColumns);
        Assert.Equal([false], orders.KeyDescending);
        Assert.Empty(orders.IncludeColumns);
        Assert.Equal(2, collected.SensitiveIndexes.Count);
    }

    [Fact]
    public async Task Reader_projects_flags_heap_name_and_unfiltered_sensitive_rows()
    {
        var collected = await Read(Fixture.Scenario(
            indexSet: Fixture.IndexSet(
                Fixture.Index(
                    indexId: Fixture.Cell.Of(4),
                    name: Fixture.Cell.Of("UQ_Contracts"),
                    unique: Fixture.Cell.Of(1),
                    primaryKey: Fixture.Cell.Of(1),
                    uniqueConstraint: Fixture.Cell.Of(1),
                    disabled: Fixture.Cell.Of(1),
                    filter: Fixture.Cell.Of(null)),
                Fixture.Index(
                    indexId: Fixture.Cell.Of(0),
                    name: Fixture.Cell.Of(null),
                    type: Fixture.Cell.Of("HEAP"),
                    unique: Fixture.Cell.Of(0),
                    primaryKey: Fixture.Cell.Of(0),
                    uniqueConstraint: Fixture.Cell.Of(0),
                    disabled: Fixture.Cell.Of(0),
                    filter: Fixture.Cell.Of(null))),
            indexColumnSet: Fixture.IndexColumnSet(
                Fixture.Column("TenantId", keyOrdinal: 1, included: 0, descending: 0, indexColumnId: 1, indexId: 4)),
            compressionSet: Fixture.CompressionSet(
                Fixture.Compression(indexId: Fixture.Cell.Of(4), description: Fixture.Cell.Of("ROW")),
                Fixture.Compression(indexId: Fixture.Cell.Of(0), description: Fixture.Cell.Of("NONE")))));

        var unique = collected.ExistingIndexes[0];
        Assert.True(unique.Unique);
        Assert.True(unique.PrimaryKey);
        Assert.True(unique.UniqueConstraint);
        Assert.True(unique.Disabled);
        Assert.False(unique.HasFilter);
        Assert.Null(unique.FilterHash);
        Assert.Equal("ROW", unique.Compression);
        Assert.Equal(["TenantId"], unique.KeyColumns);

        var heap = collected.ExistingIndexes[1];
        Assert.Equal(0, heap.IndexId);
        Assert.Equal("", heap.Name);
        Assert.Equal("HEAP", heap.Type);
        Assert.Empty(heap.KeyColumns);
        Assert.Empty(heap.IncludeColumns);
        Assert.False(heap.Unique);
        Assert.False(heap.PrimaryKey);
        Assert.False(heap.UniqueConstraint);
        Assert.False(heap.Disabled);
        Assert.False(heap.HasFilter);
        Assert.Null(heap.FilterHash);
        Assert.Equal("NONE", heap.Compression);

        Assert.Equal(2, collected.SensitiveIndexes.Count);
        Assert.All(collected.SensitiveIndexes, sensitive => Assert.Null(sensitive.FilterDefinition));
        Assert.Equal("UQ_Contracts", collected.SensitiveIndexes[0].Name);
        Assert.Equal("", collected.SensitiveIndexes[1].Name);
        Assert.Equal(0, collected.SensitiveIndexes[1].IndexId);
    }

    [Theory]
    [InlineData("[Status]=(1)")]
    [InlineData("Łódź")]
    [InlineData("")]
    public async Task Reader_hashes_exact_filter_text_as_uppercase_sha256(string filter)
    {
        var collected = await Read(Fixture.Reader(filter: filter));

        var index = Assert.Single(collected.ExistingIndexes);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filter)));
        Assert.True(index.HasFilter);
        Assert.Equal(expected, index.FilterHash);
        Assert.Equal(64, index.FilterHash!.Length);
        Assert.Matches("^[0-9A-F]{64}$", index.FilterHash);
        var json = JsonSerializer.Serialize(index);
        Assert.DoesNotContain("FilterDefinition", json, StringComparison.Ordinal);
        if (filter.Length > 0)
        {
            Assert.DoesNotContain(filter, json, StringComparison.Ordinal);
            Assert.DoesNotContain(filter, JsonSerializer.Serialize(collected.ExistingIndexes), StringComparison.Ordinal);
            foreach (var property in index.GetType().GetProperties())
            {
                if (property.GetValue(index) is string text)
                    Assert.DoesNotContain(filter, text, StringComparison.Ordinal);
            }
        }

        Assert.Equal(filter, Assert.Single(collected.SensitiveIndexes).FilterDefinition);
    }

    [Fact]
    public async Task Reader_collapses_compression_per_index()
    {
        var collected = await Read(Fixture.Scenario(
            indexSet: Fixture.IndexSet(
                Fixture.Index(indexId: Fixture.Cell.Of(3), name: Fixture.Cell.Of("IX_None"), filter: Fixture.Cell.Of(null)),
                Fixture.Index(indexId: Fixture.Cell.Of(1), name: Fixture.Cell.Of("IX_Page"), filter: Fixture.Cell.Of(null)),
                Fixture.Index(indexId: Fixture.Cell.Of(2), name: Fixture.Cell.Of("IX_Mixed"), filter: Fixture.Cell.Of(null))),
            indexColumnSet: Fixture.IndexColumnSet(),
            compressionSet: Fixture.CompressionSet(
                Fixture.Compression(schema: Fixture.Cell.Of("DBO"), table: Fixture.Cell.Of("contracts"), indexId: Fixture.Cell.Of(1), description: Fixture.Cell.Of("page")),
                Fixture.Compression(indexId: Fixture.Cell.Of(1), description: Fixture.Cell.Of("PAGE")),
                Fixture.Compression(indexId: Fixture.Cell.Of(2), description: Fixture.Cell.Of("ROW")),
                Fixture.Compression(indexId: Fixture.Cell.Of(2), description: Fixture.Cell.Of("PAGE")))));

        Assert.Equal(["IX_None", "IX_Page", "IX_Mixed"], collected.ExistingIndexes.Select(index => index.Name));
        Assert.Equal("", collected.ExistingIndexes[0].Compression);
        Assert.Equal("page", collected.ExistingIndexes[1].Compression);
        Assert.Equal("MIXED", collected.ExistingIndexes[2].Compression);
        Assert.Equal(3, collected.SensitiveIndexes.Count);
    }

    [Fact]
    public async Task Reader_raw_footprint_grows_when_a_filter_cell_is_read()
    {
        var filtered = await Read(Fixture.Reader(filter: "[Status]=(1)"));
        var unfiltered = await Read(Fixture.Reader(filter: null));

        Assert.True(unfiltered.RawFootprint.Bytes > 0);
        Assert.True(filtered.RawFootprint.Bytes > unfiltered.RawFootprint.Bytes);
        Assert.False(Assert.Single(unfiltered.ExistingIndexes).HasFilter);
        Assert.Null(Assert.Single(unfiltered.SensitiveIndexes).FilterDefinition);
    }

    [Fact]
    public async Task Reader_null_seek_and_scan_stay_null()
    {
        var collected = await Read(Fixture.Scenario(candidateSet: Fixture.CandidateSet(
            Fixture.Candidate(lastSeek: Fixture.Cell.Of(null), lastScan: Fixture.Cell.Of(null)))));

        var candidate = Assert.Single(collected.Candidates);
        Assert.Null(candidate.LastUserSeek);
        Assert.Null(candidate.LastUserScan);
    }

    [Fact]
    public async Task Reader_parses_numbers_with_the_invariant_culture()
    {
        using var _ = new TemporaryCulture("pl-PL");
        var collected = await Read(Fixture.Scenario(candidateSet: Fixture.CandidateSet(
            Fixture.Candidate(
                userSeeks: Fixture.Cell.Of("3"),
                userScans: Fixture.Cell.Of("4"),
                averageCost: Fixture.Cell.Of("1.25"),
                averageImpact: Fixture.Cell.Of("50.5"),
                score: Fixture.Cell.Of("99.25")))));

        var candidate = Assert.Single(collected.Candidates);
        Assert.Equal(3L, candidate.UserSeeks);
        Assert.Equal(4L, candidate.UserScans);
        Assert.Equal(1.25m, candidate.AverageTotalUserCost);
        Assert.Equal(50.5m, candidate.AverageUserImpactPercent);
        Assert.Equal(99.25m, candidate.CumulativeImpactScore);
    }

    [Theory]
    [InlineData("match")]
    [InlineData("seeks")]
    [InlineData("score")]
    [InlineData("unique")]
    [InlineData("ordinal")]
    [InlineData("null-seek")]
    public async Task Reader_invalid_number_is_malformed(string kind)
    {
        const string bad = "not-a-number";
        var reader = kind switch
        {
            "match" => Fixture.Scenario(observationSet: Fixture.ObservationSet(
                Fixture.Observation(Fixture.ObservationSince, Fixture.ObservedAt, bad))),
            "seeks" => Fixture.Scenario(candidateSet: Fixture.CandidateSet(
                Fixture.Candidate(userSeeks: Fixture.Cell.Of(bad)))),
            "score" => Fixture.Scenario(candidateSet: Fixture.CandidateSet(
                Fixture.Candidate(score: Fixture.Cell.Of(bad)))),
            "unique" => Fixture.Scenario(indexSet: Fixture.IndexSet(
                Fixture.Index(unique: Fixture.Cell.Of(bad), filter: Fixture.Cell.Of(null)))),
            "ordinal" => Fixture.Scenario(indexColumnSet: Fixture.IndexColumnSet(
                Fixture.Column(keyOrdinal: bad))),
            "null-seek" => Fixture.Scenario(candidateSet: Fixture.CandidateSet(
                Fixture.Candidate(userSeeks: Fixture.Cell.Of(null)))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(reader));
        Assert.Equal(MalformedMessage, ex.Message);
        Assert.DoesNotContain(bad, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("observed")]
    [InlineData("since-null")]
    [InlineData("seek")]
    [InlineData("scan")]
    public async Task Reader_invalid_timestamp_is_malformed(string kind)
    {
        const string bad = "not-a-timestamp";
        var reader = kind switch
        {
            "observed" => Fixture.Scenario(observationSet: Fixture.ObservationSet(
                Fixture.Observation(Fixture.ObservationSince, bad, 0L))),
            "since-null" => Fixture.Scenario(observationSet: Fixture.ObservationSet(
                Fixture.Observation(null, Fixture.ObservedAt, 1L))),
            "seek" => Fixture.Scenario(candidateSet: Fixture.CandidateSet(
                Fixture.Candidate(lastSeek: Fixture.Cell.Of(bad)))),
            "scan" => Fixture.Scenario(candidateSet: Fixture.CandidateSet(
                Fixture.Candidate(lastScan: Fixture.Cell.Of(bad)))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(reader, objectRequested: true));
        Assert.Equal(MalformedMessage, ex.Message);
        Assert.DoesNotContain(bad, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_missing_result_set_is_malformed()
    {
        var reader = Fixture.Of(
            Fixture.ObservationSet(Fixture.Observation(1L)),
            Fixture.CandidateSet(Fixture.Candidate()),
            Fixture.DefaultCatalogSet());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(reader));
        Assert.Equal(MalformedMessage, ex.Message);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zero-observations")]
    [InlineData("two-observations")]
    [InlineData("extra")]
    [InlineData("duplicate-index")]
    [InlineData("orphan-column")]
    [InlineData("orphan-compression")]
    [InlineData("null-schema")]
    public async Task Reader_malformed_shape_is_rejected(string kind)
    {
        var reader = kind switch
        {
            "short" => Fixture.Of(
                Fixture.ObservationSet(Fixture.Observation(1L)),
                Fixture.Narrow(Fixture.CandidateColumns.Take(3).ToArray(), [11L, "dbo", "Contracts"]),
                Fixture.DefaultCatalogSet(),
                Fixture.IndexSet(Fixture.Index()),
                Fixture.IndexColumnSet(Fixture.Column()),
                Fixture.CompressionSet(Fixture.Compression())),
            "zero-observations" => Fixture.Scenario(observationSet: Fixture.ObservationSet()),
            "two-observations" => Fixture.Scenario(observationSet: Fixture.ObservationSet(
                Fixture.Observation(1L),
                Fixture.Observation(1L))),
            "extra" => Fixture.Scenario(trailing: [Fixture.Narrow(["leaked"], ["planted-cell"])]),
            "duplicate-index" => Fixture.Scenario(indexSet: Fixture.IndexSet(
                Fixture.Index(filter: Fixture.Cell.Of(null)),
                Fixture.Index(schema: Fixture.Cell.Of("DBO"), table: Fixture.Cell.Of("contracts"), filter: Fixture.Cell.Of(null)))),
            "orphan-column" => Fixture.Scenario(indexColumnSet: Fixture.IndexColumnSet(
                Fixture.Column(indexId: 99))),
            "orphan-compression" => Fixture.Scenario(compressionSet: Fixture.CompressionSet(
                Fixture.Compression(indexId: Fixture.Cell.Of(99)))),
            "null-schema" => Fixture.Scenario(candidateSet: Fixture.CandidateSet(
                Fixture.Candidate(schema: Fixture.Cell.Of(null)))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(reader));
        Assert.Equal(MalformedMessage, ex.Message);
        Assert.DoesNotContain("planted-cell", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedObject, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_negative_match_count_in_object_mode_is_safety()
    {
        var ex = await Assert.ThrowsAsync<SqlHarnessSafetyException>(() => Read(
            Fixture.Scenario(matchCount: -1),
            objectRequested: true));

        Assert.Equal(SafetyMessage, ex.Message);
        Assert.DoesNotContain("-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_negative_match_count_database_wide_is_malformed()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(
            Fixture.Scenario(matchCount: -1),
            objectRequested: false));

        Assert.Equal(MalformedMessage, ex.Message);
        Assert.DoesNotContain("-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var reader = new CancelOnReadReader(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IndexAnalysisQuery.ReadAsync(reader, objectRequested: false, cts.Token));
    }

    private static Task<CollectedIndexAnalysis> Read(ISqlReader reader, bool objectRequested = true) =>
        IndexAnalysisQuery.ReadAsync(reader, objectRequested, CancellationToken.None);

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

        public TemporaryCulture(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }

    private sealed class CancelOnReadReader(CancellationTokenSource cts) : ISqlReader
    {
        public int FieldCount => 3;

        public int RecordsAffected => -1;

        public string GetName(int ordinal) => ordinal switch
        {
            0 => "observation_since",
            1 => "observed_at",
            _ => "object_match_count",
        };

        public Type GetFieldType(int ordinal) => typeof(object);

        public bool GetAllowNull(int ordinal) => true;

        public object GetValue(int ordinal) => throw new InvalidOperationException("unreachable");

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static class Fixture
    {
        public static readonly DateTime ObservationSince = new(2026, 7, 29, 1, 2, 3, DateTimeKind.Unspecified);

        public static readonly DateTimeOffset ObservedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.FromHours(2));

        public static readonly DateTime LastSeekLocal = new(2026, 7, 29, 15, 45, 0, DateTimeKind.Local);

        public static readonly DateTime LastScanUtc = new(2026, 7, 28, 23, 30, 0, DateTimeKind.Utc);

        private static readonly string[] ObservationColumns =
        [
            "observation_since",
            "observed_at",
            "object_match_count",
        ];

        public static readonly string[] CandidateColumns =
        [
            "candidate_id",
            "schema_name",
            "table_name",
            "equality_columns",
            "inequality_columns",
            "included_columns",
            "user_seeks",
            "user_scans",
            "avg_total_user_cost",
            "avg_user_impact",
            "cumulative_impact_score",
            "last_user_seek",
            "last_user_scan",
        ];

        private static readonly string[] CatalogColumns = ["schema_name", "table_name", "column_name"];

        private static readonly string[] IndexHeaderColumns =
        [
            "schema_name",
            "table_name",
            "index_id",
            "index_name",
            "type_desc",
            "is_unique",
            "is_primary_key",
            "is_unique_constraint",
            "is_disabled",
            "filter_definition",
        ];

        private static readonly string[] IndexColumnColumns =
        [
            "schema_name",
            "table_name",
            "index_id",
            "column_name",
            "key_ordinal",
            "is_included_column",
            "is_descending_key",
            "index_column_id",
        ];

        private static readonly string[] CompressionColumns =
        [
            "schema_name",
            "table_name",
            "index_id",
            "data_compression_desc",
        ];

        public static ISqlReader Reader(string? filter = "[Status]=(1)", long objectMatchCount = 1) =>
            Scenario(matchCount: objectMatchCount, filter: filter);

        public static ISqlReader Scenario(
            long matchCount = 1,
            string? filter = "[Status]=(1)",
            object?[][]? candidateSet = null,
            object?[][]? catalogSet = null,
            object?[][]? indexSet = null,
            object?[][]? indexColumnSet = null,
            object?[][]? compressionSet = null,
            object?[][]? observationSet = null,
            object?[][][]? trailing = null)
        {
            var sets = new List<object?[][]>
            {
                observationSet ?? ObservationSet(Observation(matchCount)),
                candidateSet ?? CandidateSet(Candidate()),
                catalogSet ?? DefaultCatalogSet(),
                indexSet ?? IndexSet(Index(filter: Cell.Of(filter))),
                indexColumnSet ?? IndexColumnSet(Column()),
                compressionSet ?? CompressionSet(Compression()),
            };
            if (trailing is not null)
                sets.AddRange(trailing);

            return new FakeReader(sets.ToArray());
        }

        public static ISqlReader Of(params object?[][][] sets) => new FakeReader(sets);

        public static object?[][] ObservationSet(params object?[][] rows) => Set(ObservationColumns, rows);

        public static object?[] Observation(object? matchCount) =>
            Observation(ObservationSince, ObservedAt, matchCount);

        public static object?[] Observation(object? since, object? observedAt, object? matchCount) =>
            [since, observedAt, matchCount];

        public static object?[][] CandidateSet(params object?[][] rows) => Set(CandidateColumns, rows);

        public static object?[] Candidate(
            Cell candidateId = default,
            Cell schema = default,
            Cell table = default,
            Cell equality = default,
            Cell inequality = default,
            Cell included = default,
            Cell userSeeks = default,
            Cell userScans = default,
            Cell averageCost = default,
            Cell averageImpact = default,
            Cell score = default,
            Cell lastSeek = default,
            Cell lastScan = default) =>
        [
            candidateId.Or(11L),
            schema.Or("dbo"),
            table.Or("Contracts"),
            equality.Or("[tenantid]"),
            inequality.Or("[createdat]"),
            included.Or("[name], [status]"),
            userSeeks.Or(3L),
            userScans.Or(4L),
            averageCost.Or(2.5m),
            averageImpact.Or(50m),
            score.Or(99.25m),
            lastSeek.Or(LastSeekLocal),
            lastScan.Or(LastScanUtc),
        ];

        public static object?[][] CatalogSet(params object?[][] rows) => Set(CatalogColumns, rows);

        public static object?[][] DefaultCatalogSet() => CatalogSet(
            Catalog("dbo", "Contracts", "TenantId"),
            Catalog("dbo", "Contracts", "CreatedAt"),
            Catalog("dbo", "Contracts", "Name"),
            Catalog("dbo", "Contracts", "Status"));

        public static object?[] Catalog(string schema, string table, string column) => [schema, table, column];

        public static object?[][] IndexSet(params object?[][] rows) => Set(IndexHeaderColumns, rows);

        public static object?[] Index(
            Cell schema = default,
            Cell table = default,
            Cell indexId = default,
            Cell name = default,
            Cell type = default,
            Cell unique = default,
            Cell primaryKey = default,
            Cell uniqueConstraint = default,
            Cell disabled = default,
            Cell filter = default) =>
        [
            schema.Or("dbo"),
            table.Or("Contracts"),
            indexId.Or(2),
            name.Or("IX_Contracts_Tenant"),
            type.Or("NONCLUSTERED"),
            unique.Or(false),
            primaryKey.Or(false),
            uniqueConstraint.Or(false),
            disabled.Or(false),
            filter.Or("[Status]=(1)"),
        ];

        public static object?[][] IndexColumnSet(params object?[][] rows) => Set(IndexColumnColumns, rows);

        public static object?[] Column(
            string column = "TenantId",
            object? keyOrdinal = null,
            object? included = null,
            object? descending = null,
            object? indexColumnId = null,
            string schema = "dbo",
            string table = "Contracts",
            object? indexId = null) =>
        [
            schema,
            table,
            indexId ?? 2,
            column,
            keyOrdinal ?? 1,
            included ?? false,
            descending ?? false,
            indexColumnId ?? 1,
        ];

        public static object?[][] CompressionSet(params object?[][] rows) => Set(CompressionColumns, rows);

        public static object?[] Compression(
            Cell schema = default,
            Cell table = default,
            Cell indexId = default,
            Cell description = default) =>
        [
            schema.Or("dbo"),
            table.Or("Contracts"),
            indexId.Or(2),
            description.Or("PAGE"),
        ];

        public static object?[][] Narrow(string[] names, params object?[][] rows) => Set(names, rows);

        private static object?[][] Set(string[] names, params object?[][] rows) => [[.. names], .. rows];

        public readonly record struct Cell(object? Value, bool Specified)
        {
            public static Cell Of(object? value) => new(value, true);

            public object? Or(object? fallback) => Specified ? Value : fallback;
        }
    }

    private sealed class FakeReader(params object?[][][] sets) : ISqlReader
    {
        private int _set;
        private int _row;

        private string[] Names => sets[_set][0].Cast<string>().ToArray();

        private object?[][] Rows => sets[_set].Skip(1).ToArray();

        public int FieldCount => Names.Length;

        public int RecordsAffected => -1;

        public string GetName(int ordinal) => Names[ordinal];

        public Type GetFieldType(int ordinal) => typeof(object);

        public bool GetAllowNull(int ordinal) => true;

        public object GetValue(int ordinal) => Rows[_row - 1][ordinal] ?? DBNull.Value;

        public Task<bool> ReadAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_row < Rows.Length)
            {
                _row++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<bool> NextResultAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (++_set < sets.Length)
            {
                _row = 0;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}