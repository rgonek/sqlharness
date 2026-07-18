using System.Text.Json;

namespace SqlHarness.Core.Auth;

public sealed class AzureCliException(string message) : Exception(message);

public interface IAzureCli
{
    Task<bool> IsLoggedInAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> RunJsonAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}