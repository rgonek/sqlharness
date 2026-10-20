using SqlHarness.Core;
using SqlHarness.Core.Journal;
using SqlHarness.Core.Targets;

using OperationPhase = SqlHarness.Core.Journal.OperationPhase;

namespace SqlHarness.Tests.Journal;

public sealed class JournalingModuleTests
{
    private static readonly SqlTargetRequest Target = new("local", new Dictionary<string, string> { ["tenant"] = "acme" });

    private sealed class FakeModule(Func<SqlHarnessOperation, CancellationToken, Task<SqlHarnessOutcome>> behavior) : ISqlHarnessModule
    {
        public int Calls;
        public Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return behavior(operation, ct);
        }
    }

    private static SqlHarnessQueryOperation Query(string sql = "SELECT 1") =>
        new(Target, sql, ["id:nvarchar=SQLH_PARAM_MARKER"], 30, 100, false, null)
        {
            TypedParameters = [new SqlHarnessParameterInput("other", "nvarchar", "SQLH_PARAM_MARKER")],
        };

    private static (JournalingModule Module, JournalTempDirectory Temp) Create(
        FakeModule inner,
        bool storeSensitive = false,
        Func<CancellationToken, string?>? cancelReason = null,
        Func<string?>? rootsJson = null,
        Func<CancellationToken, AgentCallMetadata?>? callMetadata = null,
        DiagnosticsConfig? diagnostics = null)
    {
        var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, TimeProvider.System);
        return (new JournalingModule(inner, () => journal, () => JournalTestData.Session(), cancelReason, rootsJson, callMetadata, diagnostics), temp);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("shutdown")]
    [InlineData("deadline")]
    [InlineData(null)]
    public async Task Thrown_cancellation_records_the_provider_reason(string? reason)
    {
        CancellationToken seen = default;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (module, temp) = Create(
            new FakeModule((_, ct) => Task.FromCanceled<SqlHarnessOutcome>(ct)),
            cancelReason: token => { seen = token; return reason; });
        using (temp)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync(Query(), cts.Token));

            Assert.Equal(cts.Token, seen);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT error_kind, cancel_reason FROM operations").Single();
            Assert.Equal("cancelled", row["error_kind"]);
            Assert.Equal(reason, row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Call_metadata_provider_receives_the_module_token()
    {
        CancellationToken seen = default;
        using var cts = new CancellationTokenSource();
        var (module, temp) = Create(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            callMetadata: token => { seen = token; return new AgentCallMetadata("toolu_1", null, null, null, null, null, null); });
        using (temp)
        {
            await module.ExecuteAsync(Query(), cts.Token);

            Assert.Equal(cts.Token, seen);
            Assert.Equal("toolu_1", JournalDb.Rows(temp.DatabasePath, "SELECT client_call_id FROM operations").Single()["client_call_id"]);
        }
    }

    [Fact]
    public async Task Module_assigns_one_reference_before_journal_and_preserves_it_in_outcome()
    {
        string? receivedRef = null;
        var (module, temp) = Create(new FakeModule((operation, _) =>
        {
            receivedRef = operation.OperationRef;
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        }));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());

            Assert.NotNull(receivedRef);
            Assert.Equal(receivedRef, outcome.OperationRef);
            Assert.Equal(receivedRef, JournalDb.Rows(temp.DatabasePath, "SELECT operation_ref FROM operations").Single()["operation_ref"]);
        }
    }

    [Fact]
    public async Task Call_time_annotation_is_inserted_atomically_with_the_operation_row()
    {
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))));
        using (temp)
        {
            var operation = Query() with
            {
                AgentLabel = "🧪 plan",
                AgentKeep = true,
                OperationAnnotationsEnabled = true,
            };

            var outcome = await module.ExecuteAsync(operation);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT a.agent_label_initial, a.agent_label, a.agent_keep FROM operations o JOIN operation_annotations a ON a.operation_id = o.id").Single();

            Assert.True(outcome.Journaled);
            Assert.Equal("🧪 plan", row["agent_label_initial"]);
            Assert.Equal("🧪 plan", row["agent_label"]);
            Assert.Equal(1L, row["agent_keep"]);
        }
    }

    [Fact]
    public async Task Post_hoc_annotation_call_is_journaled_without_target_or_reference()
    {
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(new SqlHarnessAnnotationOperation(_ =>
                Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, Array.Empty<object>(), null))));

            var row = JournalDb.Rows(temp.DatabasePath,
                "SELECT operation, operation_ref, profile, server, database FROM operations").Single();

            Assert.True(outcome.Journaled);
            Assert.Null(outcome.OperationRef);
            Assert.Equal("annotation", row["operation"]);
            Assert.Null(row["operation_ref"]);
            Assert.Null(row["profile"]);
            Assert.Null(row["server"]);
            Assert.Null(row["database"]);
        }
    }

    [Fact]
    public async Task Dry_run_has_a_journal_row_but_no_agent_annotation_or_ref_eligibility()
    {
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query() with
            {
                DryRun = true,
                AgentLabel = "dry run",
                AgentKeep = true,
                OperationAnnotationsEnabled = true,
            });

            Assert.True(outcome.Journaled);
            Assert.False(outcome.OperationRefEligible);
            Assert.Empty(JournalDb.Rows(temp.DatabasePath, "SELECT operation_id FROM operation_annotations"));
        }
    }

    [Fact]
    public async Task Artifact_read_journals_exact_owner_checked_parent_reference()
    {
        using var artifacts = new JournalTempDirectory();
        var directory = Path.Combine(artifacts.Path, "journal-parent-artifact");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"matrix-cells\"],\"producerOperationRef\":\"root-op-1\"}");
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), "{\"cases\":[]}");

        using var journalTemp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(journalTemp.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var module = new JournalingModule(new SqlHarnessModule(), () => journal, () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(new SqlHarnessArtifactReadOperation(
            artifacts.Path, "journal-parent-artifact", "matrix-cells"));

        Assert.Equal(SqlHarnessExitCode.Success, outcome.ExitCode);
        Assert.NotNull(outcome.OperationRef);
        Assert.Equal("root-op-1", outcome.ParentOperationRef);
        var row = JournalDb.Rows(journalTemp.DatabasePath,
            "SELECT operation, operation_ref, parent_operation_ref FROM operations").Single();
        Assert.Equal("artifact", row["operation"]);
        Assert.Equal(outcome.OperationRef, row["operation_ref"]);
        Assert.Equal("root-op-1", row["parent_operation_ref"]);
    }

    [Fact]
    public async Task Dry_run_metadata_is_stored_without_persisting_sql_by_default()
    {
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))));
        using (temp)
        {
            await module.ExecuteAsync(Query() with { DryRun = true });

            var row = JournalDb.Rows(temp.DatabasePath, "SELECT dry_run, sql_text FROM operations").Single();
            Assert.Equal(1L, row["dry_run"]);
            Assert.Null(row["sql_text"]);
        }
    }

    [Fact]
    public async Task Disabled_journal_still_passes_generated_reference_to_inner_operation()
    {
        string? receivedRef = null;
        var fake = new FakeModule((operation, _) =>
        {
            receivedRef = operation.OperationRef;
            return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
        });
        var module = new JournalingModule(fake, () => NullActivityJournal.Instance, () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(Query());

        Assert.Equal(1, fake.Calls);
        Assert.NotNull(receivedRef);
        Assert.Equal(receivedRef, outcome.OperationRef);
    }

    [Fact]
    public async Task Disabled_journal_real_module_publishes_and_reads_artifact_with_producer_reference()
    {
        using var artifacts = new JournalTempDirectory();
        var profiles = new Dictionary<string, TargetProfile>
        {
            ["test"] = new("test-server", "testdb-{env}", new Dictionary<string, string> { ["env"] = "^a$" }, "integrated"),
        };
        var core = new SqlHarnessModule(
            SqlHarnessCompareTests.FakeCompareSession.Create(plan: PlanMetricsExtractorTests.ActualPlan),
            new CompareArtifactWriter(artifacts.Path, () => DateTimeOffset.UtcNow),
            () => profiles);
        var module = new JournalingModule(core, () => NullActivityJournal.Instance, () => JournalTestData.Session());
        var producer = new SqlHarnessCompareOperation(
            new SqlTargetRequest("test", new Dictionary<string, string> { ["env"] = "a" }),
            "SELECT Id INTO #ids FROM dbo.Clients",
            "SELECT Value FROM dbo.Clients",
            "SELECT Value FROM dbo.Clients -- candidate",
            [], 30, 1)
        {
            OperationRef = "disabled-journal-producer",
            Context = new CompareContext("journal-test", "whole"),
        };

        var published = await module.ExecuteAsync(producer);
        Assert.True(published.ExitCode == SqlHarnessExitCode.Success, published.SafeError);
        var comparison = Assert.IsType<SqlHarnessCompareReport>(published.Report);
        Assert.Equal("disabled-journal-producer", published.OperationRef);
        var artifactDirectory = Assert.IsType<string>(comparison.ArtifactDirectory);
        Assert.True(File.Exists(Path.Combine(artifactDirectory, "manifest.json")));
        var artifactId = Path.GetFileName(artifactDirectory);
        var read = await module.ExecuteAsync(new SqlHarnessArtifactReadOperation(
            artifacts.Path, artifactId, "summary"));

        Assert.Equal(SqlHarnessExitCode.Success, read.ExitCode);
        Assert.NotNull(read.OperationRef);
        Assert.Equal("disabled-journal-producer", read.ParentOperationRef);
        Assert.NotNull(read.Report);
    }

    [Fact]
    public async Task Wrong_scope_artifact_read_is_journaled_without_parent_reference()
    {
        using var artifacts = new JournalTempDirectory();
        var directory = Path.Combine(artifacts.Path, "wrong-scope-artifact");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            "{\"manifestVersion\":1,\"artifactKind\":\"compare\",\"reportFile\":\"report.json\",\"sections\":[\"matrix-cells\"],\"producerOperationRef\":\"secret-parent\",\"owner\":{\"profile\":\"owner\",\"vars\":{},\"engine\":\"postgres\",\"server\":\"s\",\"database\":\"d\"}}");
        File.WriteAllText(Path.Combine(directory, "matrix-cells.json"), "{\"cases\":[]}");
        using var journalTemp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(journalTemp.DatabasePath, new JournalConfig(), TextWriter.Null, TimeProvider.System);
        var module = new JournalingModule(new SqlHarnessModule(), () => journal, () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(new SqlHarnessArtifactReadOperation(
            artifacts.Path, "wrong-scope-artifact", "matrix-cells",
            new ArtifactOwner("other", new Dictionary<string, string>(), "postgres", "s", "d")));

        Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
        Assert.Null(outcome.ParentOperationRef);
        var row = JournalDb.Rows(journalTemp.DatabasePath,
            "SELECT status, operation_ref, parent_operation_ref FROM operations").Single();
        Assert.Equal("rejected", row["status"]);
        Assert.Equal(outcome.OperationRef, row["operation_ref"]);
        Assert.Null(row["parent_operation_ref"]);
    }

    [Fact]
    public async Task Throwing_call_metadata_provider_records_the_operation_without_labels()
    {
        var (module, temp) = Create(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            callMetadata: _ => throw new InvalidOperationException("provider"));
        using (temp)
        {
            await module.ExecuteAsync(Query());

            Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT client_call_id FROM operations").Single()["client_call_id"]);
        }
    }

    [Fact]
    public async Task Throwing_reason_provider_records_null_and_rethrows_the_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (module, temp) = Create(
            new FakeModule((_, ct) => Task.FromCanceled<SqlHarnessOutcome>(ct)),
            cancelReason: _ => throw new InvalidOperationException("provider"));
        using (temp)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync(Query(), cts.Token));
            Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT cancel_reason FROM operations").Single()["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Translated_sql_failure_with_cancelled_token_is_recorded_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var failure = new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "The operation was canceled.");
        var (module, temp) = Create(
            new FakeModule(async (_, _) => { await cts.CancelAsync(); return failure; }),
            cancelReason: _ => "client");
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query(), cts.Token);

            Assert.Equal(failure.ExitCode, outcome.ExitCode);
            Assert.NotNull(outcome.OperationRef);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, exit_code, error_kind, cancel_reason FROM operations").Single();
            Assert.Equal("failed", row["status"]);
            Assert.Equal(-1L, row["exit_code"]);
            Assert.Equal("cancelled", row["error_kind"]);
            Assert.Equal("client", row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Rejected_outcome_with_cancelled_token_is_recorded_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var (module, temp) = Create(
            new FakeModule(async (_, _) =>
            {
                await cts.CancelAsync();
                return new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "SQL safety rejection: nope");
            }),
            cancelReason: _ => "shutdown");
        using (temp)
        {
            await module.ExecuteAsync(Query(), cts.Token);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT error_kind, cancel_reason FROM operations").Single();
            Assert.Equal("cancelled", row["error_kind"]);
            Assert.Equal("shutdown", row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Sql_failure_without_cancellation_stays_an_sql_failure()
    {
        var (module, temp) = Create(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "boom"))),
            cancelReason: _ => "client");
        using (temp)
        {
            await module.ExecuteAsync(Query(), CancellationToken.None);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT exit_code, error_kind, cancel_reason FROM operations").Single();
            Assert.Equal(5L, row["exit_code"]);
            Assert.NotEqual("cancelled", row["error_kind"]);
            Assert.Null(row["cancel_reason"]);
        }
    }

    [Theory]
    [InlineData(SqlHarnessExitCode.Success)]
    [InlineData(SqlHarnessExitCode.WatchMaxDuration)]
    [InlineData(SqlHarnessExitCode.SnapshotDifferences)]
    public async Task Controlled_success_with_cancelled_token_is_not_rewritten(SqlHarnessExitCode exitCode)
    {
        using var cts = new CancellationTokenSource();
        var (module, temp) = Create(
            new FakeModule(async (_, _) => { await cts.CancelAsync(); return new SqlHarnessOutcome(exitCode, null, null); }),
            cancelReason: _ => "client");
        using (temp)
        {
            await module.ExecuteAsync(Query(), cts.Token);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, cancel_reason FROM operations").Single();
            Assert.Equal("succeeded", row["status"]);
            Assert.Null(row["cancel_reason"]);
        }
    }

    [Fact]
    public async Task Roots_provider_is_read_at_each_begin()
    {
        var roots = (string?)null;
        var (module, temp) = Create(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            rootsJson: () => roots);
        using (temp)
        {
            await module.ExecuteAsync(Query());
            Assert.Null(JournalDb.Rows(temp.DatabasePath, "SELECT roots_json FROM sessions").Single()["roots_json"]);

            roots = """[{"uri":"file:///w"}]""";
            await module.ExecuteAsync(Query());
            Assert.Equal(roots, JournalDb.Rows(temp.DatabasePath, "SELECT roots_json FROM sessions").Single()["roots_json"]);
        }
    }

    [Fact]
    public async Task Successful_operation_is_recorded_and_outcome_is_unchanged()
    {
        var expected = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var (module, temp) = Create(new FakeModule((_, _) => Task.FromResult(expected)));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());

            Assert.Equal(expected.ExitCode, outcome.ExitCode);
            Assert.NotNull(outcome.OperationRef);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, exit_code, operation FROM operations").Single();
            Assert.Equal("succeeded", row["status"]);
            Assert.Equal("query", row["operation"]);
        }
    }

    [Fact]
    public async Task Rejected_operation_keeps_exit_code_and_is_recorded_as_rejected()
    {
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Safety, null, "SQL safety rejection: nope"))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());

            Assert.Equal(SqlHarnessExitCode.Safety, outcome.ExitCode);
            Assert.Equal("rejected", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
        }
    }

    [Fact]
    public async Task Cancelled_operation_is_completed_as_cancelled_and_rethrown()
    {
        var (module, temp) = Create(new FakeModule((_, ct) => Task.FromCanceled<SqlHarnessOutcome>(new CancellationToken(true))));
        using (temp)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.ExecuteAsync(Query()));

            var row = JournalDb.Rows(temp.DatabasePath, "SELECT status, error_kind FROM operations").Single();
            Assert.Equal("failed", row["status"]);
            Assert.Equal("cancelled", row["error_kind"]);
        }
    }

    [Fact]
    public async Task Unexpected_exception_is_completed_and_rethrown()
    {
        var (module, temp) = Create(new FakeModule((_, _) => throw new InvalidOperationException("boom")));
        using (temp)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => module.ExecuteAsync(Query()));

            Assert.Equal("unhandled_exception", JournalDb.Rows(temp.DatabasePath, "SELECT error_kind FROM operations").Single()["error_kind"]);
        }
    }

    [Fact]
    public async Task Journal_or_identity_failure_never_changes_the_outcome()
    {
        var expected = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(expected)),
            () => throw new IOException("disk"),
            () => throw new UnauthorizedAccessException());

        var outcome = await module.ExecuteAsync(Query());
        Assert.Equal(expected.ExitCode, outcome.ExitCode);
        Assert.NotNull(outcome.OperationRef);
    }

    [Fact]
    public async Task Emission_receipt_is_wrapped_and_records_tokens()
    {
        var innerCompleted = 0;
        var receipt = new SqlHarnessEmissionReceipt((_, _) =>
        {
            Interlocked.Increment(ref innerCompleted);
            return Task.FromResult(SqlHarnessExitCode.Success);
        })
        {
            RawFootprint = new OutputFootprint(800, 20),
        };
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());
            var exit = await outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(80, 4));

            Assert.Equal(SqlHarnessExitCode.Success, exit);
            Assert.Equal(1, innerCompleted);
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT raw_tokens, emitted_tokens FROM operations").Single();
            Assert.Equal(200L, row["raw_tokens"]);
            Assert.Equal(20L, row["emitted_tokens"]);
        }
    }

    [Fact]
    public async Task Sensitivity_gate_holds_in_raw_database_bytes()
    {
        var ok = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var (hashOnly, hashTemp) = Create(new FakeModule((_, _) => Task.FromResult(ok)));
        var (sensitive, sensitiveTemp) = Create(new FakeModule((_, _) => Task.FromResult(ok)), storeSensitive: true);
        using (hashTemp)
        using (sensitiveTemp)
        {
            await hashOnly.ExecuteAsync(Query("SELECT 'SQLH_SQL_MARKER'"));
            await sensitive.ExecuteAsync(Query("SELECT 'SQLH_SQL_MARKER'"));

            var hashBytes = JournalDb.AllBytes(hashTemp.DatabasePath);
            var sensitiveBytes = JournalDb.AllBytes(sensitiveTemp.DatabasePath);
            Assert.False(JournalDb.Contains(hashBytes, "SQLH_SQL_MARKER"));
            Assert.False(JournalDb.Contains(hashBytes, "SQLH_PARAM_MARKER"));
            Assert.True(JournalDb.Contains(sensitiveBytes, "SQLH_SQL_MARKER"));
            Assert.False(JournalDb.Contains(sensitiveBytes, "SQLH_PARAM_MARKER"));
        }
    }

    [Fact]
    public async Task Journal_is_resolved_once_and_identity_is_refreshed_per_operation()
    {
        var journalOpens = 0;
        var identities = 0;
        var journal = new RecordingJournal();
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            () => { Interlocked.Increment(ref journalOpens); return journal; },
            () => { Interlocked.Increment(ref identities); return JournalTestData.Session(); });

        await module.ExecuteAsync(Query());
        await module.ExecuteAsync(Query());

        Assert.Equal(1, journalOpens);
        Assert.Equal(2, identities);
        Assert.Equal(2, journal.Begins);
    }

    [Fact]
    public async Task Disabled_journal_never_resolves_the_session_identity()
    {
        var identities = 0;
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            () => NullActivityJournal.Instance,
            () => { Interlocked.Increment(ref identities); return JournalTestData.Session(); });

        await module.ExecuteAsync(Query());

        Assert.Equal(0, identities);
    }

    [Fact]
    public async Task Throwing_emission_write_does_not_change_the_receipt_exit_code()
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.LocalStorage))
        {
            RawFootprint = new OutputFootprint(800, 20),
        };
        var journal = new RecordingJournal { ThrowOnEmission = true };
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt))),
            () => journal,
            () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(Query());
        var exit = await outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(80, 4));

        Assert.Equal(SqlHarnessExitCode.LocalStorage, exit);
        Assert.Equal(1, journal.Emissions);
    }

    [Fact]
    public async Task Throwing_complete_does_not_change_outcome_or_exception()
    {
        var expected = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var journal = new RecordingJournal { ThrowOnComplete = true };
        var ok = new JournalingModule(new FakeModule((_, _) => Task.FromResult(expected)), () => journal, () => JournalTestData.Session());
        var crashing = new JournalingModule(
            new FakeModule((_, _) => throw new InvalidOperationException("boom")), () => journal, () => JournalTestData.Session());

        var returned = await ok.ExecuteAsync(Query());
        Assert.Equal(expected.ExitCode, returned.ExitCode);
        Assert.NotNull(returned.OperationRef);
        await Assert.ThrowsAsync<InvalidOperationException>(() => crashing.ExecuteAsync(Query()));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Failed_operations_record_phase_rows_and_capture_events_only_when_enabled(bool captureEvents, int expectedEvents)
    {
        var (module, temp) = Create(new FakeModule(async (_, _) =>
        {
            using var phase = OperationPhaseRecorder.Current!.Start(OperationPhase.Connect);
            phase.Detail("""{"client":"postgres"}""");
            phase.Fail(28_064);
            await Task.Yield();
            throw new InvalidOperationException("primary operation failure");
        }), diagnostics: new DiagnosticsConfig { Enabled = captureEvents });
        using (temp)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => module.ExecuteAsync(Query()));

            Assert.Equal("primary operation failure", error.Message);
            var phases = JournalDb.Rows(temp.DatabasePath,
                "SELECT phase, count, total_ms FROM operation_phases ORDER BY phase");
            Assert.Contains(phases, row => Equals(row["phase"], "connect") && Equals(row["count"], 1L));
            Assert.Contains(phases, row => Equals(row["phase"], "other"));
            Assert.Equal("failed", JournalDb.Rows(temp.DatabasePath, "SELECT status FROM operations").Single()["status"]);
            Assert.Equal((long)expectedEvents, JournalDb.Rows(temp.DatabasePath,
                "SELECT COUNT(*) AS n FROM operation_events").Single()["n"]);
        }
    }

    [Fact]
    public async Task Concurrent_module_calls_keep_their_phase_journals_isolated()
    {
        var (module, temp) = Create(new FakeModule(async (_, _) =>
        {
            using (OperationPhaseRecorder.Current!.Start(OperationPhase.Connect))
                await Task.Delay(5);
            return new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        }), diagnostics: new DiagnosticsConfig { Enabled = true });
        using (temp)
        {
            await Task.WhenAll(module.ExecuteAsync(Query()), module.ExecuteAsync(Query()));

            Assert.Equal(2L, JournalDb.Rows(temp.DatabasePath,
                "SELECT COUNT(*) AS n FROM operation_phases WHERE phase = 'connect'").Single()["n"]);
            Assert.Equal(2L, JournalDb.Rows(temp.DatabasePath,
                "SELECT COUNT(*) AS n FROM operation_events").Single()["n"]);
            Assert.Equal(2L, JournalDb.Rows(temp.DatabasePath,
                "SELECT COUNT(DISTINCT operation_id) AS n FROM operation_phases WHERE phase = 'connect'").Single()["n"]);
        }
    }

    [Fact]
    public async Task Disabled_journal_does_not_open_a_phase_recorder()
    {
        var observedRecorder = true;
        var module = new JournalingModule(
            new FakeModule((_, _) =>
            {
                observedRecorder = OperationPhaseRecorder.Current is not null;
                return Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null));
            }),
            () => NullActivityJournal.Instance,
            () => JournalTestData.Session());

        await module.ExecuteAsync(Query());

        Assert.False(observedRecorder);
    }

    [Fact]
    public async Task Failed_complete_skips_the_emission_write()
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.Success))
        {
            RawFootprint = new OutputFootprint(800, 20),
        };
        var journal = new RecordingJournal { CompleteResult = false };
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt))),
            () => journal,
            () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(Query());
        var exit = await outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(80, 4));

        Assert.Equal(SqlHarnessExitCode.Success, exit);
        Assert.Equal(1, journal.Completes);
        Assert.Equal(0, journal.Emissions);
    }

    [Fact]
    public async Task Raw_tokens_are_recorded_at_completion_without_an_emission()
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.Success))
        {
            RawFootprint = new OutputFootprint(800, 20),
        };
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt))));
        using (temp)
        {
            // Raw tokens are recorded when the operation completes, before MCP builds its response.
            await module.ExecuteAsync(Query());

            var row = JournalDb.Rows(temp.DatabasePath, "SELECT raw_tokens, emitted_tokens FROM operations").Single();
            Assert.Equal(200L, row["raw_tokens"]);
            Assert.Null(row["emitted_tokens"]);
        }
    }

    private sealed class RecordingJournal : IActivityJournal
    {
        public int Begins;
        public int Completes;
        public int Emissions;
        public bool ThrowOnComplete { get; init; }
        public bool ThrowOnEmission { get; init; }
        public bool CompleteResult { get; init; } = true;

        public JournalHandle? Begin(SessionIdentity session, OperationStart start)
        {
            Interlocked.Increment(ref Begins);
            return new JournalHandle(Begins);
        }

        public bool Complete(JournalHandle? handle, OperationEnd end)
        {
            Interlocked.Increment(ref Completes);
            if (ThrowOnComplete)
                throw new IOException("complete");
            return CompleteResult;
        }

        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted)
        {
            Interlocked.Increment(ref Emissions);
            if (ThrowOnEmission)
                throw new IOException("emission");
        }
    }
    [Fact]
    public async Task Receipt_completion_records_the_emitted_footprint_once_and_keeps_the_exit_code()
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.SqlExecution))
        {
            RawFootprint = new OutputFootprint(800, 20),
        };
        var (module, temp) = Create(new FakeModule((_, _) =>
            Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.SqlExecution, null, "failed", receipt))));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(80, 4))));

            Assert.All(results, code => Assert.Equal(SqlHarnessExitCode.SqlExecution, code));
            var row = JournalDb.Rows(temp.DatabasePath, "SELECT raw_bytes, emitted_bytes, emitted_lines, status FROM operations").Single();
            Assert.Equal((800L, 80L, 4L, "failed"), ((long)row["raw_bytes"]!, (long)row["emitted_bytes"]!, (long)row["emitted_lines"]!, (string)row["status"]!));
        }
    }

    [Fact]
    public async Task Emission_write_failure_never_changes_the_exit_code()
    {
        var receipt = new SqlHarnessEmissionReceipt((_, _) => Task.FromResult(SqlHarnessExitCode.Success));
        var journal = new ThrowingEmissionJournal();
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null, receipt))),
            () => journal,
            () => JournalTestData.Session());

        var outcome = await module.ExecuteAsync(Query());

        Assert.Equal(SqlHarnessExitCode.Success, await outcome.EmissionReceipt!.CompleteAsync(new OutputFootprint(8, 1)));
    }

    private sealed class ThrowingEmissionJournal : IActivityJournal
    {
        public JournalHandle? Begin(SessionIdentity session, OperationStart start) => new(1);
        public bool Complete(JournalHandle? handle, OperationEnd end) => true;
        public void RecordEmission(JournalHandle? handle, OutputFootprint? raw, OutputFootprint emitted) =>
            throw new IOException("disk full");
    }
}