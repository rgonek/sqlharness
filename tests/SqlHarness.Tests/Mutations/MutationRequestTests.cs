using SqlHarness.Core;
using SqlHarness.Core.Mutations;

namespace SqlHarness.Tests.Mutations;

public sealed class MutationRequestTests
{
    [Fact]
    public void Digest_is_stable_across_variable_insertion_order()
    {
        var first = Request(vars: new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });
        var second = Request(vars: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        Assert.Equal(first.Digest, second.Digest);
    }

    [Fact]
    public void Digest_changes_when_exact_sql_bytes_change()
    {
        Assert.NotEqual(Request(sql: "UPDATE t SET x = 1").Digest, Request(sql: "UPDATE t SET x = 1 ").Digest);
    }

    [Fact]
    public void Digest_changes_when_parameter_value_changes()
    {
        Assert.NotEqual(Request(parameters: [new("id", "int", "1")]).Digest,
            Request(parameters: [new("id", "int", "2")]).Digest);
    }

    [Fact]
    public void Digest_changes_when_typed_null_parameter_type_changes()
    {
        Assert.NotEqual(Request(parameters: [new("value", "int", null)]).Digest,
            Request(parameters: [new("value", "bigint", null)]).Digest);
    }

    [Fact]
    public void Constructor_freezes_caller_owned_collections()
    {
        var vars = new Dictionary<string, string> { ["schema"] = "one" };
        var parameters = new List<SqlHarnessParameterInput> { new("id", "int", "1") };
        var grantees = new List<string> { "reader" };
        var request = Request(vars, parameters, grantees);
        var digest = request.Digest;

        vars["schema"] = "two";
        parameters[0] = new("id", "int", "2");
        parameters.Add(new("other", "int", "3"));
        grantees[0] = "writer";

        Assert.Equal(digest, request.Digest);
        Assert.Equal("one", request.Vars["schema"]);
        Assert.Equal("1", Assert.Single(request.Parameters).Value);
        Assert.Equal("reader", Assert.Single(request.Grantees));
    }

    private static MutationRequest Request(
        IReadOnlyDictionary<string, string>? vars = null,
        IReadOnlyList<SqlHarnessParameterInput>? parameters = null,
        IReadOnlyList<string>? grantees = null,
        string sql = "UPDATE t SET x = 1") =>
        new("profile", vars ?? new Dictionary<string, string>(), "sqlserver", "server", "database", sql,
            parameters ?? [], 30, 50, "dml", grantees ?? [], "session");
}
