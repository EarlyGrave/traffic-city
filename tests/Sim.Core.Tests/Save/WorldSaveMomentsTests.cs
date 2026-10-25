using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Save;

/// <summary>
/// T3, T4: сейв в «неудобные» моменты. Эталон - один непрерывный прогон со сценарием команд (по номеру тика); сейв из параллельного прогона в тике t,
/// <c>Load</c>, затем N тиков того же сценария - хэш обязан совпасть с эталоном на тике t + N, а <c>Save(Load(Save))</c> - побайтно с <c>Save</c>.
/// </summary>
public class WorldSaveMomentsTests
{
    private readonly ITestOutputHelper _out;

    public WorldSaveMomentsTests(ITestOutputHelper output) => _out = output;

    /// <summary>Что встретилось в моментах сейва (тест не проверяет «хоть раз»: он проверяет, что проверяемый мир действительно бывает в этих состояниях).</summary>
    private sealed class Coverage
    {
        public int ArrowSub, SubPhaseTransition, GapRunning, LeftYield, AdaptiveSlots;
        public int Wave, RecalcPending, Ghost, LaneChange, Rerouting, Refill, Trips, Commands, AtStop, Selected, Granted, Stuck, WaitTicks, Meso, Queue, Head, Parked, Buses;

        public void Observe(Simulation sim)
        {
            World w = sim.World;
            Wave += w.Overlay is { WaveActive: true } ? 1 : 0;
            RecalcPending += w.RecalcPending ? 1 : 0;
            Rerouting += w.Rerouting.Count > 0 ? 1 : 0;
            Refill += w.RefillQueue.Count > 0 ? 1 : 0;
            Trips += w.Trips.Count > 0 ? 1 : 0;
            Commands += w.Commands.Count > 0 ? 1 : 0;
            Selected += w.Vehicles.IsAlive(w.Selected) ? 1 : 0;
            SignalStore sg = w.Signals;
            for (int k = 0; k < sg.Count; k++)
            {
                if (sg.Machine[k] == 0)
                {
                    continue;
                }

                AdaptiveSlots++;
                ArrowSub += sg.Sub[k] != 0 ? 1 : 0;
                SubPhaseTransition += sg.Interval[k] != SignalInterval.Green ? 1 : 0;
                GapRunning += sg.GapLeft[k] != 0 && sg.GapLeft[k] != sg.GapTicks[k] ? 1 : 0;
                for (int a = 0; a < sg.ArmCountOf[k]; a++)
                {
                    LeftYield += sg.ArmLeftYield[(k * SignalStore.ArmCap) + a] != 0 ? 1 : 0;
                }
            }

            VehicleStore v = w.Vehicles;
            bool ghost = false, change = false, atStop = false, granted = false, stuck = false, wait = false, bus = false, queue = false, head = false, parked = false, meso = false;
            for (int i = 0; i < v.HighWater; i++)
            {
                if (!v.Pool.IsAlive(i))
                {
                    continue;
                }

                ghost |= v.GhostLane[i] >= 0;
                change |= v.LaneChangeDir[i] != 0;
                atStop |= (v.Flags[i] & (byte)VehicleFlags.AtStop) != 0;
                bus |= (v.Flags[i] & (byte)VehicleFlags.IsBus) != 0;
                stuck |= (v.Flags[i] & (byte)VehicleFlags.Stuck) != 0;
                granted |= GateRules.IsGranted(v, i);
                wait |= v.WaitTicks[i] != 0;
                if (v.Lod.Length != 0)
                {
                    meso |= (v.Lod[i] & Lod.LodBits.Meso) != 0;
                    queue |= (v.Lod[i] & Lod.LodBits.Queue) != 0;
                    head |= (v.Lod[i] & Lod.LodBits.Head) != 0;
                    parked |= (v.Lod[i] & Lod.LodBits.Parked) != 0;
                }
            }

            Ghost += ghost ? 1 : 0;
            LaneChange += change ? 1 : 0;
            AtStop += atStop ? 1 : 0;
            Buses += bus ? 1 : 0;
            Stuck += stuck ? 1 : 0;
            Granted += granted ? 1 : 0;
            WaitTicks += wait ? 1 : 0;
            Meso += meso ? 1 : 0;
            Queue += queue ? 1 : 0;
            Head += head ? 1 : 0;
            Parked += parked ? 1 : 0;
        }

        public override string ToString() =>
            $"wave {Wave}, recalcPending {RecalcPending}, ghost {Ghost}, laneChange {LaneChange}, rerouting {Rerouting}, refill {Refill}, trips {Trips}, commands {Commands}, " +
            $"adaptiveSlots {AdaptiveSlots}, arrowSub {ArrowSub}, transitions {SubPhaseTransition}, gapRunning {GapRunning}, leftYield {LeftYield}, atStop {AtStop}, buses {Buses}, selected {Selected}, granted {Granted}, waiting {WaitTicks}, stuck {Stuck}, meso {Meso}, queue {Queue}, head {Head}, parked {Parked}";
    }

    /// <summary>Сценарий команд: детерминированная функция номера тика и состояния мира (одинакова у эталона, сохраняемого прогона и загруженного).</summary>
    internal static void Script(Simulation sim)
    {
        long t = sim.Tick;
        World w = sim.World;
        int zones = w.Net.ZoneCount;
        if (t % 53 == 10)
        {
            for (int i = 0; i < w.Vehicles.HighWater; i++)
            {
                if (w.Vehicles.Pool.IsAlive(i))
                {
                    sim.Enqueue(Command.MakeSelectVehicle(w.Vehicles.HandleOf(i)));
                    break;
                }
            }
        }

        if (t % 71 == 5)
        {
            sim.Enqueue(Command.MakeClearSelection());
        }

        if (t % 40 == 0 && zones > 3)
        {
            sim.Enqueue(Command.MakeSpawnTrip(new SpawnTripCmd { FromZone = (int)(t / 40 % zones), ToZone = (int)((t / 40 + 3) % zones), FromLane = -1, VehicleType = 0, Count = 2 }));
        }

        if (t % 90 == 45)
        {
            sim.Enqueue(Command.MakeSetTargetVehicleCount(100 + (int)(t % 3 * 10)));
        }

        if (t % 130 == 100)
        {
            int node = (int)((t / 130 * 7) + 3) % w.Net.NodeCount;
            NodeControl[] modes = { NodeControl.Stop, NodeControl.Yield, NodeControl.Signal, NodeControl.RightHandRule };
            sim.Enqueue(Command.MakeSetNodeControl(node, modes[t / 130 % modes.Length]));
        }

        if (t == 200)
        {
            for (int n = 0; n < w.Net.NodeCount; n++)
            {
                if (w.Net.Nodes[n].Signal >= 0)
                {
                    SetSignalPlanCmd p = SignalStore.DefaultPlan(w.Signals.PhaseCount[w.Net.Nodes[n].Signal], in w.Rules);
                    p.Node = n;
                    p.OffsetTicks = 77;
                    sim.Enqueue(Command.MakeSetSignalPlan(in p));
                    break;
                }
            }
        }

        if (t == 330 || t == 620 || t == 900)
        {
            float o = t / 10f;
            var r = new BuildRoadCmd { Ax = -400f - o, Az = -400f, Bx = -150f - o, Bz = -400f, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
            sim.Enqueue(Command.MakeBuildRoad(in r));
        }
    }

    private static void StepWithScript(Simulation sim, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Script(sim);
            sim.Step(1);
        }
    }

    private static ulong[] ReferenceHashes(in ScenarioDesc d, int ticks)
    {
        using Simulation a = Simulation.Create(in d);
        var h = new ulong[ticks + 1];
        h[0] = a.ComputeStateHash();
        for (int i = 1; i <= ticks; i++)
        {
            Script(a);
            a.Step(1);
            h[i] = a.ComputeStateHash();
        }

        return h;
    }

    private static void SaveEveryTick(in ScenarioDesc d, int from, int to, int stride, int follow, Coverage cov, int saveThreads = 1, int loadThreads = 1)
    {
        ulong[] reference = ReferenceHashes(in d, to + follow);
        using Simulation a = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = saveThreads });
        for (int t = 0; t <= to; t++)
        {
            Script(a);   // команды границы t уже в очереди: сейв их содержит; эталон делает то же перед своим тиком t + 1
            if (t >= from && (t - from) % stride == 0)
            {
                Assert.Equal(reference[t], a.ComputeStateHash());
                cov.Observe(a);
                byte[] bytes = WorldSaveFixtures.SaveBytes(a);
                using Simulation b = WorldSaveFixtures.Load(bytes, loadThreads);
                byte[] again = WorldSaveFixtures.SaveBytes(b);
                if (!bytes.AsSpan().SequenceEqual(again))
                {
                    int k = 0;
                    while (bytes[k] == again[k])
                    {
                        k++;
                    }

                    Assert.Fail($"tick {t}: Save(Load(Save)) differs at byte {k} of {bytes.Length}: {WorldSaveFixtures.Locate(bytes, k)}");
                }

                // Продолжение: границу t не повторяем (её команды в очереди), дальше сценарий как у эталона.
                b.Step(1);
                for (int i = 1; i < follow; i++)
                {
                    Script(b);
                    b.Step(1);
                }

                Assert.True(
                    b.ComputeStateHash() == reference[t + follow],
                    $"tick {t}: hash after {follow} ticks of continuation {WorldSaveFixtures.Hex(b.ComputeStateHash())} != reference {WorldSaveFixtures.Hex(reference[t + follow])}");
            }

            a.Step(1);
        }
    }

    [Fact]
    public void Small_SaveAtEveryTick_0To1200_ContinuationEqualsReference()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        var cov = new Coverage();
        SaveEveryTick(in d, 0, 1200, 1, 20, cov);
        _out.WriteLine(cov.ToString());
        Assert.True(cov.Wave > 0 && cov.Ghost > 0 && cov.Commands > 0 && cov.Trips > 0 && cov.Granted > 0 && cov.Selected > 0, cov.ToString());
    }

    [Fact]
    public void Small_AdaptiveSignals_SaveEveryTickAroundArrowAndGapOut()
    {
        // Генератор ставит узлам Adaptive со стрелкой. Пробный прогон находит первое включение стрелки; сейв - на каждом тике вокруг него:
        // подфаза Arrow, переходы жёлтый/all-red между масками, счёт gap и уступания левого (ArmLeftYield/ArmLeftHead) - всё, что читает следующий тик раньше записи.
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.City.SignalMode = CitySignalMode.Adaptive;
        int firstArrow = -1;
        using (Simulation probe = Simulation.Create(in d))
        {
            for (int t = 0; t < 4000 && firstArrow < 0; t++)
            {
                Script(probe);
                probe.Step(1);
                if (probe.Counters.ArrowPhases > 0)
                {
                    firstArrow = t;
                }
            }
        }

        Assert.True(firstArrow >= 0, "стрелка не включилась за 4000 тиков малого сценария");
        var cov = new Coverage();
        int from = Math.Max(0, firstArrow - 120);
        SaveEveryTick(in d, from, firstArrow + 80, 1, 25, cov);
        _out.WriteLine($"first arrow at tick {firstArrow}; " + cov);
        Assert.True(cov.AdaptiveSlots > 0 && cov.ArrowSub > 0 && cov.SubPhaseTransition > 0 && cov.GapRunning > 0 && cov.LeftYield > 0, cov.ToString());
    }

    [Fact]
    public void Small_PendingRecalcAndActiveWaves_SaveEveryThirdTick()
    {
        // Пересчёт весов каждые 40 тиков и волна по одной строке за тик: сейв попадает в RecalcPending и в середину волны.
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Routing.CostRecalcTicks = 40;
        d.Routing.CustomizeBudgetPerTick = 1;
        var cov = new Coverage();
        SaveEveryTick(in d, 0, 900, 3, 25, cov);
        _out.WriteLine(cov.ToString());
        Assert.True(cov.Wave > 0 && cov.RecalcPending > 0, cov.ToString());
    }

    private static ScenarioDesc WithLod(in LodDesc lod)
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Lod = lod;
        return d;
    }

    [Fact]
    public void Small_AllMesoKinematic_SaveEveryFourthTick()
    {
        ScenarioDesc d = WithLod(new LodDesc { Mode = LodMode.AllMeso, CellM = 300f, FollowSelected = true, FocusCommands = true });
        var cov = new Coverage();
        SaveEveryTick(in d, 0, 800, 4, 30, cov);
        _out.WriteLine(cov.ToString());
        Assert.True(cov.Meso > 0, cov.ToString());
    }

    [Fact]
    public void Small_AllMesoQueue_SaveEveryFourthTick()
    {
        ScenarioDesc d = WithLod(new LodDesc { Mode = LodMode.AllMeso, CellM = 300f, FollowSelected = true, FocusCommands = true, MesoModel = MesoModel.Queue });
        var cov = new Coverage();
        SaveEveryTick(in d, 0, 800, 4, 30, cov);
        _out.WriteLine(cov.ToString());
        Assert.True(cov.Queue > 0 && cov.Head > 0, cov.ToString());
    }

    [Fact]
    public void Small_LodAuto_SaveEveryFifthTick()
    {
        ScenarioDesc d = WithLod(new LodDesc
        {
            Mode = LodMode.Auto,
            CellM = 300f,
            MicroBudget = 40,
            EvalTicks = 50,
            HoldTicks = 100,
            FocusMarginCells = 1,
            FollowSelected = true,
            FocusCommands = true,
            Focus = new[] { new LodPlaceDesc { X = 300f, Z = 300f, RadiusM = 250f } },
            Pinned = new[] { new LodPlaceDesc { X = 900f, Z = 900f, RadiusM = 100f } },
        });
        var cov = new Coverage();
        SaveEveryTick(in d, 0, 800, 5, 30, cov);
        _out.WriteLine(cov.ToString());
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void Small_SaveWithOneThreadCount_LoadWithAnother(int saveThreads, int loadThreads)
    {
        // Число потоков в сейв не входит (ADR-0017): эталон считается при одном потоке, сейв и продолжение - при других.
        ScenarioDesc d = WorldSaveFixtures.Small();
        var cov = new Coverage();
        SaveEveryTick(in d, 0, 600, 30, 40, cov, saveThreads, loadThreads);
    }

    [Fact]
    public void Small_LoadWithCapacityOverride_ContinuesAndSavesCanonically()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        using Simulation a = Simulation.Create(in d);
        a.Step(500);
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        var o = new LoadOptions { StrictHash = 1, CapacityOverride = new SimCapacities { MaxVehicles = 400, MaxRoutes = 1024 } };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(bytes, in o, default, out Simulation? b, out LoadReport rep));
        using (b)
        {
            Assert.Equal(a.ComputeStateHash(), b!.ComputeStateHash());
            Assert.Equal(400, b.World.Vehicles.Capacity);
            b.Step(200);   // продолжение на других ёмкостях не обязано совпасть с исходным, но обязано работать
            Assert.True(b.ActiveVehicleCount > 0);
        }

        // Меньше занятого - отказ кодом.
        o.CapacityOverride = new SimCapacities { MaxVehicles = 10 };
        Assert.NotEqual(LoadStatus.Ok, Simulation.Load(bytes, in o, default, out Simulation? c, out rep));
        Assert.Null(c);
    }
}
