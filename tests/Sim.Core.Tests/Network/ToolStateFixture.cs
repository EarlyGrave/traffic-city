using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.Network;

/// <summary>
/// Сеть с дырами для тестов T2 (player-tools.md §3): малый мир сохранения без оверлея (<c>Routing.Levels = 0</c>: дыры оверлея - T3), две дороги без connector'ов вдали от
/// города строятся командой <c>BuildRoad</c> и затем освобождаются структурными примитивами (<see cref="NetworkEdit"/>) - получаются дыры в пулах узлов и сегментов и в аренах
/// полос и точек, <see cref="NetworkStore.ToolsTouched"/> = 1.
/// </summary>
internal static class ToolStateFixture
{
    public static ScenarioDesc Desc(bool peds = false)
    {
        ScenarioDesc d = WorldSaveFixtures.Small(buses: true);
        d.Routing.Levels = 0;
        if (peds)
        {
            d.Peds = new PedDesc { Enabled = true, Count = 40, MidblockAtStops = true, ArterialCrossings = ArterialCrossings.SignalOnly };
            SimCapacities c = d.Capacities;
            c.MaxPeds = 80;
            c.MaxResidents = 80;
            c.MaxPedPathSteps = 64;
            d.Capacities = c;
        }

        return d;
    }

    private static BuildRoadCmd Road(float ax, float az, float bx, float bz, int lanes = 1) => new()
    {
        Ax = ax,
        Az = az,
        Bx = bx,
        Bz = bz,
        SpeedLimit = 13.9f,
        LanesAB = (byte)lanes,
        LanesBA = (byte)lanes,
        Kind = RoadKind.Street,
        Flags = BuildRoadFlags.NoConnectors,
    };

    /// <summary>Две соединённые дороги (3 узла, 2 сегмента) вдали от города; возвращает индексы сегментов.</summary>
    public static (int First, int Second) BuildDetached(Simulation sim, float x = 3000f)
    {
        World w = sim.World;
        long rejected = sim.Counters.CommandsRejected;
        Assert.True(sim.Enqueue(Command.MakeBuildRoad(Road(x, 3000f, x + 100f, 3000f))));
        sim.Step(1);
        int first = LastCreatedSegment(sim);
        Assert.True(sim.Enqueue(Command.MakeBuildRoad(Road(x + 100f, 3000f, x + 200f, 3000f, lanes: 2))));
        sim.Step(1);
        int second = LastCreatedSegment(sim);
        Assert.Equal(rejected, sim.Counters.CommandsRejected);
        return (first, second);
    }

    private static int LastCreatedSegment(Simulation sim)
    {
        int seg = -1;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.SegmentCreated)
            {
                seg = e.A;
            }
        }

        Assert.True(seg >= 0, "нет события SegmentCreated");
        return seg;
    }

    /// <summary>Освобождает сегменты и их узлы (обратный порядок построения) и пересчитывает производные.</summary>
    public static void ReleaseDetached(Simulation sim, (int First, int Second) segs)
    {
        World w = sim.World;
        SegmentData a = w.Net.Segments[segs.First];
        SegmentData b = w.Net.Segments[segs.Second];
        Assert.True(NetworkEdit.ReleaseSegment(w, segs.Second));
        Assert.True(NetworkEdit.ReleaseSegment(w, segs.First));
        Assert.True(NetworkEdit.ReleaseNode(w, b.NodeB));
        Assert.True(NetworkEdit.ReleaseNode(w, a.NodeB));
        Assert.True(NetworkEdit.ReleaseNode(w, a.NodeA));
        NetworkEdit.RefreshDerived(w);
    }

    /// <summary>Мир с дырами: прогрев, построить и освободить дороги, продолжить.</summary>
    public static Simulation EditedWorld(bool peds = false, int threads = 1)
    {
        ScenarioDesc d = Desc(peds);
        Simulation sim = Simulation.Create(d, new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(300);
        (int First, int Second) segs = BuildDetached(sim);
        sim.Step(20);
        ReleaseDetached(sim, segs);
        sim.Step(40);
        return sim;
    }
}
