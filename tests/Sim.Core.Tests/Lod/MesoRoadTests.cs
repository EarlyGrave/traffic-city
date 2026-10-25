using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.LodTests;

/// <summary>L2 шага 2.6 (sim-lod.md §2): meso на малой явной сети - такт решений, смена цвета ячейки, объезд автобуса, I6 на въезде на полосу.</summary>
public class MesoRoadTests
{
    private static LodDesc AllMeso(float cellM = 1000f) => new()
    {
        Mode = LodMode.AllMeso,
        CellM = cellM,
        MicroBudget = 15000,
        FollowSelected = true,
        FocusCommands = true,
    };

    private static Simulation Create(in ScenarioDesc d)
    {
        Assert.Equal(CreateStatus.Ok, Simulation.TryCreate(in d, out Simulation? sim));
        return sim!;
    }

    private static int Place(Simulation sim, int lane, float s, float speed, float length = 4.5f)
    {
        VehicleHandle h = TestScenarios.PlaceCar(sim, lane, s, speed);
        World w = sim.World;
        int i = h.Index;
        w.Vehicles.Length[i] = length;
        w.Vehicles.DesiredSpeed[i] = Idm.DesiredSpeed(w.Types[0], w.Net.Lanes[lane].SpeedLimit);
        return i;
    }

    /// <summary>
    /// Регрессия: ячейки 300 м - узел 1 в ячейке цвета 0, узел 2 в ячейке цвета 1 (разные такты решений). Без пересчёта при въезде на полосу
    /// бюджет пути прошлого решения кончался до такта новой ячейки, и скорость обнулялась (пропускная способность -25% на коридоре).
    /// Свободная одиночная машина не должна терять скорость на границах ячеек; фазу пересечения границы относительно такта сдвигает начальная позиция.
    /// </summary>
    [Theory]
    [InlineData(5.0f)]
    [InlineData(5.7f)]
    [InlineData(6.4f)]
    [InlineData(7.1f)]
    [InlineData(7.8f)]
    [InlineData(8.5f)]
    [InlineData(9.2f)]
    [InlineData(9.9f)]
    public void FreeVehicle_KeepsSpeed_AcrossCellColorBoundaries(float startS)
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = AllMeso(cellM: 300f);
        using Simulation sim = Create(in d);
        World w = sim.World;
        LodCells lod = w.Lod!;
        Assert.NotEqual(lod.LaneColor[0], lod.LaneColor[w.Net.Segments[1].FirstLaneAB]);
        int i = Place(sim, 0, startS, 8f);
        float v0 = w.Vehicles.DesiredSpeed[i];
        float minAfterRamp = float.MaxValue;
        int lastLane = 0;
        int laneSwitches = 0;
        for (int t = 0; t < 700 && w.Vehicles.Pool.IsAlive(i); t++)
        {
            sim.Step(1);
            int lane = w.Vehicles.Lane[i];
            if (lane != lastLane)
            {
                laneSwitches++;
                lastLane = lane;
            }

            // Конец тупика: дальше 560 тика машина въезжает в разворот узла 2 (connector с малой скоростью) - не считаем.
            if (t > 120 && t < 540 && lane >= 0)
            {
                minAfterRamp = MathF.Min(minAfterRamp, w.Vehicles.Speed[i]);
            }
        }

        Assert.True(laneSwitches >= 3, $"lane switches {laneSwitches}");
        Assert.True(minAfterRamp > 0.8f * v0, $"min speed {minAfterRamp} of {v0}");
    }

    /// <summary>Решение meso-машины принимается раз в 4 тика (по цвету ячейки её полосы) либо при Fresh; движение - каждый тик.</summary>
    [Fact]
    public void Decisions_AreMadeOnlyOnColorTick_OrWhenFresh()
    {
        ScenarioDesc d = MesoCityTests.AllMeso();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(2400);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        for (int rep = 0; rep < 8; rep++)
        {
            sim.Step(1);
            int hw = v.HighWater;
            var fresh = new bool[hw];
            for (int i = 0; i < hw; i++)
            {
                fresh[i] = v.Pool.IsAlive(i) && (v.Lod[i] & LodBits.Fresh) != 0;
            }

            // Повторный прогон фазы 5 по состоянию на начало следующего тика (Step уже сделал Tick++): MesoNext >= 0 ровно у тех, чей такт.
            BehaviourPhase.Run(w);
            int thinking = 0;
            int total = 0;
            for (int i = 0; i < hw; i++)
            {
                if (!v.Pool.IsAlive(i) || v.Lane[i] < 0 || (v.Flags[i] & (byte)VehicleFlags.AtStop) != 0)
                {
                    continue;
                }

                total++;
                bool expected = fresh[i] || (w.Tick & 3) == w.Lod!.LaneColor[v.Lane[i]];
                Assert.Equal(expected, v.MesoNext[i] >= 0f);
                if (expected)
                {
                    thinking++;
                }

                v.MesoNext[i] = -1f;
            }

            Assert.True(thinking < total * 0.6, $"thinking {thinking} of {total}");
            Assert.True(thinking > 0);
        }
    }

    /// <summary>Объезд (sim-lod.md §2.2): автобус стоит на остановке в правой полосе, идущая сзади машина перестраивается и проезжает мимо.</summary>
    [Fact]
    public void Car_BypassesBusStandingAtStop()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Network.Segments[0].LanesAB = 2;
        d.Network.Segments[0].LanesBA = 2;
        d.Network.Segments[1].LanesAB = 2;
        d.Network.Segments[1].LanesBA = 2;
        d.Lod = AllMeso();
        using Simulation sim = Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        int lane0 = w.Net.Segments[0].FirstLaneAB;
        int bus = Place(sim, lane0, 120f, 0f, length: 12f);
        v.Flags[bus] |= (byte)VehicleFlags.IsBus | (byte)VehicleFlags.AtStop;
        int car = Place(sim, lane0, 40f, 10f);
        bool changed = false;
        for (int t = 0; t < 900; t++)
        {
            sim.Step(1);
            string? gaps = Step2.CheckGaps(w);
            Assert.True(gaps == null, gaps);
            if (v.Lane[car] != lane0 || v.GhostLane[car] >= 0)
            {
                changed = true;
            }
        }

        Assert.True(changed, "машина не перестроилась");
        Assert.True(sim.Counters.MesoLaneChanges >= 1);
        Assert.Equal(sim.Counters.LaneChanges, sim.Counters.MesoLaneChanges);

        // Машина проехала мимо автобуса (он стоит на месте): либо ушла на следующую полосу, либо впереди него по S на соседней.
        bool passed = !v.Pool.IsAlive(car) || v.Lane[car] != lane0 && (w.Net.Lanes[v.Lane[car]].Segment != w.Net.Lanes[lane0].Segment || v.S[car] > v.S[bus]);
        Assert.True(passed, $"car lane {v.Lane[car]} s {v.S[car]}, bus s {v.S[bus]}");
        Assert.Equal(lane0, v.Lane[bus]);
        Assert.Equal(120f, v.S[bus], 3);
    }

    /// <summary>Вставка перед meso-машиной (спавн/смена полосы) помечает ведомого Fresh; въезд на полосу - саму машину (sim-lod.md §2.5).</summary>
    [Fact]
    public void Insertion_MarksFollowerFresh_AndEnteringVehicleFresh()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = AllMeso();
        using Simulation sim = Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        int follower = Place(sim, 0, 50f, 5f);
        v.Lod[follower] &= unchecked((byte)~LodBits.Fresh);
        Assert.Equal(LodBits.Meso, v.Lod[follower]);
        Assert.True(v.Pool.IsAlive(follower));
        int behind = Place(sim, 0, 20f, 5f); // позади: у ведомого лидер прежний
        Assert.Equal(LodBits.Meso, v.Lod[follower]);
        Assert.Equal(LodBits.Meso | LodBits.Fresh, v.Lod[behind]);
        int ahead = Place(sim, 0, 90f, 5f); // впереди: у стоявшего последним без лидера появляется лидер
        Assert.Equal(LodBits.Meso | LodBits.Fresh, v.Lod[ahead]);
        Assert.Equal(LodBits.Meso | LodBits.Fresh, v.Lod[follower]);
        v.Lod[follower] &= unchecked((byte)~LodBits.Fresh);
        int between = Place(sim, 0, 70f, 5f); // между ними: ведомый получает нового, более близкого лидера
        Assert.Equal(LodBits.Meso | LodBits.Fresh, v.Lod[follower]);
        Assert.Equal(LodBits.Meso | LodBits.Fresh, v.Lod[between]);
        Assert.Equal(0f, v.Accel[between]);
    }

    /// <summary>micro -> meso и обратно при въезде на полосу (sim-lod.md §2.5): Fresh/MesoAllow/Accel.</summary>
    [Fact]
    public void LevelTransition_OnLaneEntry_FollowsCellLevel()
    {
        ScenarioDesc d = TestScenarios.Line();
        d.Lod = AllMeso();
        using Simulation sim = Create(in d);
        World w = sim.World;
        VehicleStore v = w.Vehicles;
        LodCells lod = w.Lod!;

        // Ячейки micro: машина въезжает micro, Accel сохраняется.
        Array.Fill(lod.CellLevel, LodCells.LevelMicro);
        int a = Place(sim, 0, 20f, 5f);
        Assert.Equal(0, v.Lod[a]);
        v.Accel[a] = 0.7f;

        // Ячейки meso: новая машина входит meso/Fresh, а уже стоящая micro-машина при LodTransitions.SetLevel(meso) -> Fresh, MesoAllow = 0.
        Array.Fill(lod.CellLevel, LodCells.LevelMeso);
        int b = Place(sim, w.Net.Segments[1].FirstLaneAB, 20f, 5f);
        Assert.Equal(LodBits.Meso | LodBits.Fresh, v.Lod[b]);
        v.MesoAllow[a] = 3f;
        LodTransitions.SetLevel(v, a, LodCells.LevelMeso);
        Assert.Equal(LodBits.Meso | LodBits.Fresh, v.Lod[a]);
        Assert.Equal(0f, v.MesoAllow[a]);

        // meso -> micro: Accel = 0, MesoAllow = 0, биты сняты.
        v.MesoAllow[a] = 2f;
        v.Accel[a] = 0.5f;
        LodTransitions.SetLevel(v, a, LodCells.LevelMicro);
        Assert.Equal(0, v.Lod[a]);
        Assert.Equal(0f, v.Accel[a]);
        Assert.Equal(0f, v.MesoAllow[a]);
    }
}
