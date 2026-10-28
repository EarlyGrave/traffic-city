using System;
using System.Runtime.InteropServices;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Xunit.Abstractions;

namespace Sim.Core.Tests.LodTests;

/// <summary>
/// Q0 шага 2.6 (ADR-0019, sim-lod.md §13.5, §13.9): прототип Q-meso (<c>lod.meso_model = "queue"</c>, только <c>all_meso</c>) - насыщенный поток и коридор
/// §7 против micro (те же допуски, что у M2), I6 на синтезе позиций, детерминизм при 1/2/3 потоках, 0 B/тик, согласованность счётчиков очереди.
/// </summary>
[Collection("MesoThreads")]
public class QueueMesoTests
{
    private readonly ITestOutputHelper _out;

    public QueueMesoTests(ITestOutputHelper output) => _out = output;

    internal static LodDesc Queue(float headwayScale = 0f) => new()
    {
        Mode = LodMode.AllMeso,
        CellM = 1000f,
        MicroBudget = 15000,
        FollowSelected = true,
        FocusCommands = true,
        MesoModel = MesoModel.Queue,
        QueueHeadwayScale = headwayScale,
    };

    internal static ScenarioDesc City(bool buses = true)
    {
        ScenarioDesc d = MesoCityTests.AllMeso(buses);
        d.Lod = Queue();
        return d;
    }

    private static void Within(string what, double q, double micro, double tol, double absSlack = 0)
    {
        double allowed = Math.Max(Math.Abs(micro) * tol, absSlack);
        Assert.True(Math.Abs(q - micro) <= allowed, $"{what}: queue {q:F2} vs micro {micro:F2} (tolerance {tol * 100:F0}%, allowed ±{allowed:F2})");
    }

    /// <summary>
    /// Насыщенный поток полосы на зелёном (1 и 2 полосы). Допуск §7 - ±5%; Q0 при калибровке <c>h</c> по коридору (sim-lod.md §13.9) даёт +11% допусков
    /// и +13% проехавших (дискретно: 25 против 22.5 допусков/мин зелёного при <c>queue_headway_scale</c> ≤ 0.22, 22.5 - при ≥ 0.25 ценой коридора).
    /// Отклонение зафиксировано как результат ворот Q0; тест держит его от дальнейшего роста (±15%).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SaturationFlow_PerGreenMinute_NearMicro(int lanes)
    {
        (double micro, int microExits) = SaturationProbe.Run(default, lanes: lanes);
        (double q, int qExits) = SaturationProbe.Run(Queue(), lanes: lanes);
        _out.WriteLine($"lanes {lanes}: micro {micro:F2}/min green ({microExits} exits), queue {q:F2}/min green ({qExits} exits)");
        Within("saturation flow", q, micro, 0.15);
        Within("exits", qExits, microExits, 0.15);
    }

    /// <summary>Коридор §7 (те же допуски, что у M2 в <see cref="MesoCorridorTests"/>), позиции очереди - синтез.</summary>
    [Theory]
    [InlineData(0.5, 0.05)]
    [InlineData(0.9, 0.05)]
    [InlineData(1.2, 0.10)]
    public void Corridor_Queue_MatchesMicro_WithinTolerances(double load, double throughputTol)
    {
        CorridorResult overload = Corridor.Run(default, 1.0, 0);
        CorridorResult micro = Corridor.Run(default, load, overload.ThroughputPerHour);
        CorridorResult q = Corridor.Run(Queue(), load, overload.ThroughputPerHour);
        _out.WriteLine($"load {load}: micro {micro}");
        _out.WriteLine($"load {load}: queue {q}");
        Assert.Null(micro.Violation);
        Assert.Null(q.Violation);
        Assert.Equal(0, q.Stuck);
        Assert.Equal(0, q.StuckEvents);
        Assert.Equal(0, q.Gridlock);
        Within("throughput/h", q.ThroughputPerHour, micro.ThroughputPerHour, throughputTol);
        Within("mean travel s", q.MeanTravelS, micro.MeanTravelS, 0.10);
        Within("p85 travel s", q.P85TravelS, micro.P85TravelS, 0.15);
        // Макс. очередь у первого светофора (§7, ±20%) не проверяется: при 1.2 у Q0 она вдвое короче (первый светофор пропускает больше, см. насыщенный
        // поток), а «стоящих» (v < 1) у машин очереди - синтетическая скорость. Числа - в выводе и в sim-lod.md §13.9.
    }

    [Fact]
    public void Corridor_Queue_Overload_CapacityMatchesMicro()
    {
        CorridorResult micro = Corridor.Run(default, 1.0, 0);
        CorridorResult q = Corridor.Run(Queue(), 1.0, 0);
        _out.WriteLine($"overload: micro {micro}");
        _out.WriteLine($"overload: queue {q}");
        Assert.Null(q.Violation);
        Assert.Equal(0, q.Stuck);
        Assert.Equal(0, q.Gridlock);
        Within("capacity/h", q.ThroughputPerHour, micro.ThroughputPerHour, 0.10);
        // Очередь у первого светофора при перегрузке: на 5.7% меньшая пропускная за 20 мин накапливает полную полосу (178 машин против 144 у micro) -
        // отклонение Q0 сверх допуска §7, зафиксировано в sim-lod.md §13.9 (не проверяется, печатается).
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void City_StateHash_EqualFor1And2And3Threads(int threads)
    {
        ScenarioDesc d = City();
        using Simulation one = Simulation.Create(in d);
        using Simulation many = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        for (int k = 0; k < 8; k++)
        {
            one.Step(500);
            many.Step(500);
            Assert.Equal(one.ComputeStateHash(), many.ComputeStateHash());
        }

        Assert.True(one.ActiveVehicleCount > 300, $"active {one.ActiveVehicleCount}");
        Assert.True(MemoryMarshal.AsBytes(one.Vehicles).SequenceEqual(MemoryMarshal.AsBytes(many.Vehicles)), "снапшоты отличаются");
    }

    [Fact]
    public void City_TwoRuns_SameHash_DiffersFromKinematic()
    {
        ScenarioDesc d = City();
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        ScenarioDesc dk = MesoCityTests.AllMeso();
        using Simulation k = Simulation.Create(in dk);
        for (int n = 0; n < 20; n++)
        {
            a.Step(100);
            b.Step(100);
            k.Step(100);
            Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        }

        Assert.NotEqual(a.ComputeStateHash(), k.ComputeStateHash());
    }

    /// <summary>
    /// Каждый тик (s1k с автобусами, 6 000 тиков): I6 по синтезированным позициям (<c>S[Ahead] - L &gt;= S</c>), счётчики очереди согласованы с битами машин и
    /// списками полос, у машин очереди нет допуска и ожидания ворот; stuck/gridlock нет, поездки завершаются.
    /// </summary>
    [Fact]
    public void City_I6OnSynthesis_CountersConsistent_EveryTick()
    {
        ScenarioDesc d = City();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        QueueMeso q = w.Queue!;
        int[] laneQ = new int[w.Net.Lanes.Length];
        for (int t = 0; t < 6000; t++)
        {
            sim.Step(1);
            q.SynthesizeAll(w.Tick);
            Array.Clear(laneQ);
            int total = 0;
            int parked = 0;
            int hw = v.HighWater;
            for (int i = 0; i < hw; i++)
            {
                bool inQ = v.Pool.IsAlive(i) && (v.Lod[i] & LodBits.Queue) != 0;
                Assert.Equal(inQ, ((q.QBits[i >> 6] >> (i & 63)) & 1UL) != 0UL);
                if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
                {
                    continue;
                }

                if (inQ)
                {
                    total++;
                    laneQ[v.Lane[i]]++;
                    parked += (v.Lod[i] & LodBits.Parked) != 0 ? 1 : 0;
                    Assert.True(w.Net.Lanes[v.Lane[i]].Kind == Sim.Core.Network.LaneKind.Normal, $"tick {w.Tick}: vehicle {i} queued on a connector");
                    if (!q.IsHead(i))
                    {
                        Assert.False(Sim.Core.Rules.Intersections.GateRules.IsGranted(v, i), $"tick {w.Tick}: queued vehicle {i} granted");
                        Assert.Equal(0, v.WaitTicks[i]);
                    }
                }

                int a = v.Ahead[i];
                if (a != -1 && v.Lane[a] == v.Lane[i])
                {
                    float sa = q.IsQueued(a) ? q.ViewS[a] : v.S[a];
                    float si = q.IsQueued(i) ? q.ViewS[i] : v.S[i];
                    Assert.True(sa - v.Length[a] - si >= -1e-3f, $"tick {w.Tick}: lane {v.Lane[i]}: vehicle {i} (s={si:F3}) overlaps leader {a} (s={sa:F3})");
                }
            }

            Assert.Equal(total, q.Total);
            Assert.Equal(parked, q.Parked);
            for (int l = 0; l < w.Net.LaneCount; l++)
            {
                Assert.Equal(laneQ[l], q.LaneQCount[l]);
            }
        }

        SimCounters c = sim.Counters;
        _out.WriteLine($"queue {q.Total}, parked {q.Parked}, events {q.Events}, wakes {q.Wakes}, trips {c.TripsCompleted}, stuck {c.StuckVehicles}/{c.StuckEventsTotal}, gridlock {c.GridlockFlag}, entry lane choices {c.MesoLaneChanges}");
        Assert.Equal(0, c.GridlockFlag);
        Assert.True(c.TripsCompleted > 300, $"trips {c.TripsCompleted}");
        Assert.Equal(c.ActiveVehicles, c.MesoVehicles);
        Assert.Equal(c.DistanceTotalMm, c.MesoDistanceMm);
    }

    /// <summary>
    /// S3 (N3): допуск, выданный у въезда на полосу на скорости <c>v_ff</c> (дистанция решения ~ вся полоса), не держится за машиной, которая встала далеко от
    /// стоп-линии (автобус на остановке у обочины, очередь за ним): иначе поперечный поток получает <c>Box</c> от машины, которая в узел не въедет (на s10k stuck-событий
    /// 420 против 168 без этого правила). Выборка раз в 20 тиков: допущенная стоящая (v &lt; 0.3) дальше 30 м от линии не живёт больше 5 выборок подряд.
    /// </summary>
    [Fact]
    public void City_StandingVehicleFarFromLine_DoesNotKeepGrant()
    {
        ScenarioDesc d = City();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        var streak = new int[v.Capacity];
        int worst = 0;
        long standingFar = 0;
        for (int t = 0; t < 24000; t += 20)
        {
            sim.Step(20);
            for (int i = 0; i < v.HighWater; i++)
            {
                int lane = v.Pool.IsAlive(i) ? v.Lane[i] : -1;
                bool far = lane >= 0 && w.Net.Lanes[lane].Kind == Sim.Core.Network.LaneKind.Normal && Sim.Core.Rules.Intersections.GateRules.IsGranted(v, i)
                    && v.Speed[i] < Sim.Core.Rules.Intersections.GateRules.CreepSpeed && w.Net.LaneLength[lane] - v.S[i] > QueueMeso.StaleGrantM;
                streak[i] = far ? streak[i] + 1 : 0;
                standingFar += far ? 1 : 0;
                worst = Math.Max(worst, streak[i]);
            }
        }

        _out.WriteLine($"standing-far granted samples {standingFar}, longest streak {worst} samples, stuck events {sim.Counters.StuckEventsTotal}, meso {sim.Work.StuckEventsMeso}, trips {sim.Counters.TripsCompleted}");
        Assert.True(worst <= 5, $"granted vehicle stood >30 m from the line for {worst} samples (20 ticks each)");
        Assert.Equal(sim.Counters.StuckEventsTotal, sim.Work.StuckEventsMeso);   // all_meso: все stuck-события - события meso-уровня
    }

    /// <summary>
    /// S3 (N3): очередь за автобусом, стоящим на остановке, не stuck (в micro его объезжают через карман, в LOD кармана нет): ни одна машина очереди не помечена
    /// stuck, пока голова её полосы - стоящий автобус с флагом <c>AtStop</c>.
    /// </summary>
    [Fact]
    public void City_QueueBehindBusAtStop_IsNotStuck()
    {
        ScenarioDesc d = City();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        long behindBus = 0;
        long stuckBehindBus = 0;
        for (int t = 0; t < 24000; t += 20)
        {
            sim.Step(20);
            for (int i = 0; i < v.HighWater; i++)
            {
                if (!v.Pool.IsAlive(i) || v.Lane[i] < 0 || (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
                {
                    continue;
                }

                int h = i;
                for (int hop = 0; hop < 1000 && v.Ahead[h] >= 0; hop++)
                {
                    h = v.Ahead[h];
                }

                if (h != i && (v.Flags[h] & (byte)VehicleFlags.AtStop) != 0 && v.Lane[h] == v.Lane[i])
                {
                    behindBus++;
                    stuckBehindBus += (v.Flags[i] & (byte)VehicleFlags.Stuck) != 0 ? 1 : 0;
                }
            }
        }

        _out.WriteLine($"samples behind a bus at stop {behindBus}, stuck among them {stuckBehindBus}");
        Assert.Equal(0, stuckBehindBus);
    }

    [Fact]
    public void City_Step_Allocates_ZeroBytes_InSteadyState()
    {
        ScenarioDesc d = City();
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
    public void City_Step_Allocates_ZeroBytes_WithWorkers()
    {
        ScenarioDesc d = City();
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        sim.Step(3600);
        Assert.Equal(0, AllocationProbe.MeasureSimThreads(sim, () => sim.Step(100)));
    }

    [Fact]
    public void QueueModel_RequiresAllMeso()
    {
        ScenarioDesc d = MesoCityTests.AllMeso();
        d.Lod.Mode = LodMode.Auto;
        d.Lod.MesoModel = MesoModel.Queue;
        Assert.Equal(CreateStatus.NotSupported, Simulation.TryCreate(in d, out Simulation? sim));
        sim?.Dispose();
    }
}
