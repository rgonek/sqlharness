using System.Data;
using System.Data.SqlTypes;
using System.Security.Cryptography;
using System.Text;

using Microsoft.SqlServer.Types;

namespace SqlHarness.Core;

public sealed record MeasureParameterMetadata(string Name, string Type);

internal sealed record PreparedMeasureParameterSet(
    string Name,
    IReadOnlyList<SqlHarnessParameter> Parameters,
    IReadOnlyList<MeasureParameterMetadata> Metadata,
    string ValueHash);

internal static class MeasureParameterSetValidator
{
    internal static IReadOnlyList<PreparedMeasureParameterSet> Prepare(
        IReadOnlyList<string> fixedParameters,
        IReadOnlyList<SqlHarnessParameterSetInput> parameterSets,
        string? setupSql,
        string querySql)
    {
        ArgumentNullException.ThrowIfNull(fixedParameters);
        ArgumentNullException.ThrowIfNull(parameterSets);
        ArgumentNullException.ThrowIfNull(querySql);

        var fixedParsed = Parse(fixedParameters, setName: null);
        var fixedNames = new HashSet<string>(
            fixedParsed.Select(parameter => parameter.Name),
            StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<PreparedMeasureParameterSet>(parameterSets.Count);
        IReadOnlyList<SqlHarnessParameter>? baseline = null;
        string? baselineName = null;

        foreach (var set in parameterSets)
        {
            ArgumentNullException.ThrowIfNull(set);
            ArgumentNullException.ThrowIfNull(set.Name);
            ArgumentNullException.ThrowIfNull(set.Parameters);
            if (!seenNames.Add(set.Name))
                throw new SqlHarnessSafetyException($"Duplicate parameter set '{set.Name}'.");

            var parsed = Parse(set.Parameters, set.Name);
            foreach (var parameter in parsed)
            {
                if (fixedNames.Contains(parameter.Name))
                {
                    throw new SqlHarnessSafetyException(
                        $"Parameter set '{set.Name}' repeats fixed SQL parameter '{parameter.Name}'.");
                }
            }

            var ordered = Order(fixedParsed.Concat(parsed));
            if (baseline is null || baselineName is null)
            {
                baseline = ordered;
                baselineName = set.Name;
            }
            else
            {
                EnsureSameShape(baselineName, baseline, set.Name, ordered);
            }

            SqlParameterReferenceValidator.Validate(ordered, setupSql, querySql);
            prepared.Add(new PreparedMeasureParameterSet(
                set.Name,
                ordered,
                ToMetadata(ordered),
                TypedParameterHasher.Hash(ordered)));
        }

        return prepared;
    }

    private static IReadOnlyList<SqlHarnessParameter> Parse(IReadOnlyList<string> inputs, string? setName)
    {
        try
        {
            return SqlParameterParser.Parse(inputs);
        }
        catch (SqlHarnessSafetyException exception)
        {
            // Drop the parser's inner exception so a rejected value cannot surface through ToString().
            var message = setName is null
                ? exception.Message
                : $"Parameter set '{setName}' is invalid: {exception.Message}";
            throw new SqlHarnessSafetyException(message);
        }
    }

    private static SqlHarnessParameter[] Order(IEnumerable<SqlHarnessParameter> parameters) =>
        parameters
            .OrderBy(parameter => parameter.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToArray();

    private static MeasureParameterMetadata[] ToMetadata(IReadOnlyList<SqlHarnessParameter> parameters) =>
        parameters
            .Select(parameter => new MeasureParameterMetadata(parameter.Name, MeasureParameterTyping.TypeName(parameter)))
            .ToArray();

    private static void EnsureSameShape(
        string baselineName,
        IReadOnlyList<SqlHarnessParameter> baseline,
        string candidateName,
        IReadOnlyList<SqlHarnessParameter> candidate)
    {
        var baselineByName = Index(baseline);
        var candidateByName = Index(candidate);

        foreach (var parameter in baseline)
        {
            if (!candidateByName.ContainsKey(parameter.Name))
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' is missing SQL parameter '{parameter.Name}'.");
            }
        }

        foreach (var parameter in candidate)
        {
            if (!baselineByName.ContainsKey(parameter.Name))
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' has unexpected SQL parameter '{parameter.Name}'.");
            }
        }

        foreach (var parameter in baseline)
        {
            var other = candidateByName[parameter.Name];
            var name = parameter.Name;
            if (!SameType(parameter, other))
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different type than parameter set '{baselineName}'.");
            }

            if (parameter.Size != other.Size)
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different size than parameter set '{baselineName}'.");
            }

            if (parameter.Precision != other.Precision)
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different precision than parameter set '{baselineName}'.");
            }

            if (parameter.Scale != other.Scale)
            {
                throw new SqlHarnessSafetyException(
                    $"Parameter set '{candidateName}' SQL parameter '{name}' has a different scale than parameter set '{baselineName}'.");
            }
        }
    }

    private static Dictionary<string, SqlHarnessParameter> Index(IReadOnlyList<SqlHarnessParameter> parameters) =>
        parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);

    private static bool SameType(SqlHarnessParameter left, SqlHarnessParameter right) =>
        left.Type == right.Type
        && string.Equals(left.UdtTypeName, right.UdtTypeName, StringComparison.OrdinalIgnoreCase);
}

internal static class TypedParameterHasher
{
    internal static string Hash(IReadOnlyList<SqlHarnessParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var ordered = parameters
            .OrderBy(parameter => parameter.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToArray();

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            // Versioned little-endian record. Names are lower-invariant so culture and input order cannot change the digest.
            writer.Write((byte)1);
            writer.Write(ordered.Length);
            foreach (var parameter in ordered)
                WriteParameter(writer, parameter);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteParameter(BinaryWriter writer, SqlHarnessParameter parameter)
    {
        WriteUtf8(writer, parameter.Name.ToLowerInvariant());
        WriteUtf8(writer, MeasureParameterTyping.TypeName(parameter));
        WriteOptional(writer, parameter.Size);
        WriteOptional(writer, parameter.Precision);
        WriteOptional(writer, parameter.Scale);
        var isNull = IsNull(parameter.Value);
        writer.Write(isNull);
        if (!isNull)
            WriteValue(writer, parameter.Value);
    }

    private static void WriteOptional(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value is int present)
            writer.Write(present);
    }

    private static void WriteOptional(BinaryWriter writer, byte? value)
    {
        writer.Write(value.HasValue);
        if (value is byte present)
            writer.Write(present);
    }

    private static bool IsNull(object? value) => value switch
    {
        null or DBNull => true,
        INullable nullable => nullable.IsNull,
        _ => false,
    };

    private static void WriteValue(BinaryWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                WriteUtf8(writer, text);
                break;
            case bool bit:
                writer.Write(bit);
                break;
            case byte tiny:
                writer.Write(tiny);
                break;
            case short small:
                writer.Write(small);
                break;
            case int number:
                writer.Write(number);
                break;
            case long big:
                writer.Write(big);
                break;
            case float real:
                writer.Write(BitConverter.SingleToInt32Bits(real));
                break;
            case double floating:
                writer.Write(BitConverter.DoubleToInt64Bits(floating));
                break;
            case decimal number:
                writer.Write(CanonicalDecimal(number));
                break;
            case DateTime dateTime:
                writer.Write(dateTime.Ticks);
                writer.Write((byte)dateTime.Kind);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.Write(dateTimeOffset.Ticks);
                writer.Write(dateTimeOffset.Offset.Ticks);
                break;
            case TimeSpan time:
                writer.Write(time.Ticks);
                break;
            case Guid guid:
                writer.Write(guid.ToByteArray());
                break;
            case byte[] bytes:
                writer.Write(bytes.Length);
                writer.Write(bytes);
                break;
            case SqlHierarchyId hierarchy:
                WriteNative(writer, hierarchy.Write);
                break;
            case SqlGeography geography:
                WriteNative(writer, geography.Write);
                break;
            case SqlGeometry geometry:
                WriteNative(writer, geometry.Write);
                break;
            default:
                throw new SqlHarnessSafetyException("SQL parameter value cannot be hashed.");
        }
    }

    // Trailing fractional zeros are not a distinct typed value. Declared precision and scale are written separately.
    private static decimal CanonicalDecimal(decimal value)
    {
        if (value == 0m)
            return 0m;

        while (true)
        {
            var scale = (decimal.GetBits(value)[3] >> 16) & 0x7F;
            if (scale == 0)
                return value;

            var reduced = decimal.Round(value, scale - 1, MidpointRounding.ToZero);
            var reducedScale = (decimal.GetBits(reduced)[3] >> 16) & 0x7F;
            if (reduced != value || reducedScale >= scale)
                return value;

            value = reduced;
        }
    }

    private static void WriteNative(BinaryWriter writer, Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var inner = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            write(inner);

        var bytes = buffer.ToArray();
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteUtf8(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}

file static class MeasureParameterTyping
{
    public static string TypeName(SqlHarnessParameter parameter) =>
        parameter.Type == SqlDbType.Udt && !string.IsNullOrEmpty(parameter.UdtTypeName)
            ? parameter.UdtTypeName.ToLowerInvariant()
            : parameter.Type.ToString().ToLowerInvariant();
}
