using SqlHarness.Core;

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

    private static (JournalingModule Module, JournalTempDirectory Temp) Create(FakeModule inner, bool storeSensitive = false)
    {
        var temp = new JournalTempDirectory();
        var journal = ActivityJournal.Open(temp.DatabasePath, new JournalConfig { StoreSensitive = storeSensitive }, TextWriter.Null, TimeProvider.System);
        return (new JournalingModule(inner, () => journal, () => JournalTestData.Session()), temp);
    }

    [Fact]
    public async Task Successful_operation_is_recorded_and_outcome_is_unchanged()
    {
        var expected = new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null);
        var (module, temp) = Create(new FakeModule((_, _) => Task.FromResult(expected)));
        using (temp)
        {
            var outcome = await module.ExecuteAsync(Query());

            Assert.Same(expected, outcome);
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

        Assert.Same(expected, await module.ExecuteAsync(Query()));
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
    public async Task Journal_and_identity_are_resolved_once_per_module()
    {
        var journalOpens = 0;
        var identities = 0;
        var module = new JournalingModule(
            new FakeModule((_, _) => Task.FromResult(new SqlHarnessOutcome(SqlHarnessExitCode.Success, null, null))),
            () => { Interlocked.Increment(ref journalOpens); return NullActivityJournal.Instance; },
            () => { Interlocked.Increment(ref identities); return JournalTestData.Session(); });

        await module.ExecuteAsync(Query());
        await module.ExecuteAsync(Query());

        Assert.Equal(1, journalOpens);
        Assert.Equal(1, identities);
    }
}