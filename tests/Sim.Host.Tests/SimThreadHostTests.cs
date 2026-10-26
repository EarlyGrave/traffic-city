using System;
using System.Diagnostics;
using System.Threading;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Host.Tests;

/// <summary>
/// <c>SimThreadHost</c> (ADR-0024, scale.md S9): кадры = прямые копии по тикам, хэш хоста = прямой прогон при командах, привязанных к тикам,
/// «последний выигрывает» для вида, почтовый ящик, эксклюзивный доступ, 0 B на обоих потоках, пауза и Dispose без утечки потоков.
/// Коллекция без параллельности: замеры аллокаций и потоков процесса.
/// </summary>
[Collection("HostThreads")]
public class SimThreadHostTests
{
    private static ScenarioDesc Desc(string name) => ScenarioLoader.Load(ScenarioLocator.Resolve(name, null));

    private static SimThreadHost Start(string name, SimHostOptions? opt = null, int workers = 1)
    {
        ScenarioDesc d = Desc(name);
        return new SimThreadHost(Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = workers }), opt);
    }

    private static Command SpawnCmd()
    {
        var t = new SpawnTripCmd { FromZone = 0, ToZone = 1, FromLane = -1, VehicleType = 0, Count = 3 };
        return Command.MakeSpawnTrip(in t, tag: 7);
    }

    private static SimFrame RunTo(SimThreadHost host, long tick)
    {
        host.SetTargetTick(tick);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (host.TryAcquireFrame(out SimFrame f) && f.SnapshotTick == tick)
            {
                return f;
            }

            Assert.True(host.State == HostState.Running, "хост остановился: " + host.Fault);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120), $"таймаут: tick={host.Tick}, target={tick}");
            Thread.Sleep(1);
        }
    }

    private static void ExpectCompact<T>(ReadOnlySpan<T> cur, ReadOnlySpan<T> prev, ReadOnlySpan<T> hostCur, ReadOnlySpan<T> hostPrev, Func<T, byte> flags)
        where T : struct
    {
        int k = 0;
        for (int i = 0; i < cur.Length; i++)
        {
            if (flags(cur[i]) == 0)
            {
                continue;
            }

            Assert.True(k < hostCur.Length, "кадр короче прямой копии");
            Assert.Equal(cur[i], hostCur[k]);
            Assert.Equal(i < prev.Length ? prev[i] : default, hostPrev[k]);
            k++;
        }

        Assert.Equal(k, hostCur.Length);
    }

    [Theory]
    [InlineData("s1k", 700)]
    [InlineData("s1k-peds", 1200)]
    public void Frame_IsDirectCopy_AtTick(string scenario, int ticks)
    {
        using SimThreadHost host = Start(scenario);
        SimFrame f = RunTo(host, ticks);
        using Simulation direct = Simulation.Create(Desc(scenario));
        direct.Step(ticks);

        Assert.Equal(direct.SnapshotTick, f.SnapshotTick);
        Assert.True(f.Vehicles.Length > 0);
        ExpectCompact(direct.Vehicles, direct.PreviousVehicles, f.Vehicles, f.PreviousVehicles, v => v.Flags);
        ExpectCompact(direct.Pedestrians, direct.PreviousPedestrians, f.Pedestrians, f.PreviousPedestrians, p => p.Flags);
        Assert.Equal(direct.ActiveVehicleCount, f.ActiveVehicleCount);
        direct.GetStats(out SimStats s);
        Assert.Equal(s.StuckVehicles, f.Stats.StuckVehicles);
        if (scenario == "s1k-peds")
        {
            Assert.True(f.Pedestrians.Length > 0);
        }
    }

    [Fact]
    public void StateHash_EqualsDirectRun_WithCommandsBoundToTicks_AndJournalRecordsApplyTick()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 100);
        Command c = SpawnCmd();
        Assert.True(host.TryEnqueue(in c));
        RunTo(host, 101); // команда применяется перед первым Step после её подачи
        RunTo(host, 300);

        JournalEntry[] journal = host.CopyJournal();
        Assert.Single(journal);
        Assert.Equal(100, journal[0].Tick);
        Assert.Equal(CommandKind.SpawnTrip, journal[0].Command.Kind);
        Assert.Equal(1, host.CommandsApplied);

        ulong hostHash;
        using (ExclusiveAccess ex = host.AcquireExclusive())
        {
            Assert.Equal(300, ex.Simulation!.Tick);
            hostHash = ex.Simulation.ComputeStateHash();
        }

        using Simulation direct = Simulation.Create(Desc("s1k"));
        direct.Step(100);
        direct.Enqueue(in c);
        direct.Step(200);
        Assert.Equal(direct.ComputeStateHash(), hostHash);
    }

    [Fact]
    public void StateHash_DoesNotDependOnWorkerThreads()
    {
        ulong h1, h3;
        using (SimThreadHost a = Start("s1k", workers: 1))
        {
            RunTo(a, 400);
            using ExclusiveAccess ex = a.AcquireExclusive();
            h1 = ex.Simulation!.ComputeStateHash();
        }

        using (SimThreadHost b = Start("s1k", workers: 3))
        {
            RunTo(b, 400);
            using ExclusiveAccess ex = b.AcquireExclusive();
            h3 = ex.Simulation!.ComputeStateHash();
        }

        Assert.Equal(h1, h3);
    }

    [Fact]
    public void Pause_TargetNotGrowing_TickStays_AndLagReflectsTarget()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 60);
        Thread.Sleep(150);
        Assert.Equal(60, host.Tick);
        Assert.Equal(0, host.TickLag);
        host.SetTargetTick(40); // ниже текущего - тоже пауза
        Thread.Sleep(50);
        Assert.Equal(60, host.Tick);
        RunTo(host, 70);
        Assert.Equal(70, host.Tick);
    }

    [Fact]
    public void RenderView_LastWins_FrameMatchesDirectWithFinalView()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 500);
        for (int i = 0; i < 2000; i++)
        {
            host.SetRenderView(-5000f + i, -5000f, -4000f, -4000f); // пустые виды вне сети
        }

        host.SetRenderView(-100000f, -100000f, 100000f, 100000f); // итог: всё охвачено
        host.SetRenderViewAll();
        host.SetRenderView(-1e9f, -1e9f, -1e9f + 1f, -1e9f + 1f); // итог: пусто
        SimFrame f = RunTo(host, 501);
        Assert.Equal(0, f.Vehicles.Length);

        host.SetRenderViewAll();
        SimFrame g = RunTo(host, 502);
        Assert.True(g.Vehicles.Length > 0);
        Assert.True(g.ViewVersion > f.ViewVersion);
        using Simulation direct = Simulation.Create(Desc("s1k"));
        direct.Step(502);
        int alive = 0;
        foreach (VehicleRenderData v in direct.Vehicles)
        {
            if (v.Flags != 0)
            {
                alive++;
            }
        }

        Assert.Equal(alive, g.Vehicles.Length);
    }

    [Fact]
    public void Requests_Mailbox_AnswersPick_InOrder()
    {
        using SimThreadHost host = Start("s1k");
        SimFrame f = RunTo(host, 600);
        Assert.True(f.Vehicles.Length > 0);
        VehicleRenderData v = f.Vehicles[0];
        long hit = host.TryRequestPickVehicle(v.X, v.Z, 2f);
        long miss = host.TryRequestPickVehicle(1e9f, 1e9f, 1f);
        long ped = host.TryRequestPickPedestrian(v.X, v.Z, 2f);
        Assert.True(hit > 0 && miss > hit && ped > miss);

        HostResponse r1 = default, r2 = default, r3 = default;
        var sw = Stopwatch.StartNew();
        int got = 0;
        while (got < 3 && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (host.TryReadResponse(out HostResponse r))
            {
                if (got == 0)
                {
                    r1 = r;
                }
                else if (got == 1)
                {
                    r2 = r;
                }
                else
                {
                    r3 = r;
                }

                got++;
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        Assert.Equal(3, got);
        Assert.Equal(hit, r1.RequestId);
        Assert.True(r1.Found);
        Assert.Equal(HostRequestKind.PickVehicle, r1.Kind);
        Assert.True(r1.Vehicle.Index >= 0);
        Assert.Equal(miss, r2.RequestId);
        Assert.False(r2.Found);
        Assert.Equal(ped, r3.RequestId);
        Assert.False(r3.Found); // в s1k нет пешеходов
        Assert.Equal(HostState.Running, host.State);
    }

    [Fact]
    public void ExclusiveAccess_StopsSteps_ReleasePublishesFreshFrame()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 100);
        host.SetTargetTick(100000);
        long frozen;
        using (ExclusiveAccess ex = host.AcquireExclusive())
        {
            frozen = ex.Simulation!.Tick;
            Thread.Sleep(100);
            Assert.Equal(frozen, ex.Simulation.Tick);
            Assert.Equal(frozen, host.Tick);
        }

        host.SetTargetTick(frozen); // остановить после проверки
        Thread.Sleep(100);
        Assert.True(host.Tick >= frozen);
        Assert.True(host.TryAcquireFrame(out SimFrame f) || f.SnapshotTick > 0);
    }

    [Fact]
    public void CommandRing_FullReturnsFalse_WhileThreadHeld()
    {
        using SimThreadHost host = Start("s1k", new SimHostOptions { CommandRingCapacity = 4 });
        RunTo(host, 10);
        Command c = SpawnCmd();
        using (ExclusiveAccess ex = host.AcquireExclusive())
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.True(host.TryEnqueue(in c));
            }

            Assert.False(host.TryEnqueue(in c));
            Assert.Equal(1, host.CommandsRingFull);
        }

        RunTo(host, 12);
        Assert.Equal(4, host.CommandsApplied);
        Assert.Equal(4, host.CopyJournal().Length);
        Assert.All(host.CopyJournal(), e => Assert.Equal(10, e.Tick));
    }

    [Fact]
    public void SteadyState_AllocatesZeroBytes_OnBothThreads()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 2600); // рампа s1k закончена, массивы кадров доросли
        long next = 2600;
        long simDelta = -1, mainDelta = -1;
        for (int attempt = 0; attempt < 30 && simDelta != 0; attempt++)
        {
            int gc = GC.CollectionCount(0);
            SimFrame f0 = RunTo(host, next += 20);
            long sim0 = f0.SimThreadAllocatedBytes;
            long main0 = GC.GetAllocatedBytesForCurrentThread();
            host.SetTargetTick(next + 200);
            long goal = next + 200;
            while (host.Tick < goal)
            {
                host.TryAcquireFrame(out _);
                host.TryReadResponse(out _);
            }

            host.TryAcquireFrame(out _);
            long main1 = GC.GetAllocatedBytesForCurrentThread();
            long sim1 = host.SimThreadAllocatedBytes;
            next = goal;
            if (GC.CollectionCount(0) == gc)
            {
                simDelta = sim1 - sim0;
                mainDelta = main1 - main0;
            }
        }

        Assert.Equal(0, mainDelta);
        Assert.Equal(0, simDelta);
    }

    [Fact]
    public void Dispose_StopsThread_NoLeak_AndIsIdempotent()
    {
        int before = Process.GetCurrentProcess().Threads.Count;
        for (int i = 0; i < 10; i++)
        {
            SimThreadHost host = Start("s1k", workers: 1);
            host.SetTargetTick(50);
            host.Dispose();
            host.Dispose();
            Assert.Equal(HostState.Stopped, host.State);
            Assert.False(host.TryEnqueue(SpawnCmd()));
        }

        // воркеры ядра и сам поток завершаются; допуск на пул/таймеры рантайма
        Thread.Sleep(200);
        int after = Process.GetCurrentProcess().Threads.Count;
        Assert.True(after <= before + 3, $"потоков было {before}, стало {after}");
    }

    [Fact]
    public void Dispose_WhileExclusiveHeld_DoesNotDeadlock()
    {
        SimThreadHost host = Start("s1k");
        RunTo(host, 20);
        ExclusiveAccess ex = host.AcquireExclusive();
        host.Dispose();
        Assert.Equal(HostState.Stopped, host.State);
        Assert.NotNull(ex.Simulation);
    }
}
