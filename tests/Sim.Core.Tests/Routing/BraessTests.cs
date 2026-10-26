using System;
using Sim.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Routing;

/// <summary>
/// Парадокс Брайеса на синтетике (zoning-demand.md 10.3, Z8a): маршруты по загрузке (ADR-0006), A/B без перемычки и с ней на одном
/// спросе (OD S→T, 120 машин в обороте) и seed. Окно замера - поездки, начатые после 6000-го тика (5 мин после рампы), прогон 36000 тиков.
/// </summary>
public sealed class BraessTests
{
    private const int Vehicles = 120;
    private const int Ticks = 36000;
    private const int Warmup = 6000;

    private readonly ITestOutputHelper _out;

    public BraessTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Fixture_ShortRun_CompletesTripsAndUsesBridge()
    {
        BraessResult a = Braess.Run(false, 1, Vehicles, 15000, 1500);
        BraessResult b = Braess.Run(true, 1, Vehicles, 15000, 1500);
        Assert.True(a.Trips > 10, a.ToString());
        Assert.True(b.Trips > 10, b.ToString());
        Assert.Equal(0, a.BridgeTrips);
        Assert.True(b.BridgeTrips > 0, b.ToString());
        Assert.Equal(0, a.Stuck);
        Assert.Equal(0, b.Stuck);
    }

    [Fact]
    [Trait("Category", "Long")]
    public void Braess_BridgeTakenByShareOfTrips_AndMeanTimeGrows()
    {
        var shares = new double[3];
        var growth = new double[3];
        for (int k = 0; k < 3; k++)
        {
            ulong seed = (ulong)(k + 1);
            BraessResult a = Braess.Run(false, seed, Vehicles, Ticks, Warmup);
            BraessResult b = Braess.Run(true, seed, Vehicles, Ticks, Warmup);
            shares[k] = b.BridgeShare;
            growth[k] = (b.MeanTravelS / a.MeanTravelS) - 1;
            _out.WriteLine($"seed {seed}: A [{a}]\n         B [{b}]\n         dT {growth[k]:P1}");
            Assert.Equal(0, a.Stuck);
            Assert.Equal(0, b.Stuck);
        }

        Array.Sort(shares);
        Array.Sort(growth);
        _out.WriteLine($"median bridge share {shares[1]:P1}, median dT {growth[1]:P1}");
        Assert.True(shares[1] >= 0.20, $"median bridge share {shares[1]:P1} < 20%");
        Assert.True(growth[1] >= 0.03, $"median mean-time growth {growth[1]:P1} < 3%");
    }
}
