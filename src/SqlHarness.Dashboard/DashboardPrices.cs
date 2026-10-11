using Microsoft.AspNetCore.Http;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

internal static class DashboardPrices
{
    internal const long MaximumResponseBytes = 20L * 1024 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string FailureMessage = "Model prices could not be refreshed.";
    private static readonly HttpClient Http = new() { Timeout = Timeout };

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
        var buffer = new MemoryStream();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Response too large.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    internal static async Task<IResult> RefreshAsync(
        string pricesPath, Func<CancellationToken, Task<Stream>> fetch, System.Text.Json.JsonSerializerOptions json, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await using var source = await fetch(timeout.Token);
            var reduced = ModelPrices.Extract(source, DateTimeOffset.UtcNow);
            _ = ModelPrices.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(reduced)));
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
