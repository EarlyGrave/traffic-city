#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Peds;
using Sim.Core.Save;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Peds;

/// <summary>Агенты пешеходов (pedestrians.md §15 «Агенты»): события, жители, PD2-PD4, PD8, сейв, команды, снапшот. Переходы и пассажиры - P3/P4.</summary>
public sealed class PedAgentsTests
{
    /// <summary>Малый город (WorldSaveFixtures.Small) с пешеходами: быстрые циклы «дом - работа» (активность 5-20 с), чтобы событий было много.</summary>
    private static ScenarioDesc Desc(int count = 300, int residents = 600, int rampTicks = 200)
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Peds = new PedDesc
        {
            Enabled = true,
            Count = count,
            Residents = residents,
            SpawnRampTicks = rampTicks,
            ActivityMinTicks = 100,
            ActivityMaxTicks = 400,
            MidblockAtStops = true,
        };
        d.Capacities.MaxPeds = (count * 13 / 10) + 8;
        d.Capacities.MaxResidents = residents;
        d.Capacities.MaxPedPathSteps = 64;
        d.Transit.PassengerModel = PassengerModel.Agents;
        return d;
    }

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    // ------------------------------------------------------------ PD1: без секции peds

    [Fact]
    public void WithoutPeds_NoPedStateAndCommandRejected()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        using Simulation sim = Make(in d);
        Assert.False(sim.HasPeds);
        Assert.Null(sim.World.Peds);
        Assert.True(sim.Pedestrians.IsEmpty);
        Assert.True(sim.CrossingStates.IsEmpty);
        sim.Enqueue(Command.MakeSetPedTarget(5));
        sim.Step(2);
        Assert.Equal(1, sim.Counters.CommandsRejected);
        Assert.Equal(0, sim.Counters.PedSpawned);
    }

    [Fact]
    public void PedsEnabled_DoesNotChangeVehicles()
    {
        ScenarioDesc plain = WorldSaveFixtures.Small();
        ScenarioDesc withPeds = Desc();
        using Simulation a = Make(in plain);
        using Simulation b = Make(in withPeds);
        a.Step(900);
        b.Step(900);
        Assert.True(b.ActivePedCount > 0);
        Assert.True(a.Vehicles.SequenceEqual(b.Vehicles), "машины не должны зависеть от прозрачных пешеходов (P2)");
        Assert.Equal(a.Counters.VehiclesSpawned, b.Counters.VehiclesSpawned);
        Assert.Equal(a.Counters.TripsCompleted, b.Counters.TripsCompleted);
        Assert.Equal(a.Counters.DistanceTotalMm, b.Counters.DistanceTotalMm);
    }

    // ------------------------------------------------------------ кинематика и события

    [Fact]
    public void SingleEdgeLeg_ArrivesAtExactCeilTick()
    {
        ScenarioDesc d = Desc(count: 1, residents: 1, rampTicks: 1);
        using Simulation sim = Make(in d);
        World w = sim.World;
        PedWorld pw = w.Peds!;
        Assert.Equal(1, pw.R.Count);

        int e = -1;
        for (int i = 0; i < pw.Net.SidewalkEdgeCount && e < 0; i++)
        {
            if (pw.Net.Edges[i].LenMm > 20_000)
            {
                e = i;
            }
        }

        Assert.True(e >= 0);
        int len = pw.Net.Edges[e].LenMm;
        pw.R.HomeEdge[0] = e;
        pw.R.HomeSMm[0] = 0;
        pw.R.WorkEdge[0] = e;
        pw.R.WorkSMm[0] = len;
        pw.R.ReadyTick[0] = 5;
        pw.Wheel.Clear();
        pw.Wheel.Schedule(pw.P.Capacity, 5, -1);

        sim.Step(7);   // тик 5: пробуждение; тик 6: запуск, путь, вход на ребро (T0 = 6)
        Assert.Equal(1, pw.P.AliveCount);
        int p = 0;
        int v = pw.P.VMmT[p];
        Assert.InRange(v, d.Peds.SpeedMinMmPerTick == 0 ? 56 : d.Peds.SpeedMinMmPerTick, 75);
        Assert.Equal((byte)PedState.Walking, pw.P.State[p]);
        Assert.Equal(6, pw.P.T0[p]);
        long expected = 6 + ((len + v - 1) / v);
        Assert.Equal(expected, pw.P.EvTick[p]);

        sim.Step((int)(expected - sim.Tick));   // события тика expected ещё впереди
        Assert.Equal(1, pw.P.AliveCount);
        Assert.Equal(expected, sim.Tick);
        sim.Step(1);                            // событие исполнено на тике expected
        Assert.Equal(0, pw.P.AliveCount);
        Assert.Equal(ResidentStore.AtWork, pw.R.Where[0]);
        Assert.Equal(1, sim.Counters.PedTripsCompleted);
        Assert.Equal(expected - 6, sim.Counters.PedTripTicksTotal + 0);   // поездка началась на тике запуска (6): TripTick = 6
    }

    // ------------------------------------------------------------ PD2, PD8

    [Fact]
    public void ResidentsEqualIdlePlusActive_Every50Ticks_6000Ticks()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        PedWorld pw = sim.World.Peds!;
        for (int t = 0; t < 6000; t += 50)
        {
            sim.Step(50);
            Assert.Equal(pw.R.Count, pw.R.IdleCount() + pw.P.AliveCount);
            Assert.Equal(sim.Counters.PedActive, pw.P.AliveCount);
        }

        Assert.True(sim.Counters.PedTripsCompleted > 50, $"trips {sim.Counters.PedTripsCompleted}");
        Assert.Equal(0, sim.Counters.PedNoPath);
        Assert.Equal(0, sim.Counters.PedAllocFailed);
        Assert.Equal(0, sim.Counters.PedStuckEvents);
        Assert.True(sim.Counters.PedActiveTicksTotal > 0);
    }

    [Fact]
    public void Residents_AllReachable_BothWays()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        PedWorld pw = sim.World.Peds!;
        var path = new int[64];
        long work = 0;
        float limit = PedRouter.LimitTicks(in pw.Cfg);
        for (int r = 0; r < pw.R.Count; r++)
        {
            Assert.True(pw.Router.Find(pw.R.HomeEdge[r], pw.R.HomeSMm[r], pw.R.WorkEdge[r], pw.R.WorkSMm[r], path, 0, 64, limit, ref work, out int c1, out _), $"resident {r}: home -> work");
            Assert.True(c1 >= 1);
            Assert.True(pw.Router.Find(pw.R.WorkEdge[r], pw.R.WorkSMm[r], pw.R.HomeEdge[r], pw.R.HomeSMm[r], path, 0, 64, limit, ref work, out int c2, out _), $"resident {r}: work -> home");
            Assert.True(c2 >= 1);
            Assert.InRange(pw.R.HomeSMm[r], 0, pw.Net.Edges[pw.R.HomeEdge[r]].LenMm);
        }
    }

    [Fact]
    public void Path_IsContiguousAndStartsEndsAtPoints()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        PedWorld pw = sim.World.Peds!;
        PedNetwork net = pw.Net;
        var path = new int[64];
        long work = 0;
        float limit = PedRouter.LimitTicks(in pw.Cfg);
        int checkedPaths = 0;
        for (int r = 0; r < pw.R.Count; r += 7)
        {
            int he = pw.R.HomeEdge[r];
            int we = pw.R.WorkEdge[r];
            Assert.True(pw.Router.Find(he, pw.R.HomeSMm[r], we, pw.R.WorkSMm[r], path, 0, 64, limit, ref work, out int count, out int s0));
            int first = path[0] & PedStore.StepMask;
            int last = path[count - 1] & PedStore.StepMask;
            Assert.Equal(he, first);
            Assert.Equal(we, last);
            Assert.InRange(s0, 0, net.Edges[he].LenMm);

            // Узел выхода каждого шага = узел входа следующего.
            for (int k = 0; k + 1 < count; k++)
            {
                int e1 = path[k] & PedStore.StepMask;
                int e2 = path[k + 1] & PedStore.StepMask;
                int exit = (path[k] & PedStore.StepBwd) != 0 ? net.Edges[e1].A : net.Edges[e1].B;
                int entry = (path[k + 1] & PedStore.StepBwd) != 0 ? net.Edges[e2].B : net.Edges[e2].A;
                Assert.Equal(exit, entry);
                Assert.Equal(net.Edges[e2].Kind >= PedEdgeKind.Crossing, (path[k + 1] & PedStore.StepCross) != 0);
            }

            checkedPaths++;
        }

        Assert.True(checkedPaths > 10);
    }

    // ------------------------------------------------------------ PD3: потоки и вид

    [Fact]
    public void Hash_DoesNotDependOnThreads()
    {
        ScenarioDesc d = Desc();
        ulong[]? reference = null;
        foreach (int threads in new[] { 1, 2, 3 })
        {
            using Simulation sim = Make(in d, threads);
            var hashes = new ulong[6];
            for (int i = 0; i < hashes.Length; i++)
            {
                sim.Step(250);
                hashes[i] = sim.ComputeStateHash();
            }

            Assert.True(sim.ActivePedCount > 0);
            if (reference == null)
            {
                reference = hashes;
            }
            else
            {
                Assert.Equal(reference, hashes);
            }
        }
    }

    [Fact]
    public void Hash_DoesNotDependOnRenderView_AndViewFiltersByEdgeBox()
    {
        ScenarioDesc d = Desc();
        using Simulation all = Make(in d);
        using Simulation focus = Make(in d, threads: 2);
        using Simulation empty = Make(in d);
        focus.SetRenderView(300f, 300f, 700f, 700f);
        empty.SetRenderView(-9000f, -9000f, -8000f, -8000f);
        for (int i = 0; i < 6; i++)
        {
            all.Step(250);
            focus.Step(250);
            empty.Step(250);
            Assert.Equal(all.ComputeStateHash(), focus.ComputeStateHash());
            Assert.Equal(all.ComputeStateHash(), empty.ComputeStateHash());
        }

        ReadOnlySpan<PedestrianRenderData> a = all.Pedestrians;
        ReadOnlySpan<PedestrianRenderData> f = focus.Pedestrians;
        ReadOnlySpan<PedestrianRenderData> e = empty.Pedestrians;
        Assert.Equal(a.Length, f.Length);
        Assert.Equal(a.Length, e.Length);
        int activeAll = 0;
        int activeFocus = 0;
        int insideButMissing = 0;
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(0, e[i].Flags);
            if ((a[i].Flags & PedRenderFlags.Active) != 0)
            {
                activeAll++;
                bool inside = a[i].X >= 300f && a[i].X <= 700f && a[i].Z >= 300f && a[i].Z <= 700f;
                if (inside && f[i].Flags == 0)
                {
                    insideButMissing++;
                }
            }

            if (f[i].Flags != 0)
            {
                activeFocus++;
                Assert.Equal(a[i].X, f[i].X);
                Assert.Equal(a[i].Z, f[i].Z);
                Assert.Equal(a[i].Heading, f[i].Heading);
                Assert.Equal(a[i].Generation, f[i].Generation);
            }
        }

        Assert.True(activeAll > 20, $"active {activeAll}");
        Assert.True(activeFocus > 0 && activeFocus < activeAll, $"focus {activeFocus} of {activeAll}");
        Assert.Equal(0, insideButMissing);
    }

    // ------------------------------------------------------------ PD4

    [Fact]
    public void Step_AllocatesZeroBytes_WithPeds_AllViewsAndThreads()
    {
        foreach ((int threads, bool view) in new[] { (1, false), (2, true) })
        {
            ScenarioDesc d = Desc();
            using Simulation sim = Make(in d, threads);
            if (view)
            {
                sim.SetRenderView(200f, 200f, 800f, 800f);
            }

            sim.Step(1500);
            Assert.True(sim.ActivePedCount > 100);
            long bytes = AllocationProbe.Measure(() => sim.Step(100));
            Assert.Equal(0, bytes);
        }
    }

    // ------------------------------------------------------------ снапшот

    [Fact]
    public void Snapshot_IsDoubleBuffered_AndPositionsMatchEdges()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        sim.Step(800);
        PedWorld pw = sim.World.Peds!;
        ReadOnlySpan<PedestrianRenderData> cur = sim.Pedestrians;
        ReadOnlySpan<PedestrianRenderData> prev = sim.PreviousPedestrians;
        Assert.Equal(pw.P.HighWater, cur.Length);
        Assert.True(prev.Length > 0);
        int active = 0;
        for (int i = 0; i < cur.Length; i++)
        {
            bool alive = pw.P.Pool.IsAlive(i);
            var st = (PedState)pw.P.State[i];
            if (alive && st is PedState.Walking or PedState.Crossing)
            {
                active++;
                Assert.Equal(PedRenderFlags.Active, cur[i].Flags & PedRenderFlags.Active);
                Assert.Equal(i, cur[i].Index);
                Assert.Equal((byte)st, cur[i].State);
                Assert.Equal((ushort)pw.P.Pool.GenerationOf(i), cur[i].Generation);
                Assert.True(float.IsFinite(cur[i].X) && float.IsFinite(cur[i].Z) && float.IsFinite(cur[i].Heading));

                // Позиция на полилинии ребра (в пределах её ограничивающего прямоугольника).
                int e = pw.P.Edge[i] & PedStore.StepMask;
                ref readonly PedEdgeData ed = ref pw.Net.Edges[e];
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                for (int k = ed.FirstPoint; k < ed.FirstPoint + ed.PointCount; k++)
                {
                    x0 = MathF.Min(x0, pw.Net.Points[k].X);
                    x1 = MathF.Max(x1, pw.Net.Points[k].X);
                    z0 = MathF.Min(z0, pw.Net.Points[k].Z);
                    z1 = MathF.Max(z1, pw.Net.Points[k].Z);
                }

                Assert.InRange(cur[i].X, x0 - 0.01f, x1 + 0.01f);
                Assert.InRange(cur[i].Z, z0 - 0.01f, z1 + 0.01f);
            }
            else if (!alive || st == PedState.PendingPath)
            {
                Assert.Equal(0, cur[i].Flags);
            }
        }

        Assert.True(active > 20);
        Assert.Equal(sim.Counters.PedActive, pw.P.AliveCount);
    }

    [Fact]
    public void PickAndInfo_RoundTrip()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        sim.Step(700);
        ReadOnlySpan<PedestrianRenderData> cur = sim.Pedestrians;
        int slot = -1;
        for (int i = 0; i < cur.Length; i++)
        {
            if ((cur[i].Flags & PedRenderFlags.Active) != 0)
            {
                slot = i;
                break;
            }
        }

        Assert.True(slot >= 0);
        PedestrianRenderData r = cur[slot];
        Assert.True(sim.TryPickPedestrian(r.X, r.Z, 0.5f, out PedHandle h));
        Assert.True(sim.IsAlive(h));
        Assert.True(sim.TryGetPedInfo(h, out PedInfo info));
        Assert.Equal(r.State, (byte)info.State);
        Assert.InRange(MathF.Abs(info.X - r.X), 0f, 0.001f);
        Assert.True(info.Resident >= 0);
        Assert.True(info.TripTicks >= 0);
        Assert.False(sim.TryPickPedestrian(-9999f, -9999f, 1f, out _));
        Assert.False(sim.TryGetPedInfo(new PedHandle(slot, h.Generation + 2), out _));
    }

    // ------------------------------------------------------------ команды

    [Fact]
    public void SetPedTarget_LimitsActivePedestrians()
    {
        ScenarioDesc d = Desc(count: 300, residents: 600, rampTicks: 1);
        using Simulation sim = Make(in d);
        sim.Enqueue(Command.MakeSetPedTarget(25));
        sim.Step(1200);
        Assert.Equal(25, sim.PedTarget);
        Assert.InRange(sim.ActivePedCount, 1, 25);
        sim.Enqueue(Command.MakeSetPedTarget(-1));
        sim.Step(1);
        Assert.Equal(1, sim.Counters.CommandsRejected);
    }

    // ------------------------------------------------------------ BuildRoad

    [Fact]
    public void BuildRoad_ResetsPedestrians_ResidentsStayConsistent_AndSaveRoundTrips()
    {
        ScenarioDesc d = Desc();
        using Simulation sim = Make(in d);
        sim.Step(900);
        PedWorld pw = sim.World.Peds!;
        Assert.True(pw.P.AliveCount > 20);
        var r = new BuildRoadCmd { Ax = 2000, Az = 500, Bx = 2150, Bz = 500, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        sim.Enqueue(Command.MakeBuildRoad(in r, tag: 9));
        sim.Step(1);
        Assert.Equal(1, sim.Counters.PedNetResets);
        Assert.Equal(1, sim.Counters.CommandsApplied);
        Assert.True(pw.P.AliveCount <= PedSpawn.PerTickLimit, $"alive {pw.P.AliveCount}");   // сброс в фазе команд, затем спавн этого же тика (не больше 64)
        Assert.Equal(pw.R.Count, pw.R.IdleCount() + pw.P.AliveCount);
        for (int i = 0; i < pw.R.Count; i++)
        {
            Assert.InRange(pw.R.HomeEdge[i], 0, pw.Net.SidewalkEdgeCount - 1);
            Assert.InRange(pw.R.WorkEdge[i], 0, pw.Net.SidewalkEdgeCount - 1);
        }

        sim.Step(900);
        Assert.True(pw.P.AliveCount > 20);
        Assert.Equal(0, sim.Counters.PedNoPath);

        byte[] file = WorldSaveFixtures.SaveBytes(sim);
        using Simulation back = WorldSaveFixtures.Load(file);
        Assert.Equal(sim.ComputeStateHash(), back.ComputeStateHash());
    }

    // ------------------------------------------------------------ сейв

    [Fact]
    public void SaveLoad_ContinuesWithSameHash_Threads1And2()
    {
        ScenarioDesc d = Desc();
        using Simulation a = Make(in d);
        a.Step(700);
        byte[] file = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(file);
        using Simulation c = WorldSaveFixtures.Load(file, threads: 2);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.True(a.World.Peds!.P.AliveCount > 20);
        Assert.Equal(a.ActivePedCount, b.ActivePedCount);
        Assert.True(file.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(b)), "повторный сейв загруженного мира должен совпасть побайтно");
        for (int i = 0; i < 6; i++)
        {
            a.Step(100);
            b.Step(100);
            c.Step(100);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
            Assert.Equal(a.ComputeStateHash(), c.ComputeStateHash());
        }

        Assert.True(b.Pedestrians.SequenceEqual(a.Pedestrians));
        Assert.True(b.PreviousPedestrians.Length > 0);
    }

    [Fact]
    public void SaveLoad_EveryTickAroundEvents_ContinuesWithSameHash()
    {
        ScenarioDesc d = Desc(count: 60, residents: 120);
        using Simulation a = Make(in d);
        a.Step(240);
        for (int k = 0; k < 24; k++)
        {
            a.Step(1);
            byte[] file = WorldSaveFixtures.SaveBytes(a);
            using Simulation b = WorldSaveFixtures.Load(file);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
            using Simulation fork = WorldSaveFixtures.Load(file);
            fork.Step(25);
            using Simulation orig = WorldSaveFixtures.Load(file);
            orig.Step(25);
            Assert.Equal(orig.ComputeStateHash(), fork.ComputeStateHash());
        }

        a.Step(25);
        using Simulation last = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(a));
        Assert.Equal(a.ComputeStateHash(), last.ComputeStateHash());
    }

    [Fact]
    public void CorruptedPedState_IsRejectedWithoutThrowing()
    {
        ScenarioDesc d = Desc(count: 60, residents: 120);
        using Simulation a = Make(in d);
        a.Step(400);
        byte[] good = WorldSaveFixtures.SaveBytes(a);
        Assert.True(a.World.Peds!.P.AliveCount > 5);

        // Портим по одному байту нагрузки блоков пешеходов: загрузка либо отказывает кодом, либо принимает допустимое состояние; исключений нет.
        string[] blocks = { "PEDS", "PPTH", "RESI", "XING", "PQUE" };
        int rejected = 0;
        for (int bi = 0; bi < blocks.Length; bi++)
        {
            uint id = SaveFormat.FourCc(System.Text.Encoding.ASCII.GetBytes(blocks[bi]));
            (int off, int len) = FindBlock(good, id);
            var rng = Pcg32.Create(5, (ulong)bi);
            for (int t = 0; t < 40; t++)
            {
                byte[] bad = (byte[])good.Clone();
                int pos = off + (int)rng.NextBelow((uint)len);
                bad[pos] ^= (byte)(1 << (int)rng.NextBelow(8));
                var o = new LoadOptions { StrictHash = 0 };
                LoadStatus st = Simulation.Load(bad, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out _);
                if (st == LoadStatus.Ok)
                {
                    sim!.Step(5);
                    sim.Dispose();
                }
                else
                {
                    rejected++;
                }
            }
        }

        Assert.True(rejected > 20, $"rejected {rejected} of 200");
    }

    private static (int Offset, int Length) FindBlock(byte[] file, uint id)
    {
        int blocks = (int)BitConverter.ToUInt32(file, 36);
        for (int i = 0; i < blocks; i++)
        {
            int e = 64 + (i * 32);
            if (BitConverter.ToUInt32(file, e) == id)
            {
                return ((int)BitConverter.ToInt64(file, e + 8), (int)BitConverter.ToInt64(file, e + 16));
            }
        }

        throw new InvalidOperationException("block not found");
    }

    // ------------------------------------------------------------ сценарии *-peds

    [Theory]
    [InlineData("s1k-peds", 2000, 4000, 2600)]
    [InlineData("s10k-peds", 20000, 40000, 26000)]
    public void PedsScenarioFiles_LoadWithSpecParameters(string name, int count, int residents, int maxPeds)
    {
        Sim.Scenarios.LoadedScenario sc = Sim.Scenarios.ScenarioLoader.LoadInfo(System.IO.Path.Combine(Gen.GenFixture.RepoRoot(), "scenarios", name + ".json"));
        Assert.True(sc.Desc.Peds.Enabled);
        Assert.Equal(count, sc.Desc.Peds.Count);
        Assert.Equal(residents, sc.Desc.Peds.Residents);
        Assert.Equal(maxPeds, sc.Desc.Capacities.MaxPeds);
        Assert.Equal(residents, sc.Desc.Capacities.MaxResidents);
        Assert.Equal(64, sc.Desc.Capacities.MaxPedPathSteps);
        Assert.Equal(PassengerModel.Agents, sc.Desc.Transit.PassengerModel);
        Assert.True(sc.Desc.Transit.Bidirectional);
        Assert.Equal(30 * 20, sc.Desc.Peds.ActivityMinTicks);
        Assert.Equal(120 * 20, sc.Desc.Peds.ActivityMaxTicks);
    }

    // ------------------------------------------------------------ s1k-peds из сценария

    [Fact]
    public void S1kPeds_Scenario_WalksWithoutStuckAndNoPathFailures()
    {
        string path = System.IO.Path.Combine(Gen.GenFixture.RepoRoot(), "scenarios", "s1k-peds.json");
        Sim.Scenarios.LoadedScenario sc = Sim.Scenarios.ScenarioLoader.LoadInfo(path);
        using Simulation sim = Simulation.Create(sc.Desc, new SimRuntimeOptions { WorkerThreads = 2 });
        Assert.Equal(PassengerModel.Agents, sc.Desc.Transit.PassengerModel);
        sim.Step(2500);
        PedWorld pw = sim.World.Peds!;
        Assert.Equal(4000, pw.R.Count);
        Assert.Equal(pw.R.Count, pw.R.IdleCount() + pw.P.AliveCount);
        Assert.InRange(pw.P.AliveCount, 1500, 2000);
        Assert.Equal(0, sim.Counters.PedNoPath);
        Assert.Equal(0, sim.Counters.StuckVehicles);
    }
}
