using System.Data;
using System.Globalization;

using Npgsql;

using NpgsqlTypes;

namespace SqlHarness.Core.Postgres;

internal static class PostgresParameters
{
    internal static void Validate(IReadOnlyList<SqlHarnessParameter> parameters)
    {
        foreach (var parameter in parameters)
            ValidateOne(parameter);
    }

    internal static void Bind(NpgsqlCommand command, IReadOnlyList<SqlHarnessParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            var npgsql = CreateParameter(parameter);
            command.Parameters.Add(npgsql);
        }
    }

    private static void ValidateOne(SqlHarnessParameter parameter)
    {
        switch (parameter.Type)
        {
            case SqlDbType.Money:
            case SqlDbType.SmallMoney:
            case SqlDbType.SmallDateTime:
                throw RejectedType(parameter.Type.ToString());
            case SqlDbType.Udt:
                throw RejectedType(parameter.UdtTypeName ?? "udt");
            case SqlDbType.TinyInt:
                if (parameter.Value is DBNull)
                    return;
                var value = Convert.ToInt32(parameter.Value, CultureInfo.InvariantCulture);
                if (value is < 0 or > 255)
                {
                    throw new SqlHarnessSafetyException(
                        $"SQL parameter '{parameter.Name}' tinyint value is out of range.");
                }

                return;
            default:
                return;
        }
    }

    private static SqlHarnessSafetyException RejectedType(string typeName) =>
        new($"SQL parameter type '{typeName}' is not supported on Postgres.");

    private static NpgsqlParameter CreateParameter(SqlHarnessParameter parameter)
    {
        var (dbType, value) = Map(parameter);
        var npgsql = new NpgsqlParameter(parameter.Name, dbType)
        {
            Value = value,
        };
        if (parameter.Size is { } size)
            npgsql.Size = size;
        if (parameter.Precision is { } precision)
            npgsql.Precision = precision;
        if (parameter.Scale is { } scale)
            npgsql.Scale = scale;
        return npgsql;
    }

    private static (NpgsqlDbType DbType, object Value) Map(SqlHarnessParameter parameter)
    {
        var value = parameter.Value;
        var dbType = parameter.Type switch
        {
            SqlDbType.NVarChar => NpgsqlDbType.Text,
            SqlDbType.VarChar => parameter.Size == -1 ? NpgsqlDbType.Text : NpgsqlDbType.Varchar,
            SqlDbType.Char or SqlDbType.NChar => NpgsqlDbType.Char,
            SqlDbType.Int => NpgsqlDbType.Integer,
            SqlDbType.BigInt => NpgsqlDbType.Bigint,
            SqlDbType.SmallInt => NpgsqlDbType.Smallint,
            SqlDbType.TinyInt => NpgsqlDbType.Smallint,
            SqlDbType.Bit => NpgsqlDbType.Boolean,
            SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney => NpgsqlDbType.Numeric,
            SqlDbType.Float => NpgsqlDbType.Double,
            SqlDbType.Real => NpgsqlDbType.Real,
            SqlDbType.Date => NpgsqlDbType.Date,
            SqlDbType.Time => NpgsqlDbType.Time,
            SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime => NpgsqlDbType.Timestamp,
            SqlDbType.DateTimeOffset => NpgsqlDbType.TimestampTz,
            SqlDbType.UniqueIdentifier => NpgsqlDbType.Uuid,
            SqlDbType.VarBinary or SqlDbType.Binary => NpgsqlDbType.Bytea,
            _ => throw new SqlHarnessSafetyException(
                $"SQL parameter type '{parameter.Type}' is not supported on Postgres."),
        };

        if (value is DBNull)
            return (dbType, DBNull.Value);

        if (parameter.Type == SqlDbType.TinyInt)
            value = Convert.ToInt16(value, CultureInfo.InvariantCulture);

        return (dbType, value);
    }
}