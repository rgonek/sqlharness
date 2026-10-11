using Microsoft.AspNetCore.Http;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

internal static class DashboardPrices
{
    internal const long MaximumResponseBytes = 20L * 1024 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string FailureMessage = "Model prices could not be refreshed.";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout };

    internal static object Describe(string pricesPath)
    {
        var table = ModelPrices.Load(pricesPath);
        var origin = table.FetchedAt == ModelPrices.Embedded.FetchedAt ? "embedded" : "refreshed";
        return new { source = table.Source, fetchedAt = table.FetchedAt, origin };
    }

    internal static async Task<Stream> FetchModelsDevAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync("https://models.dev/api.json", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Response too large.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await ReadCappedAsync(body, MaximumResponseBytes, ct);
    }

    /// <summary>Buffers <paramref name="body"/>, failing as soon as it exceeds <paramref name="maxBytes"/> (no trust in Content-Length).</summary>
    internal static async Task<Stream> ReadCappedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("Response too large.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    internal static async Task<IResult> RefreshAsync(
        string pricesPath, Func<CancellationToken, Task<Stream>> fetch, System.Text.Json.JsonSerializerOptions json, CancellationToken ct,
        TimeSpan? timeoutOverride = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutOverride ?? Timeout);
            await using var source = await fetch(timeout.Token);
            var reduced = ModelPrices.Extract(source, DateTimeOffset.UtcNow);
            var table = ModelPrices.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(reduced)));
            if (!ModelPrices.PrimaryProviders.Any(provider => table.InputPerMillion.TryGetValue(provider, out var models) && models.Count > 0))
                throw new InvalidDataException("No usable prices.");
            Directory.CreateDirectory(Path.GetDirectoryName(pricesPath)!);
            DashboardLock.WriteOwnerOnly(pricesPath, reduced);
            return Results.Json(Describe(pricesPath), json);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Results.Json(new { error = FailureMessage }, json, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
