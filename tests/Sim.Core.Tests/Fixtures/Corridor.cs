using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Intersections;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Fixtures;

/// <summary>Метрики прогона коридора (sim-lod.md §7): эталон micro против meso.</summary>
internal sealed class CorridorResult
{
    public double ThroughputPerHour;   // выехало с артерии за последние 10 мин, маш./ч
    public double MeanTravelS;         // среднее время проезда (источник -> сток) выехавших за последние 10 мин
    public double P85TravelS;
    public int MaxQueue;               // макс. число стоящих (v < 1 м/с) на подходе к первому светофору
    public int MaxOnEntry;             // макс. число машин на полосах подхода к первому светофору (диагностика)
    public int Stuck;
    public int Gridlock;
    public long StuckEvents;
    public int Spawned;
    public int Exits;
    public int AliveAtEnd;
    public string? Violation;

    public override string ToString() =>
        $"thr {ThroughputPerHour:F0}/h, mean {MeanTravelS:F1} s, p85 {P85TravelS:F1} s, maxQ {MaxQueue}/{MaxOnEntry}, stuck {Stuck}/{StuckEvents}, gridlock {Gridlock}, spawned {Spawned}, exits {Exits}, alive {AliveAtEnd}";
}

/// <summary>
/// Фикстура коридора (sim-lod.md §7): явная сеть - артерия 2+2 из пяти сегментов по 600 м (3 км), в четырёх внутренних узлах светофоры
/// (одинаковый план по умолчанию), у каждого боковые рукава 100 м с поперечным потоком и въездом на артерию (поворот направо с маршрутом);
/// поток артерии задаётся долей пропускной способности. Спрос ставит <see cref="FlowDriver"/>: тот же детерминированный источник в
/// micro и meso. 20 мин игрового времени, метрики по последним 10 минутам.
/// </summary>
internal static class Corridor
{
    public const int MainSegments = 5;
    public const float SegmentLength = 600f;
    public const int TotalTicks = 24000;
    public const int MeasureFromTick = 12000;

    public static ScenarioDesc Desc(in LodDesc lod)
    {
        ScenarioDesc d = TestScenarios.Line();
        SimCapacities c = d.Capacities;
        c.MaxVehicles = 1024;
        c.MaxRoutes = 512;
        c.MaxNodes = 32;
        c.MaxSegments = 32;
        c.MaxLanes = 4096;
        c.MaxLanePoints = 32768;
        c.MaxSignalizedNodes = 4;
        c.EventCapacity = 4096;
        c.CommandQueueCapacity = 32;
        d.Capacities = c;
        var nodes = new List<NodeDesc>();
        var segs = new List<SegmentDesc>();
        for (int k = 0; k <= MainSegments; k++)
        {
            nodes.Add(new NodeDesc { X = k * SegmentLength, Z = 0, Control = NodeControl.None });
        }

        for (int k = 0; k < MainSegments; k++)
        {
            segs.Add(new SegmentDesc { A = k, B = k + 1, LanesAB = 2, LanesBA = 2, SpeedLimit = 13.9f, Kind = RoadKind.Street });
        }

        for (int i = 1; i <= MainSegments - 1; i++)
        {
            int north = nodes.Count;
            nodes.Add(new NodeDesc { X = i * SegmentLength, Z = -100, Control = NodeControl.None });
            int south = nodes.Count;
            nodes.Add(new NodeDesc { X = i * SegmentLength, Z = 100, Control = NodeControl.None });
            segs.Add(new SegmentDesc { A = i, B = north, LanesAB = 1, LanesBA = 1, SpeedLimit = 13.9f, Kind = RoadKind.Street });
            segs.Add(new SegmentDesc { A = i, B = south, LanesAB = 1, LanesBA = 1, SpeedLimit = 13.9f, Kind = RoadKind.Street });
        }

        d.Network = new NetworkDesc { Nodes = nodes.ToArray(), Segments = segs.ToArray() };
        d.Rules.DefaultYellowTicks = 60;
        d.Rules.DefaultAllRedTicks = 20;
        d.VehicleTypes = new[] { Step2.RealCar() };
        d.Lod = lod;
        return d;
    }

    /// <summary>Сегмент бокового рукава светофора <paramref name="i"/> (1..4): <paramref name="south"/> = false - северный.</summary>
    public static int SideSegment(int i, bool south) => MainSegments + (2 * (i - 1)) + (south ? 1 : 0);

    /// <summary>
    /// Прогон коридора. <paramref name="load"/> - доля от <paramref name="capacityPerHour"/> (маш./ч артерии); <paramref name="capacityPerHour"/> &lt;= 0 -
    /// перегрузка (замер пропускной способности). Поперечный поток и въезды постоянны.
    /// </summary>
    public static CorridorResult Run(in LodDesc lod, double load, double capacityPerHour, int ticks = TotalTicks)
    {
        ScenarioDesc d = Desc(in lod);
        using Simulation sim = Simulation.Create(in d);
        for (int i = 1; i <= MainSegments - 1; i++)
        {
            Assert.True(sim.Enqueue(Command.MakeSetNodeControl(i, NodeControl.Signal, (uint)i)));
        }

        sim.Step(1);
        World w = sim.World;
        var drv = new FlowDriver(sim);
        int headway = capacityPerHour <= 0 ? 22 : (int)Math.Round(72000.0 / (load * capacityPerHour / 2.0));
        int[] entry = { w.Net.Segments[0].FirstLaneAB, w.Net.Segments[0].FirstLaneAB + 1 };
        drv.AddSource(entry[0], headway, offset: 0);
        drv.AddSource(entry[1], headway, offset: headway / 2);
        for (int i = 1; i <= MainSegments - 1; i++)
        {
            int north = SideSegment(i, south: false);
            int south = SideSegment(i, south: true);
            // Поперечный поток с севера на юг: прямо. Въезд на артерию с юга (направо): маршрут через connector на сегмент i.
            drv.AddSource(w.Net.Segments[north].FirstLaneBA, 240, toArm: south, offset: 17 * i);
            drv.AddSource(w.Net.Segments[south].FirstLaneBA, 400, toArm: i, offset: 31 * i, routed: true);
            drv.AddSink(w.Net.Segments[north].FirstLaneAB);
            drv.AddSink(w.Net.Segments[south].FirstLaneAB);
        }

        int exit0 = w.Net.Segments[MainSegments - 1].FirstLaneAB;
        drv.AddSink(exit0);
        drv.AddSink(exit0 + 1);

        var travel = new List<double>();
        int mainExits = 0;
        drv.OnExit = (lane, spawnLane, ticksTravel) =>
        {
            if ((lane == exit0 || lane == exit0 + 1) && w.Tick >= MeasureFromTick && (spawnLane == entry[0] || spawnLane == entry[1]))
            {
                mainExits++;
                travel.Add(ticksTravel * 0.05);
            }
        };

        int maxQueue = 0;
        int maxOnEntry = 0;
        VehicleStore v = w.Vehicles;
        drv.OnTick = _ =>
        {
            int q = 0;
            for (int l = 0; l < 2; l++)
            {
                for (int x = w.Occupancy.LaneHead[entry[l]]; x != -1; x = v.Behind[x])
                {
                    if (drv.SpeedOf(x) < 1.0f && v.Lane[x] == entry[l])
                    {
                        q++;
                    }
                }
            }

            maxQueue = Math.Max(maxQueue, q);
            maxOnEntry = Math.Max(maxOnEntry, w.Occupancy.LaneCount[entry[0]] + w.Occupancy.LaneCount[entry[1]]);
        };

        drv.Run(ticks);
        SimCounters cnt = sim.Counters;
        var r = new CorridorResult
        {
            ThroughputPerHour = mainExits * 6.0,
            MaxQueue = maxQueue,
            MaxOnEntry = maxOnEntry,
            Stuck = cnt.StuckVehicles,
            StuckEvents = cnt.StuckEventsTotal,
            Gridlock = cnt.GridlockFlag,
            Spawned = (int)cnt.VehiclesSpawned,
            Exits = drv.Exits,
            AliveAtEnd = drv.Alive,
        };
        if (travel.Count > 0)
        {
            travel.Sort();
            double sum = 0;
            foreach (double t in travel)
            {
                sum += t;
            }

            r.MeanTravelS = sum / travel.Count;
            r.P85TravelS = travel[Math.Min(travel.Count - 1, (int)(travel.Count * 0.85))];
        }

        r.Violation = drv.GapViolation ?? drv.ConflictViolation ?? drv.EntryWithoutGrant ?? drv.GrantOnRed;
        return r;
    }
}
