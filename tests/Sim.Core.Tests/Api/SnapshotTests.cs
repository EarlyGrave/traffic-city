using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Api;

/// <summary>Фаза 10 (ADR-0010): эквивалентность прежней реализации, кэш курса, длины спанов.</summary>
public class SnapshotTests
{
    /// <summary>Прежняя реализация фазы 10 (этап 1) - эталон: полный Evaluate + Atan2 на каждую машину.</summary>
    private static class LegacySnapshot
    {
        public static void Fill(World w, int[] hints, VehicleRenderData[] buf)
        {
            VehicleStore v = w.Vehicles;
            NetworkStore net = w.Net;
            Array.Clear(buf);
            for (int i = 0; i < v.HighWater; i++)
            {
                int lane = v.Lane[i];
                if (!v.Pool.IsAlive(i) || (uint)lane >= (uint)net.LaneCount)
                {
                    continue;
                }

                float centerS = v.S[i] - (v.Length[i] * 0.5f);
                Vec2 pos;
                Vec2 dir;
                if (centerS < 0f && net.Lanes[lane].Kind == LaneKind.Connector && (uint)net.Lanes[lane].FromLane < (uint)net.LaneCount)
                {
                    int from = net.Lanes[lane].FromLane;
                    LaneGeometry.Evaluate(net.Lanes, net.LanePoints, net.LanePointS, from, net.Lanes[from].Length + centerS, ref hints[i], out pos, out dir);
                }
                else
                {
                    LaneGeometry.Evaluate(net.Lanes, net.LanePoints, net.LanePointS, lane, centerS, ref hints[i], out pos, out dir);
                    if (centerS < 0f)
                    {
                        pos = net.LanePoints[net.Lanes[lane].FirstPoint] + (dir * centerS);
                    }
                }

                float lateral = v.Lateral[i];
                ref VehicleRenderData r = ref buf[i];
                r.X = pos.X + (dir.Z * lateral);
                r.Y = 0f;
                r.Z = pos.Z - (dir.X * lateral);
                r.Heading = MathF.Atan2(dir.Z, dir.X);
                r.Speed = v.Speed[i];
                r.Index = i;
                r.Generation = (ushort)v.Pool.GenerationOf(i);
                r.Type = v.Type[i];
                r.Flags = (byte)SnapshotPhase.RenderFlags(w, i);
                r.LateralOffset = lateral;
            }
        }
    }

    [Fact]
    public void Snapshot_EqualsLegacy_S1k_600Ticks()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        var hints = new int[d.Capacities.MaxVehicles];
        var legacy = new VehicleRenderData[d.Capacities.MaxVehicles];
        for (int tick = 0; tick < 600; tick++)
        {
            sim.Step(1);
            LegacySnapshot.Fill(w, hints, legacy);
            ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
            Assert.Equal(w.Vehicles.HighWater, cur.Length);
            for (int i = 0; i < cur.Length; i++)
            {
                VehicleRenderData a = cur[i];
                VehicleRenderData e = legacy[i];
                Assert.True(a.X == e.X && a.Z == e.Z && AngleDiff(a.Heading, e.Heading) <= 0.2f && a.Speed == e.Speed && a.LateralOffset == e.LateralOffset, $"tick {tick} slot {i}");
                Assert.True(a.Y == e.Y && a.Index == e.Index && a.Generation == e.Generation && a.Type == e.Type && a.Flags == e.Flags, $"tick {tick} slot {i} meta");
            }

            for (int i = cur.Length; i < legacy.Length; i++)
            {
                Assert.True(legacy[i].Flags == 0 && legacy[i].X == 0f, $"tick {tick} tail {i}");
            }
        }

        Assert.True(sim.ActiveVehicleCount > 0);
    }

    private static float AngleDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) % (2f * MathF.PI);
        return d > MathF.PI ? (2f * MathF.PI) - d : d;
    }

    [Fact]
    public void Snapshot_HeadingSmooth_OnConnectors_S1k()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        NetworkStore net = w.Net;
        const float dt = 1f / 20f;
        var prev = new VehicleRenderData[d.Capacities.MaxVehicles];
        var prevLane = new int[prev.Length];
        int checkedSteps = 0;
        for (int tick = 0; tick < 4000; tick++)
        {
            sim.Step(1);
            ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
            for (int i = 0; i < cur.Length; i++)
            {
                VehicleRenderData a = cur[i];
                int lane = w.Vehicles.Lane[i];
                if (a.Flags != 0 && prev[i].Flags != 0 && prev[i].Generation == a.Generation && prevLane[i] == lane
                    && net.Lanes[lane].Kind == LaneKind.Connector)
                {
                    LaneData cl = net.Lanes[lane];
                    float turn = 0f;
                    for (int p = cl.FirstPoint; p < cl.FirstPoint + cl.PointCount - 2; p++)
                    {
                        turn += AngleDiff(net.LanePieceHeading[p], net.LanePieceHeading[p + 1]);
                    }

                    // Изменение курса за тик не больше 2x средней угловой скорости (поворот / длина * путь) + допуск.
                    float limit = (2f * turn / MathF.Max(cl.Length, 1f) * MathF.Max(a.Speed, prev[i].Speed) * dt) + 0.02f;
                    float dh = AngleDiff(a.Heading, prev[i].Heading);
                    Assert.True(dh <= limit, $"tick {tick} slot {i}: курс {dh} рад > {limit} рад на коннекторе {lane}");
                    checkedSteps++;
                }

                prev[i] = a;
                prevLane[i] = lane;
            }

            for (int i = cur.Length; i < prev.Length; i++)
            {
                prev[i] = default;
            }
        }

        Assert.True(checkedSteps > 200, $"мало шагов: {checkedSteps}");
    }

    [Fact]
    public void Snapshot_NoJump_OnJunctionEnterAndExit_S1k()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        const float dt = 1f / 20f;
        var prev = new VehicleRenderData[d.Capacities.MaxVehicles];
        var prevLane = new int[prev.Length];
        int checkedTransitions = 0;
        for (int tick = 0; tick < 4000; tick++)
        {
            sim.Step(1);
            ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
            for (int i = 0; i < cur.Length; i++)
            {
                VehicleRenderData a = cur[i];
                int lane = w.Vehicles.Lane[i];
                if (a.Flags != 0 && prev[i].Flags != 0 && prev[i].Generation == a.Generation && prevLane[i] != lane
                    && ((prev[i].Flags ^ a.Flags) & (byte)VehicleRenderFlags.OnConnector) != 0)
                {
                    float dx = a.X - prev[i].X;
                    float dz = a.Z - prev[i].Z;
                    float dist = MathF.Sqrt((dx * dx) + (dz * dz));
                    // Въезд на коннектор - точная непрерывность; выезд - прямая экстраполяция назад, запас на кривизну коннектора.
                    bool enter = (a.Flags & (byte)VehicleRenderFlags.OnConnector) != 0;
                    float limit = (MathF.Max(a.Speed, prev[i].Speed) * dt) + (enter ? 0.3f : 0.25f * w.Vehicles.Length[i]);
                    Assert.True(dist <= limit, $"tick {tick} slot {i}: сдвиг {dist} м > {limit} м на смене полосы {prevLane[i]}->{lane}");
                    checkedTransitions++;
                }

                prev[i] = a;
                prevLane[i] = lane;
            }

            for (int i = cur.Length; i < prev.Length; i++)
            {
                prev[i] = default;
            }
        }

        Assert.True(checkedTransitions > 20, $"мало переходов: {checkedTransitions}");
    }

    [Fact]
    public void LanePieceHeading_MatchesAtan2_AfterCreateAndBuildRoad()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation sim = Simulation.Create(in d);
        sim.SetToolsConfig(Tools.ToolsFixture.Unbounded);
        CheckHeadings(sim.World.Net);

        var r = new BuildRoadCmd { Ax = 5000, Az = 5000, Bx = 5150, Bz = 5080, SpeedLimit = 13.9f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street };
        sim.Enqueue(Command.MakeBuildRoad(in r, tag: 1));
        int lanesBefore = sim.World.Net.LaneCount;
        sim.Step(1);
        Assert.True(sim.World.Net.LaneCount > lanesBefore);
        CheckHeadings(sim.World.Net);
    }

    private static void CheckHeadings(NetworkStore net)
    {
        Assert.True(net.LaneCount > 0);
        for (int l = 0; l < net.LaneCount; l++)
        {
            LaneData ld = net.Lanes[l];
            for (int k = 0; k < ld.PointCount - 1; k++)
            {
                int p = ld.FirstPoint + k;
                Vec2 dd = net.LanePoints[p + 1] - net.LanePoints[p];
                float len = MathF.Sqrt((dd.X * dd.X) + (dd.Z * dd.Z));
                Vec2 dir = len > 1e-9f ? dd * (1f / len) : new Vec2(1f, 0f);
                Assert.Equal(MathF.Atan2(dir.Z, dir.X), net.LanePieceHeading[p]);
            }
        }
    }

    [Fact]
    public void SpanLengths_AreHighWater_PrevIsPreviousSnapshot()
    {
        ScenarioDesc d = TestScenarios.Line();
        using Simulation sim = Simulation.Create(in d);
        Assert.Equal(0, sim.Vehicles.Length);
        Assert.Equal(0, sim.PreviousVehicles.Length);

        VehicleHandle h = TestScenarios.PlaceCar(sim, 0, 50f, 5f);
        sim.Step(1);
        Assert.Equal(sim.World.Vehicles.HighWater, sim.Vehicles.Length);
        Assert.True(sim.Vehicles.Length > 0 && sim.Vehicles.Length < d.Capacities.MaxVehicles);
        Assert.Equal(0, sim.PreviousVehicles.Length);
        VehicleRenderData first = sim.Vehicles[h.Index];

        VehicleHandle h2 = TestScenarios.PlaceCar(sim, 0, 10f, 5f);
        sim.Step(1);
        Assert.True(sim.PreviousVehicles.Length <= sim.Vehicles.Length);
        Assert.Equal(sim.World.Vehicles.HighWater, sim.Vehicles.Length);
        Assert.Equal(first.X, sim.PreviousVehicles[h.Index].X);
        Assert.True(h2.Index < sim.Vehicles.Length);
    }
}
