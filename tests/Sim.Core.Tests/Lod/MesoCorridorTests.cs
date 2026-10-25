using System;
using Sim.Core.Api;
using Sim.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Sim.Core.Tests.LodTests;

/// <summary>
/// Эквивалентность потока (sim-lod.md §7, ADR-0013 п.«критерий»): коридор 3 км с 4 светофорами, спрос 0.5 / 0.9 / 1.2 от пропускной
/// способности, 20 мин игрового времени; эталон - micro (<c>off</c>), сравниваемый режим - <c>all_meso</c> с калиброванными дефолтами
/// (<c>meso_headway_scale</c> 0.8, <c>meso_accel_scale</c> 1.0, итог калибровки - sim-lod.md §9). Допуски: пропускная способность на выходе
/// ±5% (0.5, 0.9) / ±10% (1.2), среднее время проезда ±10%, 85-й перцентиль ±15%, макс. очередь у первого светофора ±20% (но не меньше
/// ±1 машины: очереди малых нагрузок - единицы), stuck и gridlock 0, насыщенный поток полосы на зелёном ±5%.
/// </summary>
public class MesoCorridorTests
{
    private static readonly object _gate = new();
    private static CorridorResult? _overload;
    private static readonly CorridorResult?[] _micro = new CorridorResult?[3];

    private readonly ITestOutputHelper _out;

    public MesoCorridorTests(ITestOutputHelper output) => _out = output;

    private static LodDesc Meso() => new()
    {
        Mode = LodMode.AllMeso,
        CellM = 1000f,
        MicroBudget = 15000,
        FollowSelected = true,
        FocusCommands = true,
    };

    private static CorridorResult Overload()
    {
        lock (_gate)
        {
            return _overload ??= Corridor.Run(default, 1.0, 0);
        }
    }

    private static CorridorResult Micro(int k, double load)
    {
        lock (_gate)
        {
            return _micro[k] ??= Corridor.Run(default, load, Overload().ThroughputPerHour);
        }
    }

    private static void Within(string what, double meso, double micro, double tol, double absSlack = 0)
    {
        double allowed = Math.Max(Math.Abs(micro) * tol, absSlack);
        Assert.True(Math.Abs(meso - micro) <= allowed, $"{what}: meso {meso:F2} vs micro {micro:F2} (tolerance {tol * 100:F0}%, allowed ±{allowed:F2})");
    }

    [Theory]
    [InlineData(0, 0.5, 0.05)]
    [InlineData(1, 0.9, 0.05)]
    [InlineData(2, 1.2, 0.10)]
    public void Corridor_AllMeso_MatchesMicro_WithinTolerances(int k, double load, double throughputTol)
    {
        CorridorResult micro = Micro(k, load);
        CorridorResult meso = Corridor.Run(Meso(), load, Overload().ThroughputPerHour);
        _out.WriteLine($"load {load}: micro {micro}");
        _out.WriteLine($"load {load}: meso  {meso}");
        Assert.Null(micro.Violation);
        Assert.Null(meso.Violation);
        Assert.Equal(0, meso.Stuck);
        Assert.Equal(0, meso.StuckEvents);
        Assert.Equal(0, meso.Gridlock);
        Assert.True(micro.ThroughputPerHour > 500, $"эталон: пропускная способность {micro.ThroughputPerHour}");
        Within("throughput/h", meso.ThroughputPerHour, micro.ThroughputPerHour, throughputTol);
        Within("mean travel s", meso.MeanTravelS, micro.MeanTravelS, 0.10);
        Within("p85 travel s", meso.P85TravelS, micro.P85TravelS, 0.15);
        Within("max queue", meso.MaxQueue, micro.MaxQueue, 0.20, absSlack: 1);
    }

    /// <summary>Перегрузка (спрос выше пропускной способности): пропускная способность коридора и очередь у первого светофора.</summary>
    [Fact]
    public void Corridor_AllMeso_Overload_CapacityMatchesMicro()
    {
        CorridorResult micro = Overload();
        CorridorResult meso = Corridor.Run(Meso(), 1.0, 0);
        _out.WriteLine($"overload: micro {micro}");
        _out.WriteLine($"overload: meso  {meso}");
        Assert.Null(meso.Violation);
        Assert.Equal(0, meso.Stuck);
        Assert.Equal(0, meso.Gridlock);
        Within("capacity/h", meso.ThroughputPerHour, micro.ThroughputPerHour, 0.10);
        Within("max queue", meso.MaxQueue, micro.MaxQueue, 0.20, absSlack: 1);
    }

    /// <summary>Насыщенный поток одной полосы на зелёном (допусков в минуту зелёного) на изолированном светофоре, 1 и 2 полосы на въезде: ±5%.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SaturationFlow_PerGreenMinute_MatchesMicro(int lanes)
    {
        (double micro, int microExits) = SaturationProbe.Run(default, lanes: lanes);
        (double meso, int mesoExits) = SaturationProbe.Run(Meso(), lanes: lanes);
        _out.WriteLine($"lanes {lanes}: micro {micro:F2}/min green ({microExits} exits), meso {meso:F2}/min green ({mesoExits} exits)");
        Assert.True(micro > 15, $"эталон: {micro}");
        Within("saturation flow", meso, micro, 0.05);
        Within("exits", mesoExits, microExits, 0.05);
    }
}
