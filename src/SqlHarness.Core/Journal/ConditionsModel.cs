using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlHarness.Core;

public sealed record ConditionsModel(
    int Version,
    string Engine,
    string? Server,
    string? Database,
    ConditionsExecution Execution,
    ConditionsSetup? Setup,
    IReadOnlyList<ConditionsParameter> Fixed,
    IReadOnlyList<ConditionsCase> Cases,
    string Fingerprint,
    IReadOnlyList<string> Omitted)
{
    internal string? SetupTextForFingerprint { get; init; }
    internal IReadOnlyList<ConditionsParameter> FixedForFingerprint { get; init; } = [];
}

public sealed record ConditionsExecution(
    string Operation,
    string SessionPolicy,
    string SetupBinding,
    int Warmup,
    int Repeat,
    int TimeoutSeconds,
    string? ComparisonMode,
    bool? CompareNullability);

public sealed record ConditionsSetup(string Hash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text);

public sealed record ConditionsParameter(string Name, string EffectiveType, JsonElement? Value);

public sealed record ConditionsCase(string Id, int Index, string Label, string? SetName,
    IReadOnlyList<ConditionsParameter> Varying, string Fingerprint)
{
    internal IReadOnlyList<ConditionsParameter> VaryingForFingerprint { get; init; } = [];
}
