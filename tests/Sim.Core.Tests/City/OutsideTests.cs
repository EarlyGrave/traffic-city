using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Tools;
using Sim.Scenarios;
using Xunit.Abstractions;

namespace Sim.Core.Tests.City;

/// <summary>Y3 (new-game-scratch.md §2.1-§2.5, ADR-0037): внешние подключения - въезд машиной от края карты, внешние рабочие места, защита узла, сейв и хэш.</summary>
public sealed class OutsideTests
{
    private const int Day = 72_000;
    private const int Hour = Day / 24;

    private readonly ITestOutputHelper _out;

    public OutsideTests(ITestOutputHelper output) => _out = output;

    private static string YuryevPath => Path.Combine(GenFixture.RepoRoot(), "scenarios", "yuryev-polsky.json");

    private static ScenarioDesc Yuryev() => ScenarioLoader.Load(YuryevPath);

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    /// <summary>Жилая зона вокруг нескольких свободных участков (игрок зонирует R программно).</summary>
    private static void PaintResidential(Simulation sim, int squares)
    {
        LotStore l = sim.World.City!.Lots;
        int painted = 0;
        for (int i = 0; i < l.Count && painted < squares; i += Math.Max(1, l.Count / squares))
        {
            var cmd = new PaintZoneCmd { X0 = l.X[i] - 200f, Z0 = l.Z[i] - 200f, X1 = l.X[i] + 200f, Z1 = l.Z[i] + 200f, ZoneKind = (byte)LotZone.Residential, Density = 0 };
            if (ToolsFixture.Run(sim, Command.MakePaintZone(cmd, (uint)(10 + i))) == RejectReason.None)
            {
                painted++;
            }
        }

        Assert.True(painted > 0, "не удалось закрасить ни одной R-зоны");
    }

    [Fact]
    public void Yuryev_Create_MarksOutsideNode_AndStartsEmpty()
    {
        ScenarioDesc d = Yuryev();
        using Simulation sim = Make(in d);
        Assert.Equal(1, sim.OutsideCount);
        Assert.True(sim.GetOutside(0, out OutsideInfo o));
        Assert.Equal(0, o.Node);
        Assert.Equal(-3040f, o.X);
        Assert.Equal(3040f, o.Z);
        Assert.Equal(2000, o.JobsTotal);
        Assert.NotEqual(0, (int)(sim.World.Net.Nodes[0].Flags & NodeFlags.Outside));
        Assert.Equal(0, sim.World.City!.Residents.AliveCount);
        Assert.False(sim.GetOutside(1, out _));
    }

    [Fact]
    public void Yuryev_PaintedResidential_ImmigrantsArriveByCar_AndWorkOutside()
    {
        ScenarioDesc d = Yuryev();
        using Simulation sim = Make(in d);
        PaintResidential(sim, 6);
        CityWorld c = sim.World.City!;
        OutsideState o = c.Outside!;
        int maxInTransit = 0;
        int arrivedEvents = 0;
        for (int h = 0; h < 120; h++)
        {
            sim.Step(Hour);
            Assert.True(sim.GetOutside(0, out OutsideInfo info));
            maxInTransit = Math.Max(maxInTransit, info.ArrivalsInTransit);
            foreach (SimEvent e in sim.Events)
            {
                arrivedEvents += e.Kind == SimEventKind.ImmigrantsArrived ? (int)e.A : 0;
            }
        }

        _out.WriteLine($"lots={c.Lots.Count} bld={c.Buildings.HighWater} dR={c.DemandR} built={sim.World.Counters.BuildingsBuilt} ");
        _out.WriteLine($"pop={c.Residents.AliveCount} arrivals={o.Arrivals} timeouts={o.ArrivalTimeouts} maxInTransit={maxInTransit} events={arrivedEvents} jobs={o.Jobs[0]} taken={o.Taken[0]} imm={sim.World.Counters.Immigrants}");
        Assert.True(o.Arrivals > 0, "никто не доехал от края карты");
        Assert.True(maxInTransit > 0, "переселенцы не были в пути");
        Assert.Equal(o.Arrivals, arrivedEvents);
        Assert.True(o.Taken[0] > 0, "никто не работает за краем");
        Assert.Equal(2000, o.Jobs[0]);   // своих вакансий в городе нет - затухать нечему (§2.4)
        Assert.Equal(0, sim.World.Counters.StuckEventsTotal);
    }

    [Fact]
    public void DayDecay_ShrinksByLocalVacancies_NotBelowTaken()
    {
        var d = new CityOutsideDesc { Count = 1, JobsDecayQ16 = CityDesc.Q16(0.5), ArrivalByCar = 1 };
        d.Set(0, new OutsideConnDesc { Node = 0, Jobs = 1000, ShareQ16 = 65536 });
        var o = new OutsideState(in d);
        o.Taken[0] = 700;
        o.DayDecay(400);
        Assert.Equal(800, o.Jobs[0]);
        o.DayDecay(10_000);
        Assert.Equal(700, o.Jobs[0]);
        o.DayDecay(0);
        Assert.Equal(700, o.Jobs[0]);
    }

    [Fact]
    public void OutsideNode_CannotBeDemolished()
    {
        ScenarioDesc d = Yuryev();
        using Simulation sim = Make(in d);
        NetworkStore net = sim.World.Net;
        int seg = net.NodeArms[net.Nodes[0].FirstArm];
        RejectReason r = ToolsFixture.Run(sim, Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 7));
        Assert.Equal(RejectReason.Protected, r);
    }

    [Fact]
    public void HashIsIndependentOfThreadCount()
    {
        ulong Run(int threads)
        {
            ScenarioDesc d = Yuryev();
            using Simulation sim = Make(in d, threads);
            PaintResidential(sim, 6);
            sim.Step((3 * Day) + (6 * Hour));
            return sim.ComputeStateHash();
        }

        ulong h1 = Run(1);
        Assert.Equal(h1, Run(2));
        Assert.Equal(h1, Run(3));
    }

    [Fact]
    public void SaveRoundTrip_MidArrival_ContinuesIdentically()
    {
        ScenarioDesc d = Yuryev();
        using Simulation a = Make(in d);
        PaintResidential(a, 6);
        a.Step(3 * Day);
        int transit = 0;
        for (int i = 0; i < 200 && transit == 0; i++)
        {
            a.Step(Hour / 4);
            a.GetOutside(0, out OutsideInfo info);
            transit = info.ArrivalsInTransit;
        }

        Assert.True(transit > 0, "нет переселенцев в пути для проверки сейва");
        using Simulation b = ToolsFixture.RoundTrip(a);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        a.Step(2 * Hour);
        b.Step(2 * Hour);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.World.City!.Outside!.Arrivals, b.World.City!.Outside!.Arrivals);
    }

    [Fact]
    public void Step_AllocatesNothing_WithArrivals()
    {
        ScenarioDesc d = Yuryev();
        using Simulation sim = Make(in d);
        PaintResidential(sim, 6);
        sim.Step((3 * Day) + (6 * Hour));
        long bytes = AllocationProbe.Measure(() => sim.Step(Hour + 200));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Loader_ParsesOutside_AndRejectsBadOnes()
    {
        string json = File.ReadAllText(YuryevPath);
        static byte[] Bytes(string file) => File.ReadAllBytes(Path.Combine(GenFixture.RepoRoot(), "scenarios", file));
        static string Text(string file) => File.ReadAllText(Path.Combine(GenFixture.RepoRoot(), "scenarios", file));
        CityDesc t = ScenarioLoader.Parse(json, Text, "t", null, null, Bytes).Desc.Town;
        Assert.Equal(1, t.Outside.Count);
        Assert.Equal(0, t.Outside.C0.Node);
        Assert.Equal(2000, t.Outside.C0.Jobs);
        Assert.Equal(1800 * 20, t.Outside.C0.JobCostTicks);
        Assert.Equal(65536, t.Outside.C0.ShareQ16);
        Assert.Equal(1, t.Outside.ArrivalByCar);
        Assert.Equal(1, t.Zoning.GridFromBounds);
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json.Replace("\"node\": 0,", "\"node\": 999,"), Text, "t", null, null, Bytes));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json.Replace("\"immigrant_share\": 1.0", "\"immigrant_share\": 0.5"), Text, "t", null, null, Bytes));
        Assert.Throws<ScenarioException>(() => ScenarioLoader.Parse(json.Replace("\"model\": \"residents\"", "\"model\": \"uniform\""), Text, "t", null, null, Bytes));
    }
}
