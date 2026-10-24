using SqlHarness.Core;

namespace SqlHarness.Tests.Journal;

public sealed class StatementReconcilerTests
{
    private static StatementMetrics Statement(int ordinal, params StatementObjectCounters[] objects) =>
        new(ordinal, $"hash-{ordinal}", null, null, null, [])
        {
            Key = $"key-{ordinal}",
            Occurrence = 0,
            Objects = objects,
            CountersComplete = true,
            IdentityComplete = true,
        };

    private static TableIoCounters Io(string table, long reads, long scans = 1, long lobReads = 0) =>
        new(table, scans, reads, 0, 0, 0, lobReads, 0, 0);

    [Fact]
    public void Every_object_reconciles_to_confirmed_statements()
    {
        var statements = new[]
        {
            Statement(0, new StatementObjectCounters("[dbo].[Orders]", "table", 10, 2, 1)),
            Statement(1, new StatementObjectCounters("Customers", "table", 4, 0, 2)),
        };

        Assert.Equal([StatementStatus.Confirmed, StatementStatus.Confirmed],
            StatementReconciler.Reconcile(statements,
            [Io("[dbo].[Orders]", 10, 1, 2), Io("Customers", 4, 2)]));
    }

    [Fact]
    public void Object_mismatch_makes_all_touching_statements_partial_only()
    {
        var statements = new[]
        {
            Statement(0, new StatementObjectCounters("Orders", "table", 10, 0, 1)),
            Statement(1, new StatementObjectCounters("Orders", "table", 3, 0, 1),
                new StatementObjectCounters("Customers", "table", 4, 0, 1)),
            Statement(2, new StatementObjectCounters("Customers", "table", 4, 0, 1)),
        };

        Assert.Equal([StatementStatus.Partial, StatementStatus.Partial, StatementStatus.Confirmed],
            StatementReconciler.Reconcile(statements, [Io("Orders", 16), Io("Customers", 8, 2)]));
    }

    [Fact]
    public void Table_variable_objects_reconcile_by_group_sum()
    {
        var statements = new[]
        {
            Statement(0, new StatementObjectCounters("[@first]", "table-variable", 4, 0, 1)),
            Statement(1, new StatementObjectCounters("[@second]", "table-variable", 3, 1, 2)),
        };

        Assert.Equal([StatementStatus.Confirmed, StatementStatus.Confirmed],
            StatementReconciler.Reconcile(statements, [Io("#A531CF90", 7, 3, 1) with { Identity = StatisticsIoTableIdentity.AnonymousTableVariable }]));
    }

    [Fact]
    public void Explicit_hex_shaped_local_temp_reconciles_by_its_producer_identity()
    {
        var statement = Statement(0, new StatementObjectCounters("[#ABCDEF12]", "table", 7, 1, 2));
        var observed = Io("#ABCDEF12", 7, 2, 1) with
        {
            Identity = StatisticsIoTableIdentity.LocalTemp("#ABCDEF12"),
        };

        Assert.Equal([StatementStatus.Confirmed], StatementReconciler.Reconcile([statement], [observed]));
    }

    [Fact]
    public void Mixed_variable_and_explicit_hex_temp_reconcile_as_separate_producer_identities()
    {
        var statements = new[]
        {
            Statement(0, new StatementObjectCounters("[@v]", "table-variable", 5, 0, 1)),
            Statement(1, new StatementObjectCounters("[#ABCDEF12]", "table", 9, 1, 2)),
        };
        var observed = new[]
        {
            Io("#A1B2C3D4", 5, 1) with { Identity = StatisticsIoTableIdentity.AnonymousTableVariable },
            Io("#ABCDEF12", 9, 2, 1) with { Identity = StatisticsIoTableIdentity.LocalTemp("#ABCDEF12") },
        };

        Assert.Equal([StatementStatus.Confirmed, StatementStatus.Confirmed], StatementReconciler.Reconcile(statements, observed));
    }

    [Fact]
    public void Anonymous_group_does_not_collide_with_real_object_named_table_variables()
    {
        var statements = new[]
        {
            Statement(0, new StatementObjectCounters("[@v]", "table-variable", 9, 0, 1)),
            Statement(1, new StatementObjectCounters("[table variables]", "table", 7, 0, 1)),
        };
        var swappedObserved = new[]
        {
            Io("#A1B2C3D4", 7) with { Identity = StatisticsIoTableIdentity.AnonymousTableVariable },
            Io("table variables", 9),
        };

        Assert.Equal([StatementStatus.Partial, StatementStatus.Partial], StatementReconciler.Reconcile(statements, swappedObserved));
    }

    [Fact]
    public void Unknown_legacy_hash_name_is_not_reclassified_as_a_table_variable()
    {
        var statement = Statement(0, new StatementObjectCounters("[@first]", "table-variable", 7, 1, 3));
        var unknownHash = Io("#A531CF90", 7, 3, 1) with
        {
            Identity = StatisticsIoTableIdentity.Unknown("#A531CF90"),
        };

        Assert.Equal([StatementStatus.Partial], StatementReconciler.Reconcile([statement], [unknownHash]));
    }

    [Fact]
    public void Unattributed_runtime_io_fails_closed_for_retained_statements()
    {
        var statement = Statement(0, new StatementObjectCounters("Orders", "table", 10, 0, 1));

        Assert.Equal([StatementStatus.Partial],
            StatementReconciler.Reconcile([statement], [Io("Orders", 10), Io("Worktable", 1)]));
    }

    [Fact]
    public void Missing_plan_statements_produce_no_confirmed_rows()
    {
        Assert.Empty(StatementReconciler.Reconcile([], [Io("Orders", 10)]));
    }
}