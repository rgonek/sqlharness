using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlHarness.Core.Mutations;

public sealed record MutationRequest
{
    public MutationRequest(
        string Profile,
        IReadOnlyDictionary<string, string> Vars,
        string Engine,
        string Server,
        string Database,
        string Sql,
        IReadOnlyList<SqlHarnessParameterInput> Parameters,
        int TimeoutSeconds,
        int MaxRows,
        string Tier,
        IReadOnlyList<string> Grantees,
        string SessionKey)
    {
        this.Profile = Profile;
        this.Vars = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            Vars.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        this.Engine = Engine;
        this.Server = Server;
        this.Database = Database;
        this.Sql = Sql;
        this.Parameters = Array.AsReadOnly(Parameters.Select(parameter =>
            new SqlHarnessParameterInput(parameter.Name, parameter.Type, parameter.Value)).ToArray());
        this.TimeoutSeconds = TimeoutSeconds;
        this.MaxRows = MaxRows;
        this.Tier = Tier;
        this.Grantees = Array.AsReadOnly(Grantees.ToArray());
        this.SessionKey = SessionKey;
        Digest = ComputeDigest();
    }

    public string Profile { get; }
    public IReadOnlyDictionary<string, string> Vars { get; }
    public string Engine { get; }
    public string Server { get; }
    public string Database { get; }
    public string Sql { get; }
    public IReadOnlyList<SqlHarnessParameterInput> Parameters { get; }
    public int TimeoutSeconds { get; }
    public int MaxRows { get; }
    public string Tier { get; }
    public IReadOnlyList<string> Grantees { get; }
    public string SessionKey { get; }

    /// <summary>SHA-256 over canonical JSON. Lowercase hexadecimal.</summary>
    public string Digest { get; }

    private string ComputeDigest()
    {
        var boundParameters = SqlParameterParser.Bind(Parameters);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("profile", Profile);
            writer.WritePropertyName("vars");
            writer.WriteStartObject();
            foreach (var pair in Vars)
                writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteString("engine", Engine);
            writer.WriteString("server", Server);
            writer.WriteString("database", Database);
            writer.WriteString("sql", Sql);
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            for (var index = 0; index < Parameters.Count; index++)
            {
                var parameter = Parameters[index];
                var bound = boundParameters[index];
                writer.WriteStartObject();
                writer.WriteString("name", parameter.Name);
                if (parameter.Type is null) writer.WriteNull("type"); else writer.WriteString("type", parameter.Type);
                if (bound.Size is int size) writer.WriteNumber("size", size); else writer.WriteNull("size");
                if (bound.Precision is byte precision) writer.WriteNumber("precision", precision); else writer.WriteNull("precision");
                if (bound.Scale is byte scale) writer.WriteNumber("scale", scale); else writer.WriteNull("scale");
                if (parameter.Value is null) writer.WriteNull("value"); else writer.WriteString("value", parameter.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("timeoutSeconds", TimeoutSeconds);
            writer.WriteNumber("maxRows", MaxRows);
            writer.WriteString("tier", Tier);
            writer.WritePropertyName("grantees");
            writer.WriteStartArray();
            foreach (var grantee in Grantees)
                writer.WriteStringValue(grantee);
            writer.WriteEndArray();
            writer.WriteString("sessionKey", SessionKey);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }
}
