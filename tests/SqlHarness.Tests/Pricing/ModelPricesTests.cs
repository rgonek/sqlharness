using System.Text;

using SqlHarness.Core;

namespace SqlHarness.Tests.Pricing;

public sealed class ModelPricesTests
{
    private static ModelPriceTable Table(string providersJson, string fetchedAt = "2026-10-11T00:00:00Z") =>
        ModelPrices.Parse(new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"source":"models.dev","fetchedAt":"{{fetchedAt}}","providers":{{providersJson}}}""")));

    private static readonly ModelPriceTable Sample = Table("""
        {"anthropic":{"claude-haiku-5-5":{"input":0.1,"output":0.5}},
         "openai":{"gpt-6-sol":{"input":2,"output":10}},
         "xai":{"grok-4.7":{"input":2,"output":6}},
         "google":{"gemini-3.8-flash":{"input":0.75,"output":3.75}},
         "opencode":{"step-5-preview-free":{"input":0,"output":0},"claude-opus-5-5":{"input":4,"output":20}},
         "azure":{"gpt-x":{"input":9,"output":9}},
         "amazon-bedrock":{"claude-haiku-5-5":{"input":0.11,"output":0.55}}}
        """);

    [Theory]
    [InlineData("codex", "gpt-6-sol", "openai", 2)]
    [InlineData("claude", "claude-haiku-5-5", "anthropic", 0.1)]
    [InlineData("grok", "grok-4.7", "xai", 2)]
    [InlineData("opencode", "opencode/step-5-preview-free", "opencode", 0)]
    [InlineData("opencode", "opencode/claude-opus-5-5", "opencode", 4)]
    [InlineData("cursor", "gemini-3.8-flash", "google", 0.75)]
    [InlineData(null, "claude-haiku-5-5", "anthropic", 0.1)]
    public void Lookup_follows_the_documented_order(string? kind, string model, string provider, double input)
    {
        var price = Sample.Find(kind, model);

        Assert.NotNull(price);
        Assert.Equal(provider, price.Provider);
        Assert.Equal((decimal)input, price.InputPerMillion);
    }

    [Fact]
    public void Reseller_only_model_is_unpriced() => Assert.Null(Sample.Find("cursor", "gpt-x"));

    [Fact]
    public void Unknown_or_missing_model_is_unpriced()
    {
        Assert.Null(Sample.Find("codex", "gpt-nope"));
        Assert.Null(Sample.Find("codex", null));
        Assert.Null(Sample.Find("codex", " "));
    }

    [Fact]
    public void Embedded_snapshot_parses_and_prices_known_models()
    {
        var embedded = ModelPrices.Embedded;

        Assert.Equal("models.dev", embedded.Source);
        Assert.NotNull(embedded.Find("claude", "claude-haiku-5-5"));
        Assert.NotNull(embedded.Find("codex", "gpt-6-sol"));
    }

    [Fact]
    public void Newer_embedded_snapshot_wins_over_older_refresh()
    {
        using var temp = new Journal.JournalTempDirectory();
        var path = Path.Combine(temp.Path, "model-prices.json");
        File.WriteAllText(path, """{"source":"models.dev","fetchedAt":"2000-01-01T00:00:00Z","providers":{}}""");

        Assert.Equal(ModelPrices.Embedded.FetchedAt, ModelPrices.Load(path).FetchedAt);
    }

    [Fact]
    public void Newer_valid_refresh_wins_and_malformed_refresh_falls_back()
    {
        using var temp = new Journal.JournalTempDirectory();
        var path = Path.Combine(temp.Path, "model-prices.json");
        File.WriteAllText(path, """{"source":"models.dev","fetchedAt":"2999-01-01T00:00:00Z","providers":{"openai":{"gpt-6-sol":{"input":1}}}}""");
        Assert.Equal(1m, ModelPrices.Load(path).Find("codex", "gpt-6-sol")!.InputPerMillion);

        File.WriteAllText(path, "{ not json");
        Assert.Equal(ModelPrices.Embedded.FetchedAt, ModelPrices.Load(path).FetchedAt);
    }

    [Fact]
    public void Refresh_with_duplicate_model_key_falls_back_to_embedded()
    {
        using var temp = new Journal.JournalTempDirectory();
        var path = Path.Combine(temp.Path, "model-prices.json");
        File.WriteAllText(path, """{"source":"models.dev","fetchedAt":"2999-01-01T00:00:00Z","providers":{"openai":{"gpt-6-sol":{"input":1},"gpt-6-sol":{"input":2}}}}""");

        Assert.Equal(ModelPrices.Embedded.FetchedAt, ModelPrices.Load(path).FetchedAt);
    }

    [Fact]
    public void Extract_keeps_only_cost_fields_and_models_with_cost()
    {
        var api = """
            {"openai":{"id":"openai","name":"OpenAI","models":{
               "gpt-6-sol":{"id":"gpt-6-sol","name":"x","cost":{"input":2,"output":10,"cache_read":0.2}},
               "tts":{"id":"tts","name":"y"}}}}
            """;

        var json = ModelPrices.Extract(new MemoryStream(Encoding.UTF8.GetBytes(api)), DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
        var table = ModelPrices.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(2m, table.Find("codex", "gpt-6-sol")!.InputPerMillion);
        Assert.Null(table.Find("codex", "tts"));
        Assert.DoesNotContain("\"name\"", json);
    }
}