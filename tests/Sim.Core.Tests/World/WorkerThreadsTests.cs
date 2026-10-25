using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Rules;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Util;

namespace Sim.Core.Tests.Worlds;

/// <summary>Коллекция без параллельности: GC.GetTotalAllocatedBytes и счётчик потоков видят весь процесс, чужие тесты их портят.</summary>
[CollectionDefinition("WorkerThreads", DisableParallelization = true)]
public sealed class WorkerThreadsDefinition
{
}

/// <summary>
/// ADR-0017 / hotpath.md §8.5 (P6): исполнитель диапазонов и фаза 5 на воркерах. Один класс-коллекция: тесты считают потоки и
/// аллокации всего процесса, поэтому не должны идти параллельно друг другу (других создателей пулов в этой сборке нет).
/// </summary>
[Collection("WorkerThreads")]
public class WorkerThreadsTests
{
    private sealed class FillJob : IRangeJob
    {
        public readonly int[] Marks;
        public readonly long[] PerWorker;

        public FillJob(int n, int workers)
        {
            Marks = new int[n];
            PerWorker = new long[workers];
        }

        public void RunRange(int worker, int begin, int end)
        {
            for (int i = begin; i < end; i++)
            {
                Marks[i]++;
            }

            PerWorker[worker] += end - begin;
        }
    }

    [Theory]
    [InlineData(2, 1000)]
    [InlineData(3, 2561)]
    [InlineData(4, 255)]
    [InlineData(2, 0)]
    public void Pool_CoversEveryIndexExactlyOnce_EveryRun(int workers, int count)
    {
        using var pool = new WorkerPool(workers);
        var job = new FillJob(Math.Max(count, 1), workers);
        pool.Wake();
        const int runs = 3000;
        for (int r = 0; r < runs; r++)
        {
            pool.Run(job, count);
        }

        pool.Rest();
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(runs, job.Marks[i]);
        }

        long total = 0;
        foreach (long x in job.PerWorker)
        {
            total += x;
        }

        Assert.Equal((long)runs * count, total);
    }

    [Fact]
    public void Pool_WorksAcrossWakeRestCycles_AndDisposeIsIdempotent()
    {
        var pool = new WorkerPool(3);
        var job = new FillJob(2000, 3);
        for (int k = 0; k < 20; k++)
        {
            pool.Wake();
            pool.Run(job, 2000);
            pool.Rest();
            pool.Run(job, 2000); // и без Wake (воркеры спят): главный делает всё сам
        }

        for (int i = 0; i < 2000; i++)
        {
            Assert.Equal(40, job.Marks[i]);
        }

        pool.Dispose();
        pool.Dispose();
    }

    [Fact]
    public void Options_DefaultAndOne_CreateNoThreads_AboveCapIsClamped()
    {
        ScenarioDesc d = Step3.S1k();
        int before = WorkerPool.LiveThreads;
        using (Simulation s = Simulation.Create(in d))
        {
            s.Step(5);
            Assert.Equal(before, WorkerPool.LiveThreads);
        }

        using (Simulation s = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 }))
        {
            Assert.Equal(before, WorkerPool.LiveThreads);
        }

        using (Simulation s = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1000 }))
        {
            Assert.Equal(before + SimRuntimeOptions.MaxWorkerThreads - 1, WorkerPool.LiveThreads);
        }

        Assert.Equal(before, WorkerPool.LiveThreads);
    }

    [Fact]
    public void CreateDispose_50Cycles_WithTwoThreads_DoesNotLeakThreads()
    {
        ScenarioDesc d = Step3.S1k();
        int before = WorkerPool.LiveThreads;
        for (int k = 0; k < 50; k++)
        {
            using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
            Assert.Equal(before + 1, WorkerPool.LiveThreads);
            sim.Step(3);
        }

        Assert.Equal(before, WorkerPool.LiveThreads);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Phase5_ReverseOrder_GivesBitwiseSameOutputs_AndThreadsGiveSameWork(int threads)
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(3000);

        // тот же хэш и те же счётчики работы при пуле воркеров (до ручного прогона фазы 5: он меняет Accel относительно конца тика)
        using (Simulation sim2 = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads }))
        {
            sim2.Step(3000);
            Assert.Equal(sim.ComputeStateHash(), sim2.ComputeStateHash());
            Assert.Equal(sim.Work, sim2.Work);
        }

        World w = sim.World;
        int hw = w.Vehicles.HighWater;
        Assert.True(w.Vehicles.AliveCount > 500, "нужна загруженная сеть");

        SimWork saved = w.Work;
        w.Work = default;
        BehaviourPhase.Run(w);
        SimWork forward = w.Work;
        w.Work = saved;
        byte[][] fwd = Outputs(w, hw);

        Poison(w, hw);
        BehaviourJob job = BehaviourPhase.Prepare(w);
        for (int i = hw - 1; i >= 0; i--)
        {
            job.RunRange(0, i, i + 1);
        }

        SimWork reverseSum = default;
        job.Collect(ref reverseSum);
        byte[][] rev = Outputs(w, hw);
        for (int k = 0; k < fwd.Length; k++)
        {
            Assert.True(fwd[k].AsSpan().SequenceEqual(rev[k]), $"выход {k} отличается при обратном обходе");
        }

        Assert.Equal(forward, reverseSum);
    }

    // P7: HardClamps и снапшот (текущий и предыдущий спаны) побитово равны при 1 и 2 потоках.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Phases6And10_HardClampsAndSnapshot_EqualForOneAndManyThreads(int threads)
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation one = Simulation.Create(in d);
        using Simulation many = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        for (int k = 0; k < 6; k++)
        {
            one.Step(500);
            many.Step(500);
            Assert.Equal(one.Counters.HardClamps, many.Counters.HardClamps);
            Assert.True(MemoryMarshal.AsBytes(one.Vehicles).SequenceEqual(MemoryMarshal.AsBytes(many.Vehicles)), "cur-снапшот отличается");
            Assert.True(MemoryMarshal.AsBytes(one.PreviousVehicles).SequenceEqual(MemoryMarshal.AsBytes(many.PreviousVehicles)), "prev-снапшот отличается");
        }

        Assert.True(one.Vehicles.Length > WorkerPool.ChunkSize / 2);
        Assert.True(one.Counters.HardClamps > 0, "тест должен ловить клэмпы");
        Assert.Equal(one.ComputeStateHash(), many.ComputeStateHash());
    }

    // P7: фазы 6 и 10 - функции машины: обратный обход даёт те же выходы и ту же сумму HardClamps.
    [Fact]
    public void Phases6And10_ReverseOrder_GivesBitwiseSameOutputs()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(3000);
        World w = sim.World;
        int hw = w.Vehicles.HighWater;
        Sim.Core.Agents.VehicleStore v = w.Vehicles;
        Assert.True(v.AliveCount > 500, "нужна загруженная сеть");

        // Фаза 6: вход - зазоры фазы 5 на состоянии конца тика; сохраняем S/Speed/StillTicks, прогоняем вперёд, откатываем, прогоняем назад.
        BehaviourPhase.Run(w);
        float[] s0 = v.S.AsSpan(0, hw).ToArray();
        float[] sp0 = v.Speed.AsSpan(0, hw).ToArray();
        ushort[] st0 = v.StillTicks.AsSpan(0, hw).ToArray();
        SimCounters fwdC = default;
        Sim.Core.Agents.IntegrateJob job6 = Sim.Core.Agents.IntegratePhase.Prepare(w);
        job6.RunRange(0, 0, hw);
        job6.Collect(ref fwdC);
        byte[][] fwd6 = { Bytes(v.S, hw), Bytes(v.Speed, hw), Bytes(v.StillTicks, hw) };
        s0.CopyTo(v.S, 0);
        sp0.CopyTo(v.Speed, 0);
        st0.CopyTo(v.StillTicks, 0);
        job6 = Sim.Core.Agents.IntegratePhase.Prepare(w);
        for (int i = hw - 1; i >= 0; i--)
        {
            job6.RunRange(0, i, i + 1);
        }

        SimCounters revC = default;
        job6.Collect(ref revC);
        byte[][] rev6 = { Bytes(v.S, hw), Bytes(v.Speed, hw), Bytes(v.StillTicks, hw) };
        for (int k = 0; k < fwd6.Length; k++)
        {
            Assert.True(fwd6[k].AsSpan().SequenceEqual(rev6[k]), $"выход фазы 6 №{k} отличается при обратном обходе");
        }

        Assert.Equal(fwdC.HardClamps, revC.HardClamps);

        // Фаза 10: тот же буфер, отравленный, заполняется в обратном порядке.
        VehicleRenderData[] buf = w.Render[(int)((w.Tick + 1) & 1)];
        var job10 = new SnapshotJob();
        job10.Prepare(w, buf);
        job10.RunRange(0, 0, hw);
        byte[] fwd10 = Bytes(buf, hw);
        MemoryMarshal.AsBytes(buf.AsSpan(0, hw)).Fill(0xFF);
        for (int i = hw - 1; i >= 0; i--)
        {
            job10.RunRange(0, i, i + 1);
        }

        Assert.True(fwd10.AsSpan().SequenceEqual(Bytes(buf, hw)), "снапшот отличается при обратном обходе");
    }

    [Fact]
    public void Step_ZeroAllocations_WithTwoThreads_TotalProcessBytes()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        sim.Step(1500);
        Assert.True(sim.World.Vehicles.HighWater > WorkerPool.ChunkSize, "должно быть больше одного чанка");

        // Поток теста + потоки пула; фоновые потоки рантайма не считаются (AllocationProbe.MeasureSimThreads).
        Assert.Equal(0, AllocationProbe.MeasureSimThreads(sim, () => sim.Step(100)));
    }

    private static byte[][] Outputs(World w, int hw)
    {
        Sim.Core.Agents.VehicleStore v = w.Vehicles;
        return new[]
        {
            Bytes(v.Accel, hw),
            Bytes(v.LaneChangeDir, hw),
            Bytes(v.ClampLeadGap, hw),
            Bytes(v.ClampFlags, hw),
            Bytes(v.ClampGateGap, hw),
        };
    }

    private static byte[] Bytes<T>(T[] a, int n)
        where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(a, 0, n)).ToArray();

    private static void Poison(World w, int hw)
    {
        Sim.Core.Agents.VehicleStore v = w.Vehicles;
        Array.Fill(v.Accel, 123f, 0, hw);
        Array.Fill(v.LaneChangeDir, (sbyte)7, 0, hw);
        Array.Fill(v.ClampLeadGap, 123f, 0, hw);
        Array.Fill(v.ClampFlags, (byte)255, 0, hw);
        Array.Fill(v.ClampGateGap, 123f, 0, hw);
    }
}
