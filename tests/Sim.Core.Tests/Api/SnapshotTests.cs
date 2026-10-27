using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Api;

/// <summary>Фаза 10 (ADR-0010): эквивалентность прежней реализации, кэш курса, длины спанов.</summary>
[Collection("WorkerThreads")]
public class SnapshotTests
{
    /// <summary>Прежняя реализация фазы 10 (этап 1) - эталон: полный Evaluate + Atan2 на каждую машину.</summary>
    private static class LegacySnapshot
    {
        public static void Fill(World w, int[] hints, VehicleRenderData[] buf, Vec2[] rearState, int[] rearGen)
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

                float length = v.Length[i];
                float st = v.S[i];
                LaneGeometry.Evaluate(net.Lanes, net.LanePoints, net.LanePointS, lane, st, ref hints[i], out Vec2 front, out Vec2 pdir);
                float lateral = v.Lateral[i];
                if (lateral != 0f)
                {
                    front = new Vec2(front.X + (pdir.Z * lateral), front.Z - (pdir.X * lateral));
                }

                float wb = length * 0.8f;
                int gen = v.Pool.GenerationOf(i) + 1;
                Vec2 rear;
                Vec2 rd = front - rearState[i];
                float rl = MathF.Sqrt((rd.X * rd.X) + (rd.Z * rd.Z));
                if (rearGen[i] == gen && rl > 0.25f * wb && rl < wb + 4f)
                {
                    rear = front - (rd * (wb / rl));
                }
                else
                {
                    rear = new Vec2(front.X - (pdir.X * wb), front.Z - (pdir.Z * wb));
                }

                rearState[i] = rear;
                rearGen[i] = gen;
                Vec2 ax = front - rear;
                float alen = MathF.Sqrt((ax.X * ax.X) + (ax.Z * ax.Z));
                Vec2 dir = alen > 1e-4f ? ax * (1f / alen) : pdir;
                Vec2 pos = front - (dir * (length * 0.5f));
                ref VehicleRenderData r = ref buf[i];
                r.X = pos.X;
                r.Y = 0f;
                r.Z = pos.Z;
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
        var rearState = new Vec2[d.Capacities.MaxVehicles];
        var rearGen = new int[d.Capacities.MaxVehicles];
        var legacy = new VehicleRenderData[d.Capacities.MaxVehicles];
        for (int tick = 0; tick < 600; tick++)
        {
            sim.Step(1);
            LegacySnapshot.Fill(w, hints, legacy, rearState, rearGen);
            ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
            Assert.Equal(w.Vehicles.HighWater, cur.Length);
            for (int i = 0; i < cur.Length; i++)
            {
                VehicleRenderData a = cur[i];
                VehicleRenderData e = legacy[i];
                Assert.True(Math.Abs(a.X - e.X) < 1e-3f && Math.Abs(a.Z - e.Z) < 1e-3f && AngleDiff(a.Heading, e.Heading) <= 1e-3f && a.Speed == e.Speed && a.LateralOffset == e.LateralOffset, $"tick {tick} slot {i}");
                Assert.True(a.Y == e.Y && a.Index == e.Index && a.Generation == e.Generation && a.Type == e.Type && a.Flags == e.Flags, $"tick {tick} slot {i} meta");
            }

            for (int i = cur.Length; i < legacy.Length; i++)
            {
                Assert.True(legacy[i].Flags == 0 && legacy[i].X == 0f, $"tick {tick} tail {i}");
            }
        }

        Assert.True(sim.ActiveVehicleCount > 0);
    }

    [Fact]
    public void Snapshot_IsIndependentOfWorkerThreads_S1k()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation one = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        using Simulation two = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 2 });
        using Simulation three = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 3 });
        for (int tick = 0; tick < 800; tick++)
        {
            one.Step(1);
            two.Step(1);
            three.Step(1);
            if (tick % 20 != 0)
            {
                continue;
            }

            ReadOnlySpan<VehicleRenderData> a = one.Vehicles;
            ReadOnlySpan<VehicleRenderData> b = two.Vehicles;
            ReadOnlySpan<VehicleRenderData> c = three.Vehicles;
            Assert.Equal(a.Length, b.Length);
            Assert.Equal(a.Length, c.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.True(a[i].X == b[i].X && a[i].Z == b[i].Z && a[i].Heading == b[i].Heading, $"tick {tick} slot {i} (1 vs 2 потока)");
                Assert.True(a[i].X == c[i].X && a[i].Z == c[i].Z && a[i].Heading == c[i].Heading, $"tick {tick} slot {i} (1 vs 3 потока)");
            }
        }
    }

    private static float AngleDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) % (2f * MathF.PI);
        return d > MathF.PI ? (2f * MathF.PI) - d : d;
    }

    [Fact]
    public void Snapshot_HeadingSmooth_OnConnectors_S1k()
    {
        ScenarioDesc d = Step3.S1k();
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
    public void Snapshot_RearAxleCutsInside_In90DegreeTurn_AndFollowsOnStraight_S1k()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        NetworkStore net = w.Net;
        int turnSamples = 0;
        int straightSamples = 0;
        var since = new int[d.Capacities.MaxVehicles];   // тиков без смены полосы и бокового смещения
        var seenLane = new int[d.Capacities.MaxVehicles];
        for (int tick = 0; tick < 4000; tick++)
        {
            sim.Step(1);
            ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
            for (int i = 0; i < cur.Length; i++)
            {
                VehicleRenderData a = cur[i];
                int lane = w.Vehicles.Lane[i];
                if (a.Flags == 0 || w.Vehicles.Lateral[i] != 0f || seenLane[i] != lane)
                {
                    since[i] = 0;
                    seenLane[i] = a.Flags == 0 ? -1 : lane;
                    continue;
                }

                since[i]++;
                LaneData cl = net.Lanes[lane];
                float len = w.Vehicles.Length[i];
                float s = w.Vehicles.S[i];
                // Задняя ось из снапшота: центр - половина базы назад вдоль курса.
                float rx = a.X - (MathF.Cos(a.Heading) * len * 0.3f);
                float rz = a.Z - (MathF.Sin(a.Heading) * len * 0.3f);
                if (cl.Kind == LaneKind.Connector)
                {
                    float turn = AngleSigned(net.LanePieceHeading[cl.FirstPoint], net.LanePieceHeading[cl.FirstPoint + cl.PointCount - 2]);
                    float sRear = s - (0.8f * len);
                    float sFront = s;
                    if (MathF.Abs(turn) < 1.3f || MathF.Abs(turn) > 1.9f || sRear < 0f || sFront > cl.Length || sFront < cl.Length * 0.4f || sFront > cl.Length * 0.67f)
                    {
                        continue;
                    }

                    // Знаковое расстояние R до оси коннектора (слева от хода > 0): центр поворота лежит слева при turn > 0.
                    int hint = 0;
                    LaneGeometry.EvaluatePos(net.Lanes, net.LanePoints, net.LanePointS, lane, sFront, ref hint, out Vec2 pf, out int piece);
                    float best = float.MaxValue;
                    float signed = 0f;
                    for (int p = cl.FirstPoint; p < cl.FirstPoint + cl.PointCount - 1; p++)
                    {
                        Vec2 pa = net.LanePoints[p];
                        Vec2 pb = net.LanePoints[p + 1];
                        Vec2 ab = pb - pa;
                        float l2 = (ab.X * ab.X) + (ab.Z * ab.Z);
                        float t = l2 > 1e-9f ? Math.Clamp((((rx - pa.X) * ab.X) + ((rz - pa.Z) * ab.Z)) / l2, 0f, 1f) : 0f;
                        float qx = pa.X + (ab.X * t);
                        float qz = pa.Z + (ab.Z * t);
                        float dist = MathF.Sqrt(((rx - qx) * (rx - qx)) + ((rz - qz) * (rz - qz)));
                        if (dist < best)
                        {
                            best = dist;
                            signed = ((ab.X * (rz - pa.Z)) - (ab.Z * (rx - pa.X))) / MathF.Sqrt(MathF.Max(l2, 1e-9f));
                        }
                    }

                    Assert.True(signed * turn > 0.02f, $"tick {tick} slot {i}: задняя ось не внутри дуги (signed={signed}, turn={turn})");
                    turnSamples++;
                }
                else if (cl.Kind == LaneKind.Normal && cl.PointCount == 2 && s > 40f && a.Speed > 2f && since[i] > 200 && s - (0.8f * len) < cl.Length)
                {
                    int hint = 0;
                    LaneGeometry.EvaluatePos(net.Lanes, net.LanePoints, net.LanePointS, lane, s - (0.8f * len), ref hint, out Vec2 pr, out _);
                    float dx = rx - pr.X;
                    float dz = rz - pr.Z;
                    Assert.True(MathF.Sqrt((dx * dx) + (dz * dz)) < 0.3f, $"tick {tick} slot {i}: на прямой задняя ось не на траектории d={MathF.Sqrt((dx * dx) + (dz * dz))} s={s} lane={lane} len={len} spd={a.Speed} gen={a.Generation} fl={a.Flags} lc={w.Vehicles.LaneChangeDir[i]}");
                    straightSamples++;
                }
            }
        }

        Assert.True(turnSamples > 10, $"мало замеров в повороте: {turnSamples}");
        Assert.True(straightSamples > 50, $"мало замеров на прямой: {straightSamples}");
    }

    private static float AngleSigned(float from, float to)
    {
        float d = to - from;
        while (d > MathF.PI)
        {
            d -= 2f * MathF.PI;
        }

        while (d < -MathF.PI)
        {
            d += 2f * MathF.PI;
        }

        return d;
    }

    [Fact]
    public void Snapshot_NoJump_OnJunctionEnterAndExit_S1k() => AssertNoJump(Step3.S1k(), 4000, 100);

    [Fact]
    public void Snapshot_NoJump_OnJunctionEnterAndExit_Buses() => AssertNoJump(CityWorldTests.Desc("s1k"), 4000, 20);

    private static void AssertNoJump(ScenarioDesc d, int ticks, int minTransitions)
    {
        using Simulation sim = Simulation.Create(in d);
        World w = sim.World;
        const float dt = 1f / 20f;
        var prev = new VehicleRenderData[d.Capacities.MaxVehicles];
        var prevLane = new int[prev.Length];
        int checkedTransitions = 0;
        for (int tick = 0; tick < ticks; tick++)
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
                    float limit = (MathF.Max(a.Speed, prev[i].Speed) * dt) + 0.3f;
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

        Assert.True(checkedTransitions > minTransitions, $"мало переходов: {checkedTransitions}");
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
