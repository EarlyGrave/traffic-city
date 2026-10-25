using System;
using System.Runtime.InteropServices;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Rules;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Xunit.Abstractions;

namespace Sim.Core.Tests.LodTests;

/// <summary>Коллекция без параллельности: тесты с воркерами считают аллокации всего процесса (как <c>WorkerThreads</c>).</summary>
[CollectionDefinition("MesoThreads", DisableParallelization = true)]
public sealed class MesoThreadsDefinition
{
}

/// <summary>
/// L2 шага 2.6 (ADR-0013, sim-lod.md §2, §7, §9): meso-динамика в режиме <c>all_meso</c> на городе s1k (с автобусами): детерминизм при 1/2/3
/// потоках, 0 B/тик, I6 и согласованность списков каждый тик, нет телепортов, флаги и счётчики уровней, чистота фазы 5 по машине.
/// </summary>
[Collection("MesoThreads")]
public class MesoCityTests
{
    private readonly ITestOutputHelper _out;

    public MesoCityTests(ITestOutputHelper output) => _out = output;

    internal static ScenarioDesc AllMeso(bool buses = true)
    {
        ScenarioDesc d = Step2.S1k(buses);
        d.Capacities.MaxRoutes = 8192;
        d.Lod = new LodDesc
        {
            Mode = LodMode.AllMeso,
            CellM = 1000f,
            MicroBudget = 15000,
            FollowSelected = true,
            FocusCommands = true,
        };
        return d;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AllMeso_StateHash_EqualFor1And2And3Threads(int threads)
    {
        ScenarioDesc d = AllMeso();
        using Simulation one = Simulation.Create(in d);
        using Simulation many = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        for (int k = 0; k < 8; k++)
        {
            one.Step(500);
            many.Step(500);
            Assert.Equal(one.ComputeStateHash(), many.ComputeStateHash());
            Assert.Equal(one.Counters.MesoClamps, many.Counters.MesoClamps);
        }

        Assert.True(one.ActiveVehicleCount > 300, $"active {one.ActiveVehicleCount}");
        Assert.True(MemoryMarshal.AsBytes(one.Vehicles).SequenceEqual(MemoryMarshal.AsBytes(many.Vehicles)), "снапшоты отличаются");
    }

    [Fact]
    public void AllMeso_TwoRuns_SameHashEveryHundredTicks()
    {
        ScenarioDesc d = AllMeso();
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        for (int k = 0; k < 30; k++)
        {
            a.Step(100);
            b.Step(100);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }
    }

    [Fact]
    public void AllMeso_EveryVehicleIsMeso_FlagsAndCountersAgree()
    {
        ScenarioDesc d = AllMeso();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(1800);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        int alive = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            alive++;
            Assert.True((v.Lod[i] & LodBits.Meso) != 0, $"vehicle {i} is micro in all_meso");
            Assert.True((sim.Vehicles[i].Flags & (byte)VehicleRenderFlags.Meso) != 0, $"vehicle {i}: no Meso render flag");
            Assert.Equal(0f, v.Accel[i]);
            Assert.True(v.MesoNext[i] < 0f, "MesoNext не сброшен к концу тика");
        }

        SimCounters c = sim.Counters;
        Assert.True(alive > 300);
        Assert.Equal(alive, c.MesoVehicles);
        Assert.Equal(0, c.MicroVehicles);
        Assert.True(c.MesoVehicleTicks > 0 && c.MicroVehicleTicks == 0);
        Assert.True(c.MesoDistanceMm > 0 && c.MicroDistanceMm == 0);
        Assert.Equal(c.DistanceTotalMm, c.MesoDistanceMm);
        _out.WriteLine($"alive {alive}, meso clamps {c.MesoClamps}, meso lane changes {c.MesoLaneChanges}, trips {c.TripsCompleted}, stuck {c.StuckVehicles}");
    }

    /// <summary>I6 и согласованность списков каждый тик, нет телепортов (|ΔS| за тик ≤ MaxSpeed·dt), stuck и gridlock нет.</summary>
    [Fact]
    public void AllMeso_I6_NoTeleports_NoStuck_EveryTick()
    {
        ScenarioDesc d = AllMeso();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        var prevS = new float[v.Capacity];
        var prevLane = new int[v.Capacity];
        var prevGen = new int[v.Capacity];
        float maxStep = 0f;
        for (int t = 0; t < 7200; t++)
        {
            int hw0 = v.HighWater;
            for (int i = 0; i < hw0; i++)
            {
                prevS[i] = v.S[i];
                prevLane[i] = v.Lane[i];
                prevGen[i] = v.Pool.IsAlive(i) ? v.Pool.GenerationOf(i) : -1;
            }

            sim.Step(1);
            string? gap = Step2.CheckGaps(w);
            Assert.True(gap == null, gap);
            int hw = v.HighWater;
            for (int i = 0; i < hw; i++)
            {
                if (!v.Pool.IsAlive(i) || v.Lane[i] < 0 || prevGen[i] != v.Pool.GenerationOf(i) || prevLane[i] != v.Lane[i])
                {
                    continue;
                }

                float ds = v.S[i] - prevS[i];
                maxStep = MathF.Max(maxStep, ds);
                Assert.True(ds >= -1e-4f, $"tick {w.Tick}: vehicle {i} moved back {ds}");
                Assert.True(ds <= (w.Types[v.Type[i]].MaxSpeed * 0.05f) + 1e-3f, $"tick {w.Tick}: vehicle {i} jumped {ds} m");
            }
        }

        SimCounters c = sim.Counters;
        _out.WriteLine($"max step {maxStep:F3} m, stuck {c.StuckVehicles}, stuck events {c.StuckEventsTotal}, gridlock {c.GridlockFlag}, trips {c.TripsCompleted}, avg speed {c.DistanceTotalMm / 1000.0 / (c.VehicleTicksTotal * 0.05) * 3.6:F1} km/h, meso clamps {c.MesoClamps}, lane changes {c.LaneChanges}");
        Assert.Equal(0, c.StuckVehicles);
        Assert.Equal(0, c.GridlockFlag);
        Assert.True(c.TripsCompleted > 100);
    }

    [Fact]
    public void AllMeso_Step_Allocates_ZeroBytes_InSteadyState()
    {
        ScenarioDesc d = AllMeso();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(3600);
        long bytes = AllocationProbe.Measure(() =>
        {
            for (int t = 0; t < 400; t++)
            {
                sim.Step(1);
            }
        });
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void AllMeso_Step_Allocates_ZeroBytes_WithWorkers()
    {
        ScenarioDesc d = AllMeso();
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        sim.Step(3600);

        // GetTotalAllocatedBytes видит и воркеров, и чужие потоки (JIT): минимум по окнам - реальная аллокация ядра есть в каждом.
        long best = long.MaxValue;
        for (int attempt = 0; attempt < 50 && best != 0; attempt++)
        {
            long a = GC.GetTotalAllocatedBytes(true);
            sim.Step(100);
            long b = GC.GetTotalAllocatedBytes(true);
            best = Math.Min(best, b - a);
        }

        Assert.Equal(0, best);
    }

    /// <summary>Контракт ADR-0017 для meso-ветки фазы 5: выходы по машине не зависят от порядка обхода индексов.</summary>
    [Fact]
    public void AllMeso_Phase5_ReverseOrder_GivesBitwiseSameOutputs()
    {
        ScenarioDesc d = AllMeso();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(2000);
        World w = sim.World;
        int hw = w.Vehicles.HighWater;
        Assert.True(w.Vehicles.AliveCount > 300);

        VehicleStore v = w.Vehicles;
        float[] allow0 = (float[])v.MesoAllow.Clone();
        float[] next0 = (float[])v.MesoNext.Clone();
        sbyte[] dir0 = (sbyte[])v.LaneChangeDir.Clone();
        byte[] lod0 = (byte[])v.Lod.Clone();
        SimWork saved = w.Work;
        BehaviourPhase.Run(w);
        byte[][] fwd = Outputs(w, hw);
        Array.Copy(allow0, v.MesoAllow, allow0.Length);
        Array.Copy(next0, v.MesoNext, next0.Length);
        Array.Copy(dir0, v.LaneChangeDir, dir0.Length);
        Array.Copy(lod0, v.Lod, lod0.Length);
        BehaviourJob job = BehaviourPhase.Prepare(w);
        for (int i = hw - 1; i >= 0; i--)
        {
            job.RunRange(0, i, i + 1);
        }

        SimWork scratch = default;
        job.Collect(ref scratch);
        w.Work = saved;
        byte[][] rev = Outputs(w, hw);
        for (int k = 0; k < fwd.Length; k++)
        {
            Assert.True(fwd[k].AsSpan().SequenceEqual(rev[k]), $"выход {k} отличается при обратном обходе");
        }
    }

    private static byte[][] Outputs(World w, int hw)
    {
        VehicleStore v = w.Vehicles;
        var alive = new bool[hw];
        for (int i = 0; i < hw; i++)
        {
            alive[i] = v.Pool.IsAlive(i) && v.Lane[i] >= 0;
        }

        byte[] Pick<T>(T[] a)
            where T : unmanaged
        {
            byte[] raw = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(a, 0, hw)).ToArray();
            int sz = raw.Length / Math.Max(1, hw);
            for (int i = 0; i < hw; i++)
            {
                if (!alive[i])
                {
                    Array.Clear(raw, i * sz, sz);
                }
            }

            return raw;
        }

        return new[] { Pick(v.MesoAllow), Pick(v.MesoNext), Pick(v.LaneChangeDir), Pick(v.Lod) };
    }
}
