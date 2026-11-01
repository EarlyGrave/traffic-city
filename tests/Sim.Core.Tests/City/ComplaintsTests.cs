using System;
using System.Collections.Generic;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Metrics;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Scenarios;
using Xunit.Abstractions;

namespace Sim.Core.Tests.City;

/// <summary>
/// Жалобы жителей (PG4, progression.md §6.2): фильтр выпуска (счёт &gt;= 3, &lt;= 2 на вид, &lt;= 6 за час, ничья - меньший индекс, остывание 6 часов), кольцо и <c>CopyComplaints</c>,
/// место с точкой мира, детерминизм вывода при 1/2/3 потоках, 0 Б/тик, вне хэша и сейва; на перегрузке <c>s10k-res-overload</c> - не больше 6 за игровой час (долгий тест).
/// </summary>
public sealed class ComplaintsTests
{
    private const int MicroDay = 28_800;   // час = 1 200 тиков
    private const int Hour = MicroDay / 24;
    private readonly ITestOutputHelper _out;

    public ComplaintsTests(ITestOutputHelper output) => _out = output;

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static ScenarioDesc Econ() => ProgressionFixtures.Econ(MicroDay, """{ "enabled": false }""").Desc;

    private static int[] AliveSegments(Simulation sim, int n)
    {
        NetworkStore net = sim.World.Net;
        var r = new List<int>();
        for (int s = 0; s < net.SegmentCount && r.Count < n; s++)
        {
            if (net.SegmentAlive(s))
            {
                r.Add(s);
            }
        }

        return r.ToArray();
    }

    [Fact]
    public void Filter_MinCount_PerKind_PerHour_TieByIndex_Cooldown()
    {
        using Simulation sim = Make(Econ());
        World w = sim.World;
        CityWorld c = w.City!;
        Complaints q = new(MicroDay);
        int[] seg = AliveSegments(sim, 5);
        int Gen(int s) => w.Net.Segments[s].Generation;

        // LongWait: seg0 x5, seg1 x3, seg2 x3 (ничья с seg1 - меньший индекс), seg3 x2 (меньше порога).
        for (int i = 0; i < 5; i++)
        {
            q.Add(ComplaintKind.LongWait, seg[0], Gen(seg[0]));
        }

        for (int i = 0; i < 3; i++)
        {
            q.Add(ComplaintKind.LongWait, seg[2], Gen(seg[2]));
            q.Add(ComplaintKind.LongWait, seg[1], Gen(seg[1]));
        }

        q.Add(ComplaintKind.LongWait, seg[3], Gen(seg[3]));
        q.Add(ComplaintKind.LongWait, seg[3], Gen(seg[3]));
        q.EmitHour(w, c);
        Assert.Equal(2, q.Seq);
        var got = new ComplaintInfo[8];
        Assert.Equal(2, q.Copy(got, 0));
        Assert.Equal(seg[0], got[0].Place.Index);
        Assert.Equal(5, got[0].Count);
        Assert.Equal(Math.Min(seg[1], seg[2]), got[1].Place.Index);
        Assert.Equal(EntityKind.Segment, got[0].Place.ObjKind);
        EventPlace expect = PlaceEvents.SegmentPlace(w.Net, seg[0]);
        Assert.Equal(expect.X, got[0].Place.X);
        Assert.Equal(expect.Z, got[0].Place.Z);
        Assert.Equal(1, q.Copy(got, 1));
        Assert.Equal(2, got[0].Seq);

        // Накопитель очищен; через час seg0 снова x5 - остывает (6 часов), выходит следующий по счёту.
        for (int i = 0; i < 5; i++)
        {
            q.Add(ComplaintKind.LongWait, seg[0], Gen(seg[0]));
        }

        for (int i = 0; i < 4; i++)
        {
            q.Add(ComplaintKind.LongWait, seg[4], Gen(seg[4]));
        }

        sim.Step(Hour);
        q.EmitHour(w, c);
        Assert.Equal(3, q.Seq);
        Assert.Equal(1, q.Copy(got, 2));
        Assert.Equal(seg[4], got[0].Place.Index);

        // Всего за час <= 6: по три места со счётом 3 у четырёх видов (сегменты и здания) - до 2 на вид, всего 6.
        sim.Step(7 * Hour);
        int b = FirstBuilding(c, 0);
        int b2 = FirstBuilding(c, b + 1);
        int b3 = FirstBuilding(c, b2 + 1);
        foreach (ComplaintKind k in new[] { ComplaintKind.NoParking, ComplaintKind.LongCommute, ComplaintKind.TourCancelled, ComplaintKind.LongWait })
        {
            for (int i = 0; i < 3; i++)
            {
                if (k == ComplaintKind.LongWait)
                {
                    q.Add(k, seg[1], Gen(seg[1]));
                    q.Add(k, seg[2], Gen(seg[2]));
                    q.Add(k, seg[3], Gen(seg[3]));
                }
                else
                {
                    q.Add(k, b, c.Buildings.Pool.GenerationOf(b));
                    q.Add(k, b2, c.Buildings.Pool.GenerationOf(b2));
                    q.Add(k, b3, c.Buildings.Pool.GenerationOf(b3));
                }
            }
        }

        int before = q.Seq;
        q.EmitHour(w, c);
        Assert.Equal(Complaints.PerHour, q.Seq - before);
        int n = q.Copy(got, before);
        var perKind = new int[6];
        for (int i = 0; i < n; i++)
        {
            perKind[(int)got[i].Kind]++;
            Assert.True(got[i].Count >= Complaints.MinCount);
        }

        foreach (int pk in perKind)
        {
            Assert.True(pk <= Complaints.PerKind);
        }

        Assert.Equal(ComplaintKind.LongWait, got[0].Kind);   // первый проход - лучшее место каждого вида по порядку 1..5, второй - вторые места
        Assert.Equal(ComplaintKind.NoParking, got[1].Kind);
        Assert.Equal(EntityKind.Building, got[1].Place.ObjKind);
        Assert.Equal(ComplaintKind.LongWait, got[4].Kind);
    }

    private static int FirstBuilding(CityWorld c, int from)
    {
        for (int i = from; i < c.Buildings.Pool.HighWater; i++)
        {
            if (c.Buildings.Pool.IsAlive(i))
            {
                return i;
            }
        }

        throw new InvalidOperationException("нет зданий");
    }

    private static List<(long Tick, int Seq, int Kind, int Obj, int Index, int Gen, int Count)> Run(int threads, int hours, out ulong hash)
    {
        using Simulation sim = Make(Econ(), threads);
        var r = new List<(long, int, int, int, int, int, int)>();
        for (int t = 0; t < hours * Hour; t += 200)
        {
            sim.Step(200);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.Complaint)
                {
                    r.Add((e.Tick, e.B, e.A, (int)e.Place.ObjKind, e.Place.Index, e.Place.Generation, e.C));
                }
            }
        }

        var ring = new ComplaintInfo[Complaints.RingSize];
        int n = sim.CopyComplaints(ring, 0);
        Assert.Equal(Math.Min(r.Count, Complaints.RingSize), n);
        hash = sim.ComputeStateHash();
        return r;
    }

    [Fact]
    public void Output_Deterministic_At123Threads_AndBoundedPerHour()
    {
        var one = Run(1, 12, out ulong h1);
        _out.WriteLine($"complaints in 12 h: {one.Count}");
        Assert.NotEmpty(one);
        for (int threads = 2; threads <= 3; threads++)
        {
            var other = Run(threads, 12, out ulong h);
            Assert.Equal(h1, h);
            Assert.Equal(one, other);
        }

        AssertPerHour(one, Hour);
    }

    private static void AssertPerHour(List<(long Tick, int Seq, int Kind, int Obj, int Index, int Gen, int Count)> list, int hourTicks)
    {
        var perHour = new Dictionary<long, int>();
        var perKindHour = new Dictionary<(long, int), int>();
        foreach (var e in list)
        {
            long hh = (e.Tick + 1) / hourTicks;
            perHour[hh] = perHour.GetValueOrDefault(hh) + 1;
            perKindHour[(hh, e.Kind)] = perKindHour.GetValueOrDefault((hh, e.Kind)) + 1;
            Assert.True(e.Count >= Complaints.MinCount);
            Assert.NotEqual((int)EntityKind.None, e.Obj);
        }

        foreach (int v in perHour.Values)
        {
            Assert.True(v <= Complaints.PerHour);
        }

        foreach (int v in perKindHour.Values)
        {
            Assert.True(v <= Complaints.PerKind);
        }
    }

    [Fact]
    public void Complaints_OutsideHashAndSave()
    {
        using Simulation sim = Make(Econ());
        sim.Step(3 * Hour);
        CityKpi k = sim.World.City!.Kpi;
        ulong h0 = sim.ComputeStateHash();
        byte[] s0 = WorldSaveFixtures.SaveBytes(sim);
        k.Complaints.Add(ComplaintKind.LongWait, AliveSegments(sim, 1)[0], 1);
        k.Complaints.Seq += 5;
        Assert.Equal(h0, sim.ComputeStateHash());
        Assert.True(s0.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(sim)));
    }

    [Fact]
    public void Step_WithComplaints_AllocatesZeroBytes()
    {
        using Simulation sim = Make(Econ());
        sim.Step((3 * Hour) - 300);   // прогрев: три выпуска до окна (JIT путей выпуска)
        long bytes = AllocationProbe.Measure(() => sim.Step(2 * Hour), out _);   // два выпуска внутри окна
        Assert.Equal(0, bytes);
        var buf = new ComplaintInfo[16];
        Assert.Equal(0, AllocationProbe.Measure(() => sim.CopyComplaints(buf, 0)));
    }

    /// <summary>Перегрузка (s10k-res-overload, 102 тыс. жителей): за утро жалобы идут, но не больше 6 за игровой час и 2 на вид. Долгий: <c>TC_SLOW=1</c>.</summary>
    [SlowFact]
    [Trait("Category", "Long")]
    public void Overload_AtMostSixPerGameHour()
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s10k-res-overload.json")).Desc;
        using Simulation sim = Simulation.Create(in d);
        const int hourTicks = 3000;
        var list = new List<(long Tick, int Seq, int Kind, int Obj, int Index, int Gen, int Count)>();
        for (int t = 0; t < 10 * hourTicks; t += 100)
        {
            sim.Step(100);
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == SimEventKind.Complaint)
                {
                    list.Add((e.Tick, e.B, e.A, (int)e.Place.ObjKind, e.Place.Index, e.Place.Generation, e.C));
                }
            }
        }

        _out.WriteLine($"complaints in 10 h: {list.Count}");
        Assert.NotEmpty(list);
        AssertPerHour(list, hourTicks);
    }
}
