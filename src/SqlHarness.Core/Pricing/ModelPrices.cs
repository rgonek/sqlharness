using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlHarness.Core;

public sealed record ModelPrice(string Provider, string Model, decimal InputPerMillion);

/// <summary>Input prices in USD per 1M tokens by provider and model (models.dev ids).</summary>
public sealed record ModelPriceTable(
    string Source,
    DateTimeOffset FetchedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> InputPerMillion)
{
    public ModelPrice? Find(string? agentKind, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var slash = model.IndexOf('/');
        if (slash > 0 && Get(model[..slash], model[(slash + 1)..]) is { } qualified)
            return qualified;
        if (ModelPrices.ProviderForAgent(agentKind) is { } provider && Get(provider, model) is { } byAgent)
            return byAgent;
        foreach (var primary in ModelPrices.PrimaryProviders)
        {
            if (Get(primary, model) is { } found)
                return found;
        }

        return null;
    }

    private ModelPrice? Get(string provider, string model) =>
        InputPerMillion.TryGetValue(provider, out var models) && models.TryGetValue(model, out var input)
            ? new ModelPrice(provider, model, input)
            : null;
}

public static class ModelPrices
{
    public static readonly IReadOnlyList<string> PrimaryProviders = ["anthropic", "openai", "google", "xai"];

    private static readonly Lazy<ModelPriceTable> EmbeddedTable = new(() =>
    {
        using var stream = typeof(ModelPrices).Assembly.GetManifestResourceStream("SqlHarness.Core.Pricing.model-prices.json")
            ?? throw new InvalidOperationException("The embedded model price snapshot is missing.");
        return Parse(stream);
    });

    public static ModelPriceTable Embedded => EmbeddedTable.Value;

    internal static string? ProviderForAgent(string? agentKind) => agentKind switch
    {
        "claude" => "anthropic",
        "codex" => "openai",
        "grok" => "xai",
        _ => null,
    };

    /// <summary>The refreshed file when it parses and is newer than the embedded snapshot; otherwise the snapshot.</summary>
    public static ModelPriceTable Load(string refreshedPath)
    {
        try
        {
            if (File.Exists(refreshedPath))
            {
                using var stream = File.OpenRead(refreshedPath);
                var refreshed = Parse(stream);
                if (refreshed.FetchedAt > Embedded.FetchedAt)
                    return refreshed;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
        }

        return Embedded;
    }

    public static ModelPriceTable Parse(Stream json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Empty price table.");
        var source = root["source"]?.GetValue<string>() ?? throw new JsonException("Missing source.");
        var fetchedAt = DateTimeOffset.Parse(root["fetchedAt"]?.GetValue<string>() ?? throw new JsonException("Missing fetchedAt."), System.Globalization.CultureInfo.InvariantCulture);
        var providers = new Dictionary<string, IReadOnlyDictionary<string, decimal>>(StringComparer.Ordinal);
        foreach (var (provider, modelsNode) in root["providers"]?.AsObject() ?? throw new JsonException("Missing providers."))
        {
            var models = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var (model, cost) in modelsNode?.AsObject() ?? [])
            {
                if (cost?["input"] is JsonValue input && input.TryGetValue<decimal>(out var value) && value >= 0)
                    models[model] = value;
            }

            providers[provider] = models;
        }

        return new ModelPriceTable(source, fetchedAt, providers);
    }

    /// <summary>Reduces models.dev api.json to the snapshot shape: cost fields of models that have a cost.</summary>
    public static string Extract(Stream modelsDevApiJson, DateTimeOffset fetchedAt)
    {
        var api = JsonNode.Parse(modelsDevApiJson)?.AsObject() ?? throw new JsonException("Empty models.dev document.");
        var providers = new JsonObject();
        foreach (var (provider, node) in api)
        {
            var models = new JsonObject();
            foreach (var (model, info) in node?["models"]?.AsObject() ?? [])
            {
                if (info?["cost"] is JsonObject cost)
                    models[model] = cost.DeepClone();
            }

            if (models.Count > 0)
                providers[provider] = models;
        }

        var snapshot = new JsonObject
        {
            ["source"] = "models.dev",
            ["fetchedAt"] = fetchedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["providers"] = providers,
        };
        return snapshot.ToJsonString();
    }
}