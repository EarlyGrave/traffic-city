using System;
using Sim.Core.Api;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Intersections;

/// <summary>
/// Координация светофоров генератора (2.5 S3, intersections.md 6.7, city-generator 3.8): цикл, офсеты «чередующейся волны», Fixed с окном C/2,
/// красный не дольше 52 с и хэш при 1/2/3 потоках на s1k с <c>alternate</c>.
/// </summary>
public class CoordinationGeneratorTests
{
    private readonly ITestOutputHelper _out;

    public CoordinationGeneratorTests(ITestOutputHelper output) => _out = output;

    private static ScenarioDesc S1k(CitySignalMode mode, CitySignalCoordination coord)
    {
        ScenarioDesc d = Worlds.Step3.S1k();
        d.City.SignalMode = mode;
        d.City.SignalCoordination = coord;
        return d;
    }

    [Fact]
    public void Alternate_Adaptive_CycleAndOffsets_NeighboursShiftedByHalfCycle()
    {
        ScenarioDesc d = S1k(CitySignalMode.Adaptive, CitySignalCoordination.Alternate);
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        SignalStore s = w.Signals;
        int coordinated = 0;
        int pairs = 0;
        for (int a = 0; a < s.Count; a++)
        {
            if (s.CycleTicks[a] == 0)
            {
                continue;
            }

            coordinated++;
            Assert.Equal(1460, s.CycleTicks[a]);   // s1k: 608 м / 16.7 м/с = 36.5 с, C = 2T = 72.96 с -> 1459 тиков, чётное 1460
            Assert.True(s.OffsetTicks[a] < 1460);
            var pa = w.Net.Nodes[s.Node[a]].Position;
            for (int b = a + 1; b < s.Count; b++)
            {
                if (s.CycleTicks[b] == 0)
                {
                    continue;
                }

                var pb = w.Net.Nodes[s.Node[b]].Position;
                bool alongX = Math.Abs(pa.Z - pb.Z) < 1f && Math.Abs(Math.Abs(pa.X - pb.X) - 608f) < 1f;
                bool alongZ = Math.Abs(pa.X - pb.X) < 1f && Math.Abs(Math.Abs(pa.Z - pb.Z) - 608f) < 1f;
                if (alongX || alongZ)
                {
                    pairs++;
                    int diff = Math.Abs(s.OffsetTicks[a] - s.OffsetTicks[b]);
                    Assert.InRange(Math.Min(diff, 1460 - diff), 728, 732);   // сдвиг на T = C/2 (-T = +T mod 2T)
                }
            }
        }

        _out.WriteLine($"coordinated={coordinated} neighbourPairs={pairs} of {s.Count}");
        Assert.True(coordinated > 0 && pairs > 0);
    }

    [Fact]
    public void Alternate_Fixed_WindowGreen_AndLegacyAndNoneUnchanged()
    {
        ScenarioDesc d = S1k(CitySignalMode.Fixed, CitySignalCoordination.Alternate);
        using (Simulation sim = Simulation.Create(in d))
        {
            SignalStore s = sim.World.Signals;
            for (int k = 0; k < s.Count; k++)
            {
                if (s.GreenTicks[k * SignalStore.MaxPhases] == 650)
                {
                    Assert.Equal(650, s.GreenTicks[(k * SignalStore.MaxPhases) + 1]);   // C/2 - Yellow - AllRed = 36.5 - 3 - 1 = 32.5 с
                    Assert.Equal(1460, s.CycleOf(k));
                    Assert.Equal(0, s.CycleTicks[k]);
                }
            }
        }

        ScenarioDesc none = S1k(CitySignalMode.Adaptive, CitySignalCoordination.None);
        using Simulation sn = Simulation.Create(in none);
        for (int k = 0; k < sn.World.Signals.Count; k++)
        {
            Assert.Equal(0, sn.World.Signals.OffsetTicks[k]);
            Assert.Equal(0, sn.World.Signals.CycleTicks[k]);
        }

        ScenarioDesc legacy = S1k(CitySignalMode.Adaptive, CitySignalCoordination.Legacy);
        using Simulation sl = Simulation.Create(in legacy);
        for (int k = 0; k < sl.World.Signals.Count; k++)
        {
            Assert.Equal(0, sl.World.Signals.CycleTicks[k]);
        }
    }

    /// <summary>
    /// 2 минуты s1k с координацией (Debug-сверки ворот каждый тик): красный каждой оси не дольше 52 с (инвариант 5.6), хэш при 1/2/3 потоках одинаков,
    /// stuck = 0.
    /// </summary>
    [Fact]
    public void Alternate_S1k_RedBelow52s_SameHashOn1_2_3Threads()
    {
        ScenarioDesc d = S1k(CitySignalMode.Adaptive, CitySignalCoordination.Alternate);
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        using Simulation c = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 3 });
        SignalStore s = a.World.Signals;
        int n = s.Count;
        int[,] gap = new int[n, 2];
        int maxGap = 0;
        for (int t = 0; t < 2400; t++)
        {
            a.Step(1);
            b.Step(1);
            c.Step(1);
            for (int k = 0; k < n; k++)
            {
                if (s.PhaseCount[k] != 2)
                {
                    continue;
                }

                SignalState st = a.World.SignalStates[k];
                int axis = st.Phase & 0x7F;
                for (int ax = 0; ax < 2; ax++)
                {
                    bool served = axis == ax && st.Interval != SignalInterval.AllRed;
                    gap[k, ax] = served ? 0 : gap[k, ax] + 1;
                    maxGap = Math.Max(maxGap, gap[k, ax]);
                }
            }
        }

        _out.WriteLine($"maxRedTicks={maxGap} ({maxGap / 20.0:F1} s) spillOuts={a.Counters.SpillOuts} stuck={a.Counters.StuckEventsTotal}");
        Assert.True(maxGap <= SignalStore.MaxRedTicks, $"красный {maxGap / 20.0:F1} с");
        Assert.Equal(0, a.Counters.StuckEventsTotal);
        ulong h = a.ComputeStateHash();
        Assert.Equal(h, b.ComputeStateHash());
        Assert.Equal(h, c.ComputeStateHash());
    }
}
