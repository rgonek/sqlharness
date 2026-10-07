using SqlHarness.Core;
using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardLockTests
{
    private static readonly DateTimeOffset Started = new(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Second_acquire_fails_while_the_first_is_held()
    {
        using var home = new TempHome();

        using var first = DashboardLock.TryAcquire(home.Path);
        var second = DashboardLock.TryAcquire(home.Path);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void Lock_is_released_on_dispose()
    {
        using var home = new TempHome();

        DashboardLock.TryAcquire(home.Path)!.Dispose();

        using var again = DashboardLock.TryAcquire(home.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public void Published_endpoint_is_read_back_while_its_process_lives()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, 47801, "tok"));

        var running = DashboardLock.ReadRunning(home.Path, new FakeProcesses().Alive(4242, Started.AddMilliseconds(400)));

        Assert.Equal(new DashboardEndpoint(4242, Started, 47801, "tok"), running);
        Assert.Equal(new Uri("http://127.0.0.1:47801/?t=tok"), running!.OpenUri);
    }

    [Fact]
    public void Stale_info_file_is_not_trusted()
    {
        using var home = new TempHome();
        using (var held = DashboardLock.TryAcquire(home.Path)!)
            held.Publish(new DashboardEndpoint(4242, Started, 47801, "tok"));
        File.WriteAllText(Path.Combine(home.Path, "dashboard.json"),
            """{"pid":4242,"startedAt":"2026-10-07T08:00:00+00:00","port":47801,"token":"tok"}""");

        Assert.Null(DashboardLock.ReadRunning(home.Path, new FakeProcesses()));
        Assert.Null(DashboardLock.ReadRunning(home.Path, new FakeProcesses().Alive(4242, Started.AddHours(1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"pid":1,"port":99999,"token":"x"}""")]
    [InlineData("""{"pid":1,"port":47800,"token":""}""")]
    [InlineData("""{"pid":0,"port":47800,"token":"x"}""")]
    [InlineData("""{"pid":-5,"port":47800,"token":"x"}""")]
    public void Malformed_info_file_reads_as_not_running(string content)
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        File.WriteAllText(Path.Combine(home.Path, "dashboard.json"), content);

        Assert.Null(DashboardLock.ReadRunning(home.Path, new FakeProcesses().Alive(1, null)));
    }

    [Fact]
    public void Dispose_removes_the_published_info_file()
    {
        using var home = new TempHome();
        var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(1, null, 47800, "tok"));

        held.Dispose();

        Assert.False(File.Exists(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public async Task Wait_for_running_returns_null_after_the_timeout()
    {
        using var home = new TempHome();

        var endpoint = await DashboardLock.WaitForRunningAsync(home.Path, new FakeProcesses(), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(endpoint);
    }

    [Fact]
    public void Info_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;

        held.Publish(new DashboardEndpoint(1, null, 47800, "tok"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home.Path, "dashboard.json")));
    }

    [Fact]
    public void Held_lock_with_a_dead_publisher_is_not_trusted()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(4242, Started, 47801, "tok"));

        Assert.Null(DashboardLock.ReadRunning(home.Path, new FakeProcesses()));
    }

    [Fact]
    public void Self_only_reader_trusts_the_file_only_while_the_lock_is_held()
    {
        using var home = new TempHome();
        var held = DashboardLock.TryAcquire(home.Path)!;
        held.Publish(new DashboardEndpoint(int.MaxValue, Started, 47801, "tok"));

        Assert.Equal(47801, DashboardLock.ReadRunning(home.Path, new SelfOnlyProcessInfo())?.Port);

        held.Dispose();
        File.WriteAllText(Path.Combine(home.Path, "dashboard.json"),
            """{"pid":2147483647,"port":47801,"token":"tok"}""");
        Assert.Null(DashboardLock.ReadRunning(home.Path, new SelfOnlyProcessInfo()));
    }

    [Fact]
    public void Acquire_removes_an_earlier_info_file()
    {
        using var home = new TempHome();
        var info = Path.Combine(home.Path, "dashboard.json");
        File.WriteAllText(info, """{"pid":4242,"port":47801,"token":"tok"}""");

        using var held = DashboardLock.TryAcquire(home.Path)!;

        Assert.False(File.Exists(info));
    }

    [Fact]
    public async Task Wait_for_running_returns_an_endpoint_published_during_the_wait()
    {
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var processes = new FakeProcesses().Alive(4242, Started);

        var wait = DashboardLock.WaitForRunningAsync(home.Path, processes, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(250);
        held.Publish(new DashboardEndpoint(4242, Started, 47801, "tok"));

        Assert.Equal(new DashboardEndpoint(4242, Started, 47801, "tok"), await wait);
    }

    [Fact]
    public void Endpoint_text_redacts_the_token()
    {
        var text = new DashboardEndpoint(4242, Started, 47801, "secret-token").ToString();

        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.Contains("47801", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_replaces_a_leftover_temp_file_and_stays_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var home = new TempHome();
        using var held = DashboardLock.TryAcquire(home.Path)!;
        var temp = Path.Combine(home.Path, "dashboard.json.tmp");
        File.WriteAllText(temp, "leftover");
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        held.Publish(new DashboardEndpoint(1, null, 47800, "tok"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home.Path, "dashboard.json")));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Lock_file_and_home_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var home = new TempHome();
        File.SetUnixFileMode(home.Path, (UnixFileMode)0b111_101_101);

        using var held = DashboardLock.TryAcquire(home.Path)!;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home.Path, "dashboard.lock")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(home.Path));
    }

    [Fact]
    public void Acquire_keeps_the_lock_when_the_earlier_info_file_cannot_be_deleted()
    {
        using var home = new TempHome();
        var info = Path.Combine(home.Path, "dashboard.json");
        Directory.CreateDirectory(info);
        File.WriteAllText(Path.Combine(info, "blocker"), "x");

        using var held = DashboardLock.TryAcquire(home.Path);

        Assert.NotNull(held);
        Assert.Null(DashboardLock.TryAcquire(home.Path));
    }

    [Fact]
    public async Task Acquire_retries_once_when_a_probe_briefly_holds_the_lock()
    {
        using var home = new TempHome();
        var probe = DashboardLock.TryAcquire(home.Path)!;
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            probe.Dispose();
        });

        using var held = DashboardLock.TryAcquire(home.Path);
        await release;

        Assert.NotNull(held);
    }
}