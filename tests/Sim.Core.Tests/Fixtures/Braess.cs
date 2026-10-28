using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Fixtures;

/// <summary>Итог прогона <see cref="Braess"/>: доля поездок через перемычку и среднее время S→T.</summary>
internal sealed class BraessResult
{
    public int Trips;                // завершённые поездки в окне замера
    public int BridgeTrips;          // из них проехавшие перемычку A→B
    public double MeanTravelS;
    public long TimedReroutes;
    public long CostRecalcs;
    public int Stuck;

    public double BridgeShare => Trips == 0 ? 0 : (double)BridgeTrips / Trips;

    public override string ToString() =>
        $"trips {Trips}, bridge {BridgeTrips} ({BridgeShare:P1}), mean {MeanTravelS:F1} s, reroutes {TimedReroutes}, recalcs {CostRecalcs}, stuck {Stuck}";
}

/// <summary>
/// Фикстура «парадокс Брайеса» (zoning-demand.md 10.3): явная сеть (city.enabled=false) и OD-генератор с фиксированным числом
/// машин в обороте. S→A узкое (1 полоса, 300 м, Yield на выходе), A→T длинное широкое (2 полосы, 2.5 км), S→B широкое, B→T узкое;
/// вариант «+» добавляет перемычку A→B (1 полоса, 200 м). Звенья односторонние. Прямых отрезков со стороной 2.5 км в плоскости с
/// остальными длинами не уложить, поэтому длинные звенья - цепочка из двух сегментов по 1250 м через промежуточный узел.
/// Зоны S и T - явные: полосы появления подставляются в сеть после <c>Create</c> (явные зоны вне city их не получают).
/// </summary>
internal static class Braess
{
    public const float WideHalfLength = 1250f;

    public static ScenarioDesc Desc(bool bridge, ulong seed, int vehicles, int ticks)
    {
        const float h = 259.8076f;                  // |SA| = 300
        var nodes = new List<NodeDesc>
        {
            new() { X = 0, Z = 0, Control = NodeControl.None },                    // 0 S
            new() { X = -150, Z = h, Control = NodeControl.Yield },                // 1 A
            new() { X = 50, Z = h, Control = NodeControl.Yield },                  // 2 B (|AB| = 200)
            new() { X = 200, Z = h + 259.8076f, Control = NodeControl.None },      // 3 T (|BT| = 300)
        };
        float ax = nodes[1].X, az = nodes[1].Z, bx = nodes[2].X, bz = nodes[2].Z, tx = nodes[3].X, tz = nodes[3].Z;
        nodes.Add(Mid(0, 0, bx, bz, -1));          // 4: середина S→B (2 x 1250 м)
        nodes.Add(Mid(ax, az, tx, tz, +1));        // 5: середина A→T
        nodes.Add(new NodeDesc { X = -300, Z = -50, Control = NodeControl.None });         // 6: внешний конец подъезда S
        nodes.Add(new NodeDesc { X = tx + 300, Z = tz + 50, Control = NodeControl.None }); // 7: внешний конец подъезда T

        static SegmentDesc Seg(int a, int b, int lanes, RoadKind k, float v) =>
            new() { A = a, B = b, LanesAB = lanes, LanesBA = 0, SpeedLimit = v, Kind = k };
        var segs = new List<SegmentDesc>
        {
            new() { A = 6, B = 0, LanesAB = 2, LanesBA = 1, SpeedLimit = 13.9f, Kind = RoadKind.Street },   // 0: подъезд S (полоса появления - AB)
            new() { A = 7, B = 3, LanesAB = 1, LanesBA = 1, SpeedLimit = 13.9f, Kind = RoadKind.Street },   // 1: подъезд T (прибытие - BA)
            Seg(0, 1, 1, RoadKind.Street, 13.9f),         // 2: S→A узкое
            Seg(1, 5, 2, RoadKind.Arterial, 16.7f),       // 3: A→M
            Seg(5, 3, 2, RoadKind.Arterial, 16.7f),       // 4: M→T
            Seg(0, 4, 2, RoadKind.Arterial, 16.7f),       // 5: S→M
            Seg(4, 2, 2, RoadKind.Arterial, 16.7f),       // 6: M→B
            Seg(2, 3, 1, RoadKind.Street, 13.9f),         // 7: B→T узкое
        };
        if (bridge)
        {
            segs.Add(Seg(1, 2, 1, RoadKind.Street, 13.9f)); // 8: перемычка A→B
        }

        SimCapacities c = TestScenarios.Caps();
        c.MaxVehicles = Math.Max(64, vehicles * 2);
        c.MaxNodes = 32;
        c.MaxSegments = 32;
        c.MaxLanes = 512;
        c.MaxLanePoints = 8192;
        c.MaxZones = 4;
        c.MaxRoutes = vehicles * 4;
        c.MaxPendingTrips = vehicles * 2;
        c.EventCapacity = 4096;
        c.CommandQueueCapacity = 16;
        c.MaxSignalizedNodes = 2;
        return new ScenarioDesc
        {
            Seed = seed,
            DurationTicks = ticks,
            Capacities = c,
            Routing = new RoutingDesc { Levels = 0, PathfindBudgetPerTick = 32 },
            Network = new NetworkDesc { Nodes = nodes.ToArray(), Segments = segs.ToArray() },
            Demand = new DemandDesc
            {
                TargetVehicles = vehicles,
                SpawnRampTicks = 1200,
                Model = DemandModel.Gravity,
                Zones = new[]
                {
                    new ZoneDesc { CenterX = 0, CenterZ = 0, HalfSize = 50, Weight = 1 },
                    new ZoneDesc { CenterX = tx, CenterZ = tz, HalfSize = 50, Weight = 1 },
                },
                OdPairs = new[] { new OdPairDesc { FromZone = 0, ToZone = 1, Weight = 1 } },
            },
            Rules = new RulesDesc
            {
                MinPhysicalGap = 0.5f,
                StuckThresholdTicks = 1200,
                StuckSpeedThreshold = 0.1f,
                GridlockStuckShare = 0.05f,
                GridlockMinStuck = 10,
                GridlockHoldTicks = 1200,
                MobilEveryTicks = 10,
                DefaultYellowTicks = 60,
                DefaultAllRedTicks = 20,
            },
            VehicleTypes = new[] { Step2.RealCar() },
        };
    }

    // Точка, дающая ломаную 2 x 1250 м между (x0, z0) и (x1, z1): сдвиг от середины по нормали.
    private static NodeDesc Mid(float x0, float z0, float x1, float z1, int side)
    {
        double dx = x1 - x0, dz = z1 - z0, d = Math.Sqrt((dx * dx) + (dz * dz));
        double off = Math.Sqrt((WideHalfLength * WideHalfLength) - (d * d / 4));
        return new NodeDesc
        {
            X = (float)(((x0 + x1) / 2) + (side * off * -dz / d)),
            Z = (float)(((z0 + z1) / 2) + (side * off * dx / d)),
            Control = NodeControl.None,
        };
    }

    /// <summary>Прогон: поездки, начатые до <paramref name="warmupTicks"/>, не учитываются; учёт завершённых до <paramref name="ticks"/>.</summary>
    public static BraessResult Run(bool bridge, ulong seed, int vehicles, int ticks, int warmupTicks)
    {
        ScenarioDesc d = Desc(bridge, seed, vehicles, ticks);
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        NetworkStore net = w.Net;
        net.Zones[0].FirstSpawnLane = 0;
        net.Zones[0].SpawnLaneCount = 2;
        net.Zones[1].FirstSpawnLane = 2;
        net.Zones[1].SpawnLaneCount = 1;
        net.ZoneLanes[0] = net.Segments[0].FirstLaneAB;
        net.ZoneLanes[1] = net.Segments[0].FirstLaneAB + 1;
        net.ZoneLanes[2] = net.Segments[1].FirstLaneAB;
        net.ZoneLaneCount = 3;

        int bridgeSeg = bridge ? 8 : -1;
        var v = w.Vehicles;
        int cap = v.Capacity;
        var spawn = new long[cap];
        var viaBridge = new bool[cap];
        var lastSeen = new long[cap];
        var gen = new int[cap];
        Array.Fill(spawn, -1L);
        var r = new BraessResult();
        double sum = 0;
        for (int t = 0; t < ticks; t++)
        {
            sim.Step(1);
            long now = w.Tick;
            int hw = v.HighWater;
            for (int i = 0; i < hw; i++)
            {
                bool alive = v.Pool.IsAlive(i);
                if (spawn[i] >= 0 && (!alive || v.Pool.GenerationOf(i) != gen[i]))
                {
                    if (spawn[i] >= warmupTicks)
                    {
                        r.Trips++;
                        sum += (lastSeen[i] - spawn[i]) * 0.05;
                        if (viaBridge[i])
                        {
                            r.BridgeTrips++;
                        }
                    }

                    spawn[i] = -1;
                }

                if (!alive)
                {
                    continue;
                }

                if (spawn[i] < 0)
                {
                    spawn[i] = v.SpawnTick[i];
                    gen[i] = v.Pool.GenerationOf(i);
                    viaBridge[i] = false;
                }

                lastSeen[i] = now;
                int lane = v.Lane[i];
                if (bridgeSeg >= 0 && lane >= 0 && net.Lanes[lane].Segment == bridgeSeg)
                {
                    viaBridge[i] = true;
                }
            }
        }

        r.MeanTravelS = r.Trips > 0 ? sum / r.Trips : 0;
        r.TimedReroutes = sim.Counters.TimedReroutes;
        r.CostRecalcs = sim.Counters.CostRecalcs;
        r.Stuck = sim.Counters.StuckVehicles;
        return r;
    }
}
