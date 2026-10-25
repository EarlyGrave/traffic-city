using Sim.Core.Api;
using Sim.Core.Metrics;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Worlds;

/// <summary>
/// ADR-0019, A3/A4: фазы 7-9 работают по маскам и накопителям фазы 6. Побитовую эквивалентность прежним проходам по всем машинам на каждом тике
/// сверяют Debug.Assert в самих фазах (<c>TransitionPhase.DebugCheckMask</c>, <c>ArrivalPhase</c>, <c>MetricsPhase.DebugVerify</c>), здесь -
/// проверка, не зависящая от конфигурации сборки: нагрузка сегментов по требованию совпадает с полным пересчётом по тому же состоянию.
/// </summary>
public class PhaseMasksTests
{
    [Theory]
    [InlineData(37)]
    [InlineData(40)]   // конец тика, кратного 20: нагрузка уже посчитана фазой 9
    [InlineData(1201)]
    public void SegmentLoads_OnDemand_EqualsFullRecompute(int ticks)
    {
        ScenarioDesc d = Step2.S1k(buses: true);
        using Simulation sim = Simulation.Create(in d);
        sim.Step(ticks);
        World w = sim.World;
        SegmentLoad[] got = sim.SegmentLoads.ToArray();
        MetricsPhase.ComputeSegmentLoads(w);
        int n = w.Net.SegmentCount;
        Assert.True(n > 0);
        int nonEmpty = 0;
        for (int s = 0; s < n; s++)
        {
            Assert.Equal(w.SegmentLoads[s].Count, got[s].Count);
            Assert.Equal(w.SegmentLoads[s].SpeedRatio, got[s].SpeedRatio);
            Assert.Equal(w.SegmentLoads[s].Density, got[s].Density);
            if (got[s].Count > 0)
            {
                nonEmpty++;
            }
        }

        Assert.True(nonEmpty > 0);
    }

    [Fact]
    public void Counters_MatchBetweenStepSizes()
    {
        // Шаг по 1 и по 50 тиков даёт один и тот же хэш: накопители и маски не зависят от границ вызовов Step.
        ScenarioDesc d = Step2.S1k(buses: true);
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        for (int k = 0; k < 600; k++)
        {
            a.Step(1);
        }

        for (int k = 0; k < 12; k++)
        {
            b.Step(50);
        }

        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.Counters.DistanceTotalMm, b.Counters.DistanceTotalMm);
        Assert.Equal(a.Counters.MovingVehicleTicksTotal, b.Counters.MovingVehicleTicksTotal);
        Assert.Equal(a.Counters.TripsCompleted, b.Counters.TripsCompleted);
    }
}
