using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Intersections;

/// <summary>
/// ADR-0019, A2: ворота по маскам. Маска занятых полос и список связанных машин ведутся в <see cref="LaneOccupancy"/>; активный проход фазы 4
/// побитово равен полному проходу по машинам (в Debug это же сверяет сама фаза на каждом тике - <c>GatePhase.DebugCompareWithFullScan</c>,
/// а здесь проверка не зависит от конфигурации сборки: второй мир принудительно идёт полным проходом).
/// </summary>
public class GateActiveSetTests
{
    private static int Spawn(VehicleStore v, float s)
    {
        Assert.True(v.TrySpawn(out VehicleHandle h));
        v.S[h.Index] = s;
        return h.Index;
    }

    private static void CheckMask(LaneOccupancy occ)
    {
        for (int lane = 0; lane < occ.MaxLanes; lane++)
        {
            bool bit = (occ.OccMask[lane >> 6] & (1UL << (lane & 63))) != 0;
            Assert.Equal(occ.LaneCount[lane] > 0, bit);
        }
    }

    [Fact]
    public void OccMask_FollowsLaneCount_ThroughAllListOperations()
    {
        const int lanes = 130;   // три слова маски, граница слов 63/64 и 127/128
        var v = new VehicleStore(64, false);
        var occ = new LaneOccupancy(v, lanes);
        CheckMask(occ);

        int a = Spawn(v, 10f);
        int b = Spawn(v, 20f);
        int c = Spawn(v, 5f);
        occ.PushTail(0, a);
        occ.PushHead(63, b);
        occ.PushTail(64, c);
        CheckMask(occ);
        Assert.NotEqual(0UL, occ.OccMask[0]);
        Assert.NotEqual(0UL, occ.OccMask[1]);

        // Смена полосы: ghost держит исходную полосу занятой, пока не снят.
        occ.BeginLaneChange(c, 127, -1, -1);
        CheckMask(occ);
        occ.GhostUnlink(c);
        CheckMask(occ);
        Assert.Equal(0UL, occ.OccMask[1] & 1UL);   // полоса 64 опустела
        Assert.NotEqual(0UL, occ.OccMask[1] & (1UL << 63));   // полоса 127 занята

        occ.UnlinkAll(a);
        occ.UnlinkAll(b);
        occ.UnlinkAll(c);
        CheckMask(occ);
        foreach (ulong word in occ.OccMask)
        {
            Assert.Equal(0UL, word);
        }
    }

    [Fact]
    public void Linked_ListsEachVehicleOnce_UntilCleared()
    {
        var v = new VehicleStore(16, false);
        var occ = new LaneOccupancy(v, 8);
        int a = Spawn(v, 10f);
        int b = Spawn(v, 20f);
        occ.PushTail(0, a);
        occ.PushHead(1, b);
        occ.Unlink(a);
        occ.PushTail(2, a);   // повторное связывание того же слота - без дубля
        Assert.Equal(2, occ.LinkedCount);
        var seen = new HashSet<int>();
        for (int k = 0; k < occ.LinkedCount; k++)
        {
            Assert.True(seen.Add(occ.Linked[k]));
        }

        Assert.Contains(a, seen);
        Assert.Contains(b, seen);

        occ.ClearLinked();
        Assert.Equal(0, occ.LinkedCount);
        occ.Unlink(b);
        occ.PushTail(3, b);
        Assert.Equal(1, occ.LinkedCount);
        Assert.Equal(b, occ.Linked[0]);
    }

    private static ScenarioDesc City(LodMode mode)
    {
        ScenarioDesc d = Step2.S1k(true);
        d.Capacities.MaxRoutes = 8192;
        if (mode != LodMode.Off)
        {
            d.Lod = new LodDesc { Mode = mode, CellM = 1000f, MicroBudget = 15000, FollowSelected = true, FocusCommands = true };
        }

        return d;
    }

    // Мир A идёт активным проходом; мир B перед каждым тиком принудительно запускает полный проход (GateScratch.FullScan): хэш и счётчики равны.
    [Theory]
    [InlineData(LodMode.Off)]
    [InlineData(LodMode.AllMeso)]
    public void ActivePass_EqualsFullScan_OnCity(LodMode mode)
    {
        ScenarioDesc d = City(mode);
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        for (int tick = 0; tick < 1500; tick++)
        {
            a.Step(1);
            b.World.Gate.FullScan = true;
            b.Step(1);
            if (tick % 100 == 99)
            {
                Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
            }
        }

        Assert.Equal(a.Counters.Grants, b.Counters.Grants);
        Assert.Equal(a.Counters.BoxWaitTicks, b.Counters.BoxWaitTicks);
        Assert.Equal(a.Counters.ArterialSignalStops, b.Counters.ArterialSignalStops);
        Assert.Equal(a.Counters.ArterialSignalGrants, b.Counters.ArterialSignalGrants);
        Assert.True(a.Counters.Grants > 100, $"grants {a.Counters.Grants}");
    }
}
