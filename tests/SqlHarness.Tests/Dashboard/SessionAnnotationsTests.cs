using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.Data.Sqlite;

using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class SessionAnnotationsTests
{
    private static HttpRequestMessage Patch(long id, string json, bool guarded = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/sessions/{id}/annotation")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (guarded)
            request.Headers.Add(DashboardSecurity.WriteHeader, "1");
        return request;
    }

    private static long SeedSession(TempHome home, string key = "cli:a")
    {
        new JournalSeed(home.DatabasePath).Operation(JournalSeed.Session(key));
        return (long)SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath, $"SELECT id FROM sessions WHERE session_key = '{key}'").Single()["id"]!;
    }

    private static List<Dictionary<string, object?>> Annotations(TempHome home) =>
        SqlHarness.Tests.Journal.JournalDb.Rows(home.DatabasePath, "SELECT * FROM session_annotations");

    [Fact]
    public async Task Patch_sets_fields_independently_and_returns_the_annotation()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var first = await client.SendAsync(Patch(id, """{"title":"  BSI-1 orders  ","starred":true}"""));
        var second = await client.SendAsync(Patch(id, """{"note":"line 1\nline 2"}"""));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var annotation = await second.Content.ReadFromJsonAsync<SessionAnnotation>();
        Assert.Equal(new SessionAnnotation("BSI-1 orders", "line 1\nline 2", true, false), annotation);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    }

    [Fact]
    public async Task Archiving_twice_keeps_the_first_time_and_unarchive_clears_it()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        var time = new SqlHarness.Tests.Journal.FixedTimeProvider(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero));
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home, o => o with { Time = time });
        var (_, client) = dashboard;

        await client.SendAsync(Patch(id, """{"archived":true,"note":"keep"}"""));
        time.Now = time.Now.AddHours(1);
        await client.SendAsync(Patch(id, """{"archived":true}"""));
        Assert.Equal("2026-10-14T10:00:00.000Z", Annotations(home).Single()["archived_at"]);

        await client.SendAsync(Patch(id, """{"archived":false}"""));
        Assert.Null(Annotations(home).Single()["archived_at"]);
    }

    [Fact]
    public async Task Clearing_everything_leaves_no_empty_row_and_noop_unstar_creates_none()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Patch(id, """{"starred":false,"title":"   "}"""))).StatusCode);
        Assert.Empty(Annotations(home));

        await client.SendAsync(Patch(id, """{"title":"x"}"""));
        await client.SendAsync(Patch(id, """{"title":null}"""));
        Assert.Empty(Annotations(home));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"color":"red"}""")]
    [InlineData("""{"starred":"yes"}""")]
    [InlineData("""{"title":42}""")]
    [InlineData("""{"title":"a\nb"}""")]
    [InlineData("""{"title":"\ud800"}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public async Task Invalid_bodies_are_rejected_without_writing(string body)
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch(id, body))).StatusCode);
        Assert.Empty(Annotations(home));
    }

    [Fact]
    public async Task Length_limits_are_enforced()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Patch(id, $$"""{"title":"{{new string('t', 200)}}","note":"{{new string('n', 10_000)}}"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch(id, $$"""{"title":"{{new string('t', 201)}}"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch(id, $$"""{"note":"{{new string('n', 10_001)}}"}"""))).StatusCode);
    }

    [Fact]
    public async Task Unknown_session_is_404()
    {
        using var home = new TempHome();
        SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;

        var response = await client.SendAsync(Patch(999, """{"starred":true}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("The session no longer exists.", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
    }

    [Fact]
    public async Task A_locked_journal_is_503_busy_without_writing()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;
        HttpResponseMessage response;
        using (var holder = new SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            holder.Open();
            using var begin = holder.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();

            response = await client.SendAsync(Patch(id, """{"starred":true}"""));

            using var rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("The journal is busy; try again.", (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>())!["error"]);
        Assert.Empty(Annotations(home));
    }

    [Fact]
    public async Task A_journal_migrated_by_a_newer_sqlharness_is_refused_with_409()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (_, client) = dashboard;
        using (var connection = new SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var bump = connection.CreateCommand();
            bump.CommandText = $"PRAGMA user_version = {JournalSchema.CurrentVersion + 1};";
            bump.ExecuteNonQuery();
        }

        var response = await client.SendAsync(Patch(id, """{"starred":true}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("schema", await response.Content.ReadAsStringAsync());
        Assert.Empty(Annotations(home));
    }

    [Fact]
    public async Task Patch_requires_the_write_protections()
    {
        using var home = new TempHome();
        var id = SeedSession(home);
        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
        var (server, client) = dashboard;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Patch(id, """{"starred":true}""", guarded: false))).StatusCode);
        var foreign = Patch(id, """{"starred":true}""");
        foreign.Headers.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Patch(id, """{"starred":true}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/sessions/{id}") { Content = JsonContent.Create(new { }) })).StatusCode);
        Assert.Empty(Annotations(home));
    }
}
