namespace Sim.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreInfo_ApiVersion_IsOne()
    {
        Assert.Equal(1, CoreInfo.ApiVersion);
    }
}
