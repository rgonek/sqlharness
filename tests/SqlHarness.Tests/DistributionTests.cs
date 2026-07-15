using SqlHarness.Core;

namespace SqlHarness.Tests;

public class DistributionTests
{
    [Fact]
    public void Distribution_uses_middle_average_for_even_samples()
    {
        var distribution = Distribution.From([1, 4, 10, 20]);

        Assert.Equal(7, distribution.Median);
        Assert.Equal(1, distribution.Min);
        Assert.Equal(20, distribution.Max);
    }

    [Fact]
    public void Distribution_uses_middle_sample_for_odd_samples()
    {
        var distribution = Distribution.From([9, 1, 4]);

        Assert.Equal(4, distribution.Median);
        Assert.Equal(1, distribution.Min);
        Assert.Equal(9, distribution.Max);
    }
}
