namespace Sim.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreInfo_ApiVersion_IsZeroInScaffold()
    {
        Assert.Equal(0, CoreInfo.ApiVersion);
    }
}
