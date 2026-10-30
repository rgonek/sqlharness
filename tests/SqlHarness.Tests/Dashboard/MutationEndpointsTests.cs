using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using SqlHarness.Core;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Mutations;
using SqlHarness.Core.Targets;
using SqlHarness.Dashboard;
using SqlHarness.Dashboard.Mutations;

namespace SqlHarness.Tests.Dashboard;

public sealed class MutationEndpointsTests
{
    private static System.Text.Json.Nodes.JsonObject Wire(MutationRequest request)
    {
        var result = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        result["preparationToken"] = request.PreparationToken;
        return result;
    }

    private static readonly string PreparationToken = new('A', 64);
    private static void MarkPrepared(IActivityJournal journal, JournalHandle handle, MutationRequest request) =>
        Assert.True(journal.Complete(handle, new OperationEnd("succeeded", 0, null, 1, null, null, null, null, null,
            MutationPreparationDigest: request.Digest, MutationPreparationTokenHash: Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PreparationToken))))));

    [Fact]
    public async Task Inherited_production_yolo_without_acknowledgement_is_rejected_at_submission()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        File.WriteAllText(Path.Combine(home.Path, "config.json"), """{ "mutations": { "defaultMode": "yolo" } }""");
        File.WriteAllText(Path.Combine(home.Path, "targets.json"), """
            {"app":{"server":"srv","database":"db","auth":"integrated","engine":"sqlserver","vars":{},"environment":"production","mutations":{"writer":{"sameAsReader":true}}}}
            """);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var response = await client.PostAsJsonAsync("/api/mutations", Wire(Request()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("production_yolo_acknowledgement_required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http_broker_transports_token_while_normal_serialization_omits_it()
    {
        using var home = new TempHome();
        var token = DashboardToken.LoadOrCreate(home.Path);
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var current = ProcessInfo.Current.Get(Environment.ProcessId);
        held.Publish(new DashboardEndpoint(Environment.ProcessId, current?.StartedAt, 47899, token));
        var handler = new CaptureCorrelationHandler();
        var client = new HttpMutationBrokerClient(() => new SqlHarnessConfig(), home.Path, ProcessInfo.Current,
            new RecordingLauncher(), handlerFactory: () => handler);
        var request = Request() with { OperationRef = "ref", PreparationToken = PreparationToken };
        Assert.DoesNotContain(PreparationToken, JsonSerializer.Serialize(request));
        Assert.DoesNotContain(PreparationToken, request.ToString());
        await client.SubmitAsync(request, null, CancellationToken.None);
        Assert.True(handler.CorrelationPresent);
    }

    [Fact]
    public async Task Real_preparation_binds_http_request_and_rejects_mismatch_and_replay()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        File.WriteAllText(Path.Combine(home.Path, "targets.json"), """
            {"app":{"server":"srv","database":"db","auth":"integrated","engine":"sqlserver","vars":{},"mutations":{"mode":"yolo","writer":{"sameAsReader":true}}}}
            """);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var session = new SessionIdentity("prepare-evidence", "test", "test", JournalTransport.Cli, null, null, null, null, null, Environment.ProcessId, null, null);
        var module = new JournalingModule(new SqlHarnessModule(() => ProfileStore.Load(Path.Combine(home.Path, "targets.json"))), () => journal, () => session);
        var operation = new SqlHarnessMutationOperation(new SqlTargetRequest("app", new Dictionary<string, string>()),
            "UPDATE dbo.T SET x = 1", [], 30, 10, "db")
        { SessionKey = "session" };
        var preparedOutcome = await module.ExecuteAsync(operation);
        var prepared = Assert.IsType<SqlHarnessMutationPrepared>(preparedOutcome.Report);
        Assert.NotNull(prepared.Request.PreparationToken);
        Assert.DoesNotContain(prepared.Request.PreparationToken, JsonSerializer.Serialize(prepared));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT mutation_preparation_token_hash FROM operations WHERE operation_ref = $ref";
            command.Parameters.AddWithValue("$ref", preparedOutcome.OperationRef!);
            Assert.NotEqual(prepared.Request.PreparationToken, command.ExecuteScalar());
        }
        var correlation = prepared.Request with { OperationRef = preparedOutcome.OperationRef };
        var otherRequest = Request(sql: "UPDATE dbo.T SET x = 2") with { OperationRef = correlation.OperationRef, PreparationToken = correlation.PreparationToken };
        using var mismatched = await client.PostAsJsonAsync("/api/mutations", Wire(otherRequest));
        Assert.Equal(HttpStatusCode.OK, mismatched.StatusCode);
        var reader = new JournalReader(home.DatabasePath, ProcessInfo.Current);
        var operationId = reader.Operations(new OperationQuery()).Items.Single().Id;
        Assert.Null(reader.Operation(operationId)!.MutationRequest);
        using var matched = await client.PostAsJsonAsync("/api/mutations", Wire(correlation));
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        var detail = reader.Operation(operationId)!;
        Assert.NotNull(detail.MutationRequest);
        Assert.Equal(1, detail.Operation.RecordsAffected);
        var requestId = detail.MutationRequest!.RequestId;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={home.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM mutation_requests WHERE request_id = $id";
            command.Parameters.AddWithValue("$id", requestId);
            command.ExecuteNonQuery();
        }
        using var replay = await client.PostAsJsonAsync("/api/mutations", Wire(correlation));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Null(reader.Operation(operationId)!.MutationRequest);
    }

    [Fact]
    public async Task Dry_run_reference_cannot_be_used_for_approval_evidence()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        File.WriteAllText(Path.Combine(home.Path, "targets.json"), """
            {"app":{"server":"srv","database":"db","auth":"integrated","engine":"sqlserver","vars":{},"mutations":{"mode":"yolo","writer":{"sameAsReader":true}}}}
            """);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var session = new SessionIdentity("dry-evidence", "test", "test", JournalTransport.Cli, null, null, null, null, null, Environment.ProcessId, null, null);
        var module = new JournalingModule(new SqlHarnessModule(() => ProfileStore.Load(Path.Combine(home.Path, "targets.json"))), () => journal, () => session);
        var outcome = await module.ExecuteAsync(new SqlHarnessMutationOperation(new SqlTargetRequest("app", new Dictionary<string, string>()),
            "UPDATE dbo.T SET x = 1", [], 30, 10, "db")
        { SessionKey = "session", DryRun = true });
        using var response = await client.PostAsJsonAsync("/api/mutations", Wire(Request() with { OperationRef = outcome.OperationRef, PreparationToken = PreparationToken }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reader = new JournalReader(home.DatabasePath, ProcessInfo.Current);
        var detail = reader.Operation(reader.Operations(new OperationQuery()).Items.Single().Id)!;
        Assert.True(detail.Operation.DryRun);
        Assert.Null(detail.MutationRequest);
        Assert.Null(detail.Operation.RecordsAffected);
    }

    [Fact]
    public async Task Automatic_execution_records_provider_total_without_operator_verification()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        File.WriteAllText(Path.Combine(home.Path, "targets.json"), """
            {"app":{"server":"srv","database":"db","auth":"integrated","engine":"sqlserver","vars":{},"mutations":{"mode":"yolo","writer":{"sameAsReader":true}}}}
            """);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var session = new SessionIdentity("session-auto", "test", "test", JournalTransport.Cli, null, null, null, null, null, Environment.ProcessId, null, null);
        var handle = journal.Begin(session, new OperationStart("mutation", "app", null, true, null, null, null, null, OperationRef: "ref-auto"))!;
        MarkPrepared(journal, handle, Request());
        using var response = await client.PostAsJsonAsync("/api/mutations", Wire(Request() with { OperationRef = "ref-auto", PreparationToken = PreparationToken }));
        var detail = new JournalReader(home.DatabasePath, ProcessInfo.Current).Operation(handle.OperationId)!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("succeeded", detail.MutationRequest!.State);
        Assert.Null(detail.MutationRequest.Verification);
        Assert.Equal(1, detail.Operation.RecordsAffected);
    }

    [Fact]
    public async Task Operator_result_and_joined_request_have_only_canonical_evidence()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var session = new SessionIdentity("session-for-evidence", "test", "test", JournalTransport.Cli, null, null, null, null, null, Environment.ProcessId, null, null);
        var first = journal.Begin(session, new OperationStart("mutation", "app", null, true, null, null, null, null, OperationRef: "ref-first"))!;
        var joined = journal.Begin(session, new OperationStart("mutation", "app", null, true, null, null, null, null, OperationRef: "ref-joined"))!;
        MarkPrepared(journal, first, Request(sql: "UPDATE dbo.T SET x = 1; UPDATE dbo.T SET x = 2"));
        MarkPrepared(journal, joined, Request(sql: "UPDATE dbo.T SET x = 1; UPDATE dbo.T SET x = 2"));
        var firstSubmit = client.PostAsJsonAsync("/api/mutations", Wire(Request(sql: "UPDATE dbo.T SET x = 1; UPDATE dbo.T SET x = 2") with { OperationRef = "ref-first", PreparationToken = PreparationToken }));
        var requestId = await WaitForRequestId(client);
        var secondSubmit = client.PostAsJsonAsync("/api/mutations", Wire(Request(sql: "UPDATE dbo.T SET x = 1; UPDATE dbo.T SET x = 2") with { OperationRef = "ref-joined", PreparationToken = PreparationToken }));
        await WaitForRequestCount(client, 2);
        AddWriteHeaders(client, server.Port);
        using var approval = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        using var firstResponse = await firstSubmit;
        using var secondResponse = await secondSubmit;
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var reader = new JournalReader(home.DatabasePath, ProcessInfo.Current);
        var firstDetail = reader.Operation(first.OperationId)!;
        var joinedDetail = reader.Operation(joined.OperationId)!;
        Assert.Equal(1, firstDetail.Operation.RecordsAffected); // provider's batch total, not per statement
        Assert.Equal(requestId, firstDetail.MutationRequest!.RequestId);
        Assert.Equal("click", firstDetail.MutationRequest.Verification);
        Assert.Null(joinedDetail.MutationRequest);
        Assert.Null(joinedDetail.Operation.RecordsAffected);
    }

    [Fact]
    public async Task Denied_request_keeps_recorded_decision_without_affected_rows()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var journal = ActivityJournal.Open(home.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var session = new SessionIdentity("session-denied", "test", "test", JournalTransport.Cli, null, null, null, null, null, Environment.ProcessId, null, null);
        var handle = journal.Begin(session, new OperationStart("mutation", "app", null, true, null, null, null, null, OperationRef: "ref-denied"))!;
        MarkPrepared(journal, handle, Request());
        var submission = client.PostAsJsonAsync("/api/mutations", Wire(Request() with { OperationRef = "ref-denied", PreparationToken = PreparationToken }));
        var requestId = await WaitForRequestId(client);
        AddWriteHeaders(client, server.Port);
        using var denial = await client.PostAsJsonAsync($"/api/mutations/{requestId}/deny", new { });
        using var response = await submission;
        var detail = new JournalReader(home.DatabasePath, ProcessInfo.Current).Operation(handle.OperationId)!;
        Assert.Equal("denied", detail.MutationRequest!.State);
        Assert.Equal("click", detail.MutationRequest.Verification);
        Assert.Null(detail.Operation.RecordsAffected);
    }

    [Fact]
    public async Task Mutation_submit_requires_bearer_authentication()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(
            new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient { BaseAddress = server.BaseUri };

        using var response = await client.PostAsJsonAsync("/api/mutations", Request());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Client_without_running_dashboard_and_autostart_disabled_returns_unavailable()
    {
        using var home = new TempHome();
        var client = new HttpMutationBrokerClient(
            () => new SqlHarnessConfig(), home.Path, new FakeProcesses(), new RecordingLauncher());

        var result = await client.SubmitAsync(Request(), null, CancellationToken.None);

        Assert.Equal(MutationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Client_refused_before_receiving_a_response_returns_unavailable()
    {
        using var home = new TempHome();
        var token = DashboardToken.LoadOrCreate(home.Path);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var current = ProcessInfo.Current.Get(Environment.ProcessId);
        held.Publish(new DashboardEndpoint(Environment.ProcessId, current?.StartedAt, port, token));
        var client = new HttpMutationBrokerClient(
            () => new SqlHarnessConfig(), home.Path, ProcessInfo.Current, new RecordingLauncher());

        var result = await client.SubmitAsync(Request(), null, CancellationToken.None);

        Assert.Equal(MutationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Client_reports_unknown_when_the_connection_drops_after_body_send()
    {
        using var home = new TempHome();
        var token = DashboardToken.LoadOrCreate(home.Path);
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var current = ProcessInfo.Current.Get(Environment.ProcessId);
        held.Publish(new DashboardEndpoint(Environment.ProcessId, current?.StartedAt, 47899, token));
        var handler = new DropAfterBodyHandler();
        var client = new HttpMutationBrokerClient(
            () => new SqlHarnessConfig(), home.Path, ProcessInfo.Current, new RecordingLauncher(),
            handlerFactory: () => handler);

        var result = await client.SubmitAsync(Request(), null, CancellationToken.None);

        Assert.Equal(MutationStatus.OutcomeUnknown, result.Status);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(MutationStatus.DatabaseMismatch)]
    [InlineData(MutationStatus.NotAMutation)]
    public async Task Client_preserves_preflight_rejection_status(string status)
    {
        using var home = new TempHome();
        var token = DashboardToken.LoadOrCreate(home.Path);
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var current = ProcessInfo.Current.Get(Environment.ProcessId);
        held.Publish(new DashboardEndpoint(Environment.ProcessId, current?.StartedAt, 47899, token));
        var client = new HttpMutationBrokerClient(
            () => new SqlHarnessConfig(), home.Path, ProcessInfo.Current, new RecordingLauncher(),
            handlerFactory: () => new StatusResponseHandler(status));

        var result = await client.SubmitAsync(Request(), null, CancellationToken.None);

        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task Mutation_submit_rejects_a_database_that_differs_from_server_resolution()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, _) = dashboard;
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        using var response = await client.PostAsJsonAsync("/api/mutations", Request(database: "wrong-db"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<StatusErrorBody>())!;
        Assert.Equal(MutationStatus.DatabaseMismatch, error.Status);
        Assert.Equal(MutationStatus.DatabaseMismatch, error.Error);
    }

    [Fact]
    public async Task Mutation_submit_returns_a_status_envelope_when_the_body_is_not_a_mutation()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, _) = dashboard;
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        using var response = await client.PostAsJsonAsync("/api/mutations", Request(sql: "SELECT 1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<StatusErrorBody>())!;
        Assert.Equal(MutationStatus.NotAMutation, error.Status);
        Assert.Equal(MutationStatus.NotAMutation, error.Error);
    }

    [Fact]
    public async Task Mutation_submit_rejects_body_fields_that_do_not_match_the_server_prepared_digest()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, _) = dashboard;
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        using var response = await client.PostAsJsonAsync("/api/mutations", Request(grantees: ["forged-principal"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request_mismatch", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Mutation_submit_rejects_unsupported_verification_configuration()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        File.WriteAllText(Path.Combine(home.Path, "config.json"), """
            {"mutations":{"defaultMode":"operator","verification":"webauthn","approvalTimeoutSeconds":30},"journal":{"enabled":true,"storeSensitive":false}}
            """);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, _) = dashboard;
        using var client = new HttpClient { BaseAddress = server.BaseUri };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        using var response = await client.PostAsJsonAsync("/api/mutations", Request());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<StatusErrorBody>())!;
        Assert.Equal(MutationStatus.Unavailable, error.Status);
        Assert.Contains("click is the only supported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_rejects_a_verification_configuration_that_became_invalid()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var cancellation = new CancellationTokenSource();
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request(), cancellation.Token);
        var requestId = await WaitForRequestId(client);
        File.WriteAllText(Path.Combine(home.Path, "config.json"), """
            {"mutations":{"defaultMode":"operator","verification":"webauthn","approvalTimeoutSeconds":30},"journal":{"enabled":true,"storeSensitive":false}}
            """);
        AddWriteHeaders(client, server.Port);

        using var response = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MutationStatus.Unavailable, (await response.Content.ReadFromJsonAsync<StatusErrorBody>())!.Status);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitTask);
    }

    [Fact]
    public async Task Approve_requires_the_guarded_write_header_even_with_a_dashboard_cookie()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var cancellation = new CancellationTokenSource();
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request(), cancellation.Token);
        var requestId = await WaitForRequestId(client);

        using var response = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitTask);
    }

    [Fact]
    public async Task Approve_completes_the_long_poll_and_a_second_approval_conflicts()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request());
        var requestId = await WaitForRequestId(client);
        AddWriteHeaders(client, server.Port);

        using var first = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        using var second = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        using var result = await submitTask;

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(MutationStatus.Success, (await result.Content.ReadFromJsonAsync<MutationBrokerResult>())!.Status);
    }

    [Fact]
    public async Task Events_stream_sends_added_and_removed_after_its_snapshot()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var sse = await client.GetAsync("/api/mutations/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await sse.Content.ReadAsStreamAsync());
        Assert.Equal("snapshot", await ReadEventName(reader));

        var submitTask = client.PostAsJsonAsync("/api/mutations", Request());
        var added = await ReadSseEvent(reader);
        var requestId = added.Data.GetProperty("requestId").GetString()!;
        AddWriteHeaders(client, server.Port);
        using var approval = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        var removed = await ReadSseEvent(reader);
        using var result = await submitTask;

        Assert.Equal("added", added.Name);
        Assert.Equal("removed", removed.Name);
        Assert.Equal(requestId, removed.Data.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Events_stream_snapshot_includes_requests_created_before_subscription()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request());
        var requestId = await WaitForRequestId(client);
        using var sse = await client.GetAsync("/api/mutations/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await sse.Content.ReadAsStreamAsync());
        var snapshot = await ReadSseEvent(reader);

        Assert.Equal("snapshot", snapshot.Name);
        Assert.Equal(requestId, snapshot.Data[0].GetProperty("requestId").GetString());

        AddWriteHeaders(client, server.Port);
        using var approval = await client.PostAsJsonAsync($"/api/mutations/{requestId}/approve", new { });
        var removed = await ReadSseEvent(reader);
        using var result = await submitTask;
        Assert.Equal("removed", removed.Name);
    }

    [Fact]
    public async Task Aborting_a_pending_submit_cancels_the_broker_request()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        await using var dashboard = await StartMutationDashboard(home);
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var cancellation = new CancellationTokenSource();
        var submitTask = client.PostAsJsonAsync("/api/mutations", Request(), cancellation.Token);
        var requestId = await WaitForRequestId(client);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitTask);

        Assert.Equal("cancelled", await WaitForRequestState(client, requestId));
    }

    [Fact]
    public async Task Cancellation_after_journaling_but_before_broker_ownership_terminalizes_the_row()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        using var cancellation = new CancellationTokenSource();
        var executorCalls = 0;
        await using var dashboard = await StartMutationDashboard(home,
            afterPrepared: cancellation.Cancel, onExecute: () => Interlocked.Increment(ref executorCalls));
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);

        var submitTask = client.PostAsJsonAsync("/api/mutations", Request(), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitTask);
        var requestId = await WaitForRequestId(client);

        Assert.Equal("cancelled", await WaitForRequestState(client, requestId));
        Assert.Equal(0, Volatile.Read(ref executorCalls));
    }

    [Fact]
    public async Task Cancelling_a_joined_submit_terminalizes_only_its_journal_row()
    {
        using var home = new TempHome();
        ConfigureHome(home);
        var executorCalls = 0;
        await using var dashboard = await StartMutationDashboard(home, onExecute: () => Interlocked.Increment(ref executorCalls));
        var (server, client) = dashboard;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.Token);
        using var firstCancellation = new CancellationTokenSource();
        var firstSubmit = client.PostAsJsonAsync("/api/mutations", Request(), firstCancellation.Token);
        var firstId = await WaitForRequestId(client);

        using var joinedCancellation = new CancellationTokenSource();
        var joinedSubmit = client.PostAsJsonAsync("/api/mutations", Request(), joinedCancellation.Token);
        var requestIds = await WaitForRequestCount(client, 2);
        var joinedId = Assert.Single(requestIds, requestId => requestId != firstId);
        joinedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joinedSubmit);

        Assert.Equal("cancelled", await WaitForRequestState(client, joinedId));
        Assert.Equal("pending", await ReadRequestState(client, firstId));
        Assert.Equal(0, Volatile.Read(ref executorCalls));
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstSubmit);
        Assert.Equal("cancelled", await WaitForRequestState(client, firstId));
    }

    private static MutationRequest Request(string database = "db", IReadOnlyList<string>? grantees = null, string sql = "UPDATE dbo.T SET x = 1") => new(
        "app", new Dictionary<string, string>(), "sqlserver", "srv", database, sql,
        [], 30, 10, "dml", grantees ?? [], "session");

    private static void ConfigureHome(TempHome home)
    {
        File.WriteAllText(Path.Combine(home.Path, "targets.json"), """
            {"app":{"server":"srv","database":"db","auth":"integrated","engine":"sqlserver","vars":{},"mutations":{"mode":"operator","writer":{"sameAsReader":true}}}}
            """);
        File.WriteAllText(Path.Combine(home.Path, "config.json"), """
            {"mutations":{"defaultMode":"operator","approvalTimeoutSeconds":30},"journal":{"enabled":true,"storeSensitive":false}}
            """);
    }

    private static async Task<DashboardServerTests.AuthenticatedDashboard> StartMutationDashboard(TempHome home,
        Action? afterPrepared = null, Action? onExecute = null)
    {
        var profiles = () => ProfileStore.Load(Path.Combine(home.Path, "targets.json"));
        var module = new TestMutationModule(new SqlHarnessModule(profiles), afterPrepared, onExecute);
        var config = SqlHarnessConfigLoader.Load(Path.Combine(home.Path, "config.json")).Config;
        var journal = ActivityJournal.Open(home.DatabasePath, config.Journal, TextWriter.Null, TimeProvider.System);
        return await DashboardServerTests.StartAuthenticated(home, options => options with
        {
            MutationModule = module,
            MutationJournal = journal,
            ConfigPath = Path.Combine(home.Path, "config.json"),
            TargetsPath = Path.Combine(home.Path, "targets.json"),
        });
    }

    private static async Task<string> WaitForRequestId(HttpClient client)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await client.GetAsync("/api/mutations?limit=1");
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = page.RootElement.GetProperty("items");
            if (items.GetArrayLength() != 0)
                return items[0].GetProperty("requestId").GetString()!;
            await Task.Delay(10);
        }
        throw new TimeoutException("The pending mutation was not recorded.");
    }

    private static async Task<string> WaitForRequestState(HttpClient client, string requestId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var state = await ReadRequestState(client, requestId);
            if (state != "pending") return state;
            await Task.Delay(10);
        }
        throw new TimeoutException("The mutation lifecycle did not reach a terminal state.");
    }

    private static async Task<string> ReadRequestState(HttpClient client, string requestId)
    {
        using var response = await client.GetAsync("/api/mutations?limit=20");
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = page.RootElement.GetProperty("items").EnumerateArray()
            .FirstOrDefault(value => value.GetProperty("requestId").GetString() == requestId);
        return item.ValueKind == JsonValueKind.Undefined
            ? throw new TimeoutException("The mutation journal row was not recorded.")
            : item.GetProperty("state").GetString()!;
    }

    private static async Task<IReadOnlyList<string>> WaitForRequestCount(HttpClient client, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await client.GetAsync("/api/mutations?limit=20");
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ids = page.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("requestId").GetString()!).ToArray();
            if (ids.Length >= count) return ids;
            await Task.Delay(10);
        }
        throw new TimeoutException("The expected mutation journal rows were not recorded.");
    }

    private static async Task<string> ReadEventName(StreamReader reader)
    {
        var item = await ReadSseEvent(reader);
        return item.Name;
    }

    private static async Task<(string Name, JsonElement Data)> ReadSseEvent(StreamReader reader)
    {
        string? name = null;
        string? data = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0 && name is not null && data is not null)
                return (name, JsonDocument.Parse(data).RootElement.Clone());
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }
        throw new EndOfStreamException("The mutation event stream ended.");
    }

    private static void AddWriteHeaders(HttpClient client, int port)
    {
        client.DefaultRequestHeaders.Add("X-SqlHarness-Dashboard", "1");
        client.DefaultRequestHeaders.Add("Origin", $"http://127.0.0.1:{port}");
    }

    private sealed record ErrorBody(string Error);
    private sealed record StatusErrorBody(string Status, string Error, string Message);

    private sealed class TestMutationModule(ISqlHarnessModule core, Action? afterPrepared = null, Action? onExecute = null) : ISqlHarnessModule
    {
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            if (operation is SqlHarnessMutationOperation { Approval: not null })
            {
                onExecute?.Invoke();
                return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, QueryReport(), null));
            }
            return ExecuteCoreAsync(operation, ct);
        }

        private async Task<SqlHarnessOutcome> ExecuteCoreAsync(SqlHarnessOperation operation, CancellationToken ct)
        {
            var outcome = await core.ExecuteAsync(operation, ct);
            if (operation is SqlHarnessMutationOperation { DryRun: false }) afterPrepared?.Invoke();
            return outcome;
        }

        private static SqlHarnessQueryReport QueryReport() => new(
            new SqlHarnessTargetIdentityReport("srv", "db", "srv", "db", "app", "sqlserver"), "update", [], [], 1, 1,
            "hash", new OutputFootprint(0, 0));
    }

    private sealed class RecordingLauncher : IDashboardLauncher
    {
        public DashboardLaunchIdentity Launch(DashboardLaunchCommand command, string workingDirectory) =>
            throw new InvalidOperationException("Autostart must be disabled in this test.");
    }

    private sealed class DropAfterBodyHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            _ = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            throw new HttpRequestException("connection dropped");
        }
    }

    private sealed class CaptureCorrelationHandler : HttpMessageHandler
    {
        public bool CorrelationPresent { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            CorrelationPresent = document.RootElement.GetProperty("preparationToken").GetString() == PreparationToken;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new MutationBrokerResult(MutationStatus.Success, null, null)) };
        }
    }

    private sealed class StatusResponseHandler(string status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { status, error = status }),
            });
    }
}