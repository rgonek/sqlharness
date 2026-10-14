namespace SqlHarness.Tests;

public sealed class PlatformFactAttributesTests
{
    [Fact]
    public void Windows_fact_skips_only_off_windows()
    {
        var attribute = new WindowsFactAttribute();

        Assert.Equal(OperatingSystem.IsWindows() ? null : "Requires Windows.", attribute.Skip);
    }

    [Fact]
    public void Unix_fact_skips_only_on_windows()
    {
        var attribute = new UnixFactAttribute();

        Assert.Equal(OperatingSystem.IsWindows() ? "Requires Unix." : null, attribute.Skip);
    }
}