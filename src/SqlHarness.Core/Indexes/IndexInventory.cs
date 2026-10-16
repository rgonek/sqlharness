using System.Text.Json.Serialization;

namespace SqlHarness.Core;

public sealed record InventoryIndex(
    int IndexId,
    string Name,
    string Type,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> IncludeColumns,
    bool Unique,
    bool PrimaryKey,
    bool Disabled,
    bool Hypothetical,
    bool HasFilter);

public sealed record InventoryTable(
    string Schema,
    string Table,
    bool ObjectFound,
    IReadOnlyList<InventoryIndex> Indexes,
    bool IndexesTruncated);

public sealed record IndexInventory(
    string Database,
    DateTimeOffset CapturedAt,
    IReadOnlyList<InventoryTable> Tables,
    IReadOnlyList<string> Gaps);

public sealed record MatchedIndex(
    string Name,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> IncludeColumns,
    bool Filtered);

public sealed record IndexOverlap(
    IndexOverlapVerdict Verdict,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MatchedIndex? Index,
    int MatchedKeyColumns,
    int HintKeyColumns,
    IReadOnlyList<string> MissingIncludeColumns);