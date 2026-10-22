using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlHarness.Core;

public sealed class ParameterSetFileException(string message) : Exception(message);

public static partial class ParameterSetFileReader
{
    public const int MaximumBytes = ParameterFileIngressBudget.MaximumFileBytes;

    private const string UnableToReadReason = "could not be read";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<SqlHarnessParameterSetInput> ReadAsync(string path, CancellationToken cancellationToken)
        => await ReadAsync(path, new ParameterFileIngressBudget(), cancellationToken);

    public static async Task<SqlHarnessParameterSetInput> ReadAsync(
        string path, ParameterFileIngressBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes;
        try
        {
            bytes = await ReadAtMostAsync(path, budget, cancellationToken);
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
            throw new ParameterSetFileException(FileMessage(path, UnableToReadReason));
        }

        try
        {
            return Parse(bytes, path);
        }
        catch (ParameterSetFileException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ParameterSetFileException(FileMessage(path, "has invalid JSON"));
        }
        catch (DecoderFallbackException)
        {
            throw new ParameterSetFileException(FileMessage(path, "is not valid UTF-8"));
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(
        string path, ParameterFileIngressBudget budget, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ParameterSetFileException(FileMessage(path, UnableToReadReason));

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await budget.ReadAsync(stream, path, cancellationToken);
    }

    /// <summary>
    /// Parses already-read parameter-set bytes: at most 16 MiB, optional BOM,
    /// comments, and trailing commas, with only name and parameters members. Shared
    /// by the CLI file reader and the MCP bounded input reader so both enforce
    /// the same contract without a second parser. Only
    /// <see cref="ParameterSetFileException"/> escapes this method.
    /// </summary>
    public static SqlHarnessParameterSetInput Parse(byte[] bytes, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes)
            throw new ParameterSetFileException(FileMessage(path, "exceeds the 16 MiB raw parameter-file limit"));

        try
        {
            return ParseCore(bytes, path);
        }
        catch (UnknownPropertyException exception)
        {
            throw new ParameterSetFileException(FileMessage(path, $"contains unknown property '{exception.Message}'"));
        }
        catch (JsonException exception)
        {
            var line = (exception.LineNumber ?? 0) + 1;
            var bomLength = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            var bytePosition = (exception.BytePositionInLine ?? 0) + 1 + (line == 1 ? bomLength : 0);
            var json = bytes.AsSpan(bomLength);
            if (IsUnterminatedTopLevelObject(json))
            {
                line = 1;
                long bytePositionInLine = 0;
                for (var index = 0; index < json.Length; index++)
                {
                    if (json[index] == (byte)'\n')
                    {
                        line++;
                        bytePositionInLine = 0;
                    }
                    else
                    {
                        bytePositionInLine++;
                    }
                }
                bytePosition = bytePositionInLine + 1 + (line == 1 ? bomLength : 0);
            }
            throw new ParameterSetFileException(FileMessage(path, $"has invalid JSON at line {line}, byte {bytePosition}"));
        }
        catch (DecoderFallbackException)
        {
            throw new ParameterSetFileException(FileMessage(path, "is not valid UTF-8"));
        }
    }

    private static SqlHarnessParameterSetInput ParseCore(byte[] bytes, string? path)
    {
        var text = StrictUtf8.GetString(bytes);
        var json = text.Length > 0 && text[0] == '\uFEFF' ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;

        var reader = new Utf8JsonReader(json, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw InvalidJson("Expected a JSON object.", json, reader.TokenStartIndex);

        string? name = null;
        long? nameTokenStart = null;
        List<string>? parameters = null;
        List<SqlHarnessParameterInput>? typedParameters = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw InvalidJson("Expected a property name.", json, reader.TokenStartIndex);

            var property = reader.GetString();
            if (property is null || !seen.Add(property))
                throw InvalidJson("Duplicate property.", json, reader.TokenStartIndex);
            if (!reader.Read())
                throw InvalidJson("Missing property value.", json, reader.BytesConsumed);

            switch (property)
            {
                case "name":
                    if (reader.TokenType != JsonTokenType.String)
                        throw InvalidJson("The name property must be a string.", json, reader.TokenStartIndex);
                    name = reader.GetString();
                    nameTokenStart = reader.TokenStartIndex;
                    break;
                case "parameters":
                    (parameters, typedParameters) = ReadParameters(ref reader, json, path);
                    break;
                default:
                    throw new UnknownPropertyException(property);
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
            throw InvalidJson("The object must contain name and parameters.", json, json.Length);
        if (name is null || parameters is null || typedParameters is null)
            throw InvalidJson("The object must contain name and parameters.", json, reader.TokenStartIndex);
        // '$' matches before a trailing newline; the label must be the entire string.
        var label = LabelPattern().Match(name);
        if (!label.Success || label.Length != name.Length)
            throw InvalidJson("The name property is not a valid set label.", json, nameTokenStart ?? reader.TokenStartIndex);
        if (reader.Read())
            throw InvalidJson("Unexpected content after the JSON object.", json, reader.TokenStartIndex);

        return new SqlHarnessParameterSetInput(name, parameters.ToArray())
        {
            TypedParameters = typedParameters.Count == 0 ? null : typedParameters.ToArray(),
            SourcePath = path,
        };
    }

    private static (List<string> Legacy, List<SqlHarnessParameterInput> Typed) ReadParameters(
        ref Utf8JsonReader reader,
        ReadOnlySpan<byte> json,
        string? path)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw InvalidJson("The parameters property must be an array.", json, reader.TokenStartIndex);

        var parameters = new List<string>();
        var typedParameters = new List<SqlHarnessParameterInput>();
        var entryKind = -1;
        while (true)
        {
            if (!reader.Read())
                throw InvalidJson("The parameters array is incomplete.", json, reader.BytesConsumed);
            if (reader.TokenType == JsonTokenType.EndArray)
                break;
            if (reader.TokenType == JsonTokenType.String)
            {
                if (entryKind == 1)
                    throw InvalidJson("A parameter set cannot mix string and typed parameter entries.", json, reader.TokenStartIndex);
                entryKind = 0;
                var value = reader.GetString();
                if (string.IsNullOrEmpty(value))
                    throw InvalidJson("Each parameters entry must be nonempty.", json, reader.TokenStartIndex);
                parameters.Add(value);
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                if (entryKind == 0)
                    throw InvalidJson("A parameter set cannot mix string and typed parameter entries.", json, reader.TokenStartIndex);
                entryKind = 1;
                using var element = JsonDocument.ParseValue(ref reader);
                try
                {
                    var wrapper = Encoding.UTF8.GetBytes("{\"parameters\":[" + element.RootElement.GetRawText() + "]}");
                    var parsed = ParameterInputFileReader.Parse(wrapper, path);
                    if (parsed.Count != 1)
                        throw InvalidJson("Each typed parameter entry must produce exactly one parameter.", json, reader.TokenStartIndex);
                    typedParameters.Add(parsed[0]);
                }
                catch (ParameterSetFileException exception)
                {
                    throw InvalidJson(exception.Message, json, reader.TokenStartIndex);
                }
            }
            else
            {
                throw InvalidJson("Each parameters entry must be a string or typed parameter object.", json, reader.TokenStartIndex);
            }
        }

        if (parameters.Count == 0 && typedParameters.Count == 0)
            throw InvalidJson("The parameters array must not be empty.", json, reader.TokenStartIndex);
        return (parameters, typedParameters);
    }

    private static JsonException InvalidJson(string message, ReadOnlySpan<byte> json, long offset)
    {
        long line = 0;
        long bytePosition = 0;
        var end = (int)Math.Clamp(offset, 0, json.Length);
        for (var index = 0; index < end; index++)
        {
            if (json[index] == (byte)'\n')
            {
                line++;
                bytePosition = 0;
            }
            else
            {
                bytePosition++;
            }
        }

        return new JsonException(message, null, line, bytePosition, null);
    }

    private static string FileMessage(string? path, string reason) =>
        path is null
            ? $"Parameter set file {reason}."
            : $"Parameter set file '{path}' {reason}.";

    private static bool IsUnterminatedTopLevelObject(ReadOnlySpan<byte> json)
    {
        var first = 0;
        while (first < json.Length && IsJsonWhitespace(json[first]))
            first++;
        var last = json.Length - 1;
        while (last >= first && IsJsonWhitespace(json[last]))
            last--;
        return first < last && json[first] == (byte)'{' && json[last] == (byte)']';
    }

    private static bool IsJsonWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private sealed class UnknownPropertyException(string property) : Exception(property);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();
}
