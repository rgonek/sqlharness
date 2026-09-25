using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SqlHarness.Core;

namespace SqlHarness.Cli.Infrastructure;

public sealed class ParameterSetFileException(string message) : Exception(message);

public static partial class ParameterSetFileReader
{
    public const int MaximumBytes = 65_536;

    private const string InvalidFileMessage = "The parameter set file is invalid.";
    private const string TooLargeMessage = "Parameter set file exceeds the 64 KiB limit.";
    private const string UnableToReadMessage = "Unable to read parameter set file.";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static async Task<SqlHarnessParameterSetInput> ReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes;
        try
        {
            bytes = await ReadAtMostAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ParameterSetFileException)
        {
            throw;
        }
        catch (Exception)
        {
            // Framework IO exceptions include the caller path. Do not keep that message.
            throw new ParameterSetFileException(UnableToReadMessage);
        }

        try
        {
            return Parse(bytes);
        }
        catch (ParameterSetFileException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ParameterSetFileException(InvalidFileMessage);
        }
        catch (DecoderFallbackException)
        {
            throw new ParameterSetFileException(InvalidFileMessage);
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ParameterSetFileException(UnableToReadMessage);

        // One extra byte distinguishes a 64 KiB file from a larger one without reading the rest.
        var buffer = new byte[MaximumBytes + 1];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
                break;
            total += read;
        }

        if (total > MaximumBytes)
            throw new ParameterSetFileException(TooLargeMessage);

        return buffer[..total];
    }

    private static SqlHarnessParameterSetInput Parse(byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
            throw new ParameterSetFileException(InvalidFileMessage);

        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new ParameterSetFileException(InvalidFileMessage);

        string? name = null;
        List<string>? parameters = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new ParameterSetFileException(InvalidFileMessage);

            var property = reader.GetString();
            if (property is null || !seen.Add(property))
                throw new ParameterSetFileException(InvalidFileMessage);
            if (!reader.Read())
                throw new ParameterSetFileException(InvalidFileMessage);

            switch (property)
            {
                case "name":
                    if (reader.TokenType != JsonTokenType.String)
                        throw new ParameterSetFileException(InvalidFileMessage);
                    name = reader.GetString();
                    break;
                case "parameters":
                    parameters = ReadParameters(ref reader);
                    break;
                default:
                    throw new ParameterSetFileException(InvalidFileMessage);
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || name is null || parameters is null)
            throw new ParameterSetFileException(InvalidFileMessage);
        // '$' matches before a trailing newline; the label must be the entire string.
        var label = LabelPattern().Match(name);
        if (!label.Success || label.Length != name.Length)
            throw new ParameterSetFileException(InvalidFileMessage);
        if (reader.Read())
            throw new ParameterSetFileException(InvalidFileMessage);

        return new SqlHarnessParameterSetInput(name, parameters.ToArray());
    }

    private static List<string> ReadParameters(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new ParameterSetFileException(InvalidFileMessage);

        var parameters = new List<string>();
        while (true)
        {
            if (!reader.Read())
                throw new ParameterSetFileException(InvalidFileMessage);
            if (reader.TokenType == JsonTokenType.EndArray)
                break;
            if (reader.TokenType != JsonTokenType.String)
                throw new ParameterSetFileException(InvalidFileMessage);

            var value = reader.GetString();
            if (string.IsNullOrEmpty(value))
                throw new ParameterSetFileException(InvalidFileMessage);
            parameters.Add(value);
        }

        if (parameters.Count == 0)
            throw new ParameterSetFileException(InvalidFileMessage);
        return parameters;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();
}
