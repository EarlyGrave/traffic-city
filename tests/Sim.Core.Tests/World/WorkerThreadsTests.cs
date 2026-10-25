using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Rules;
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

    [Fact]
    public void Step_ZeroAllocations_WithTwoThreads_TotalProcessBytes()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        sim.Step(1500);
        Assert.True(sim.World.Vehicles.HighWater > WorkerPool.ChunkSize, "должно быть больше одного чанка");

        // GetTotalAllocatedBytes видит и воркеров, но и чужие потоки: берём минимум по окнам (реальная аллокация ядра есть в каждом).
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
