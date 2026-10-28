using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Demand;
using Sim.Core.Tests.City;

namespace Sim.Core.Tests.Demand;

/// <summary>Выбор Z3 (zoning-demand.md §6, §19 «Скимы и выбор»): диапазоны, монотонность по всем уровням, достижимость (CZ8), детерминизм, 0 B на решение.</summary>
public sealed class ChoiceTests
{
    private delegate void Tweak(ref ScenarioDesc d);

    private static Simulation Make(int threads = 1, Tweak? tweak = null)
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        tweak?.Invoke(ref d);
        return Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
    }

    private static int FirstResident(CityWorld c, Func<int, bool> pred)
    {
        for (int i = 0; i < c.Residents.HighWater; i++)
        {
            if (c.Residents.IsAlive(i) && c.Residents.Home[i] >= 0 && pred(i))
            {
                return i;
            }
        }

        throw new InvalidOperationException("нет подходящего жителя");
    }

    private static double MeanFrequency(World w, int resident, int draws)
    {
        long sum = 0;
        for (int i = 0; i < draws; i++)
        {
            int k = Choice.ChooseFrequency(w, resident);
            Assert.InRange(k, 0, w.City!.Cfg.Choice.FreqMax);
            sum += k;
        }

        return sum / (double)draws;
    }

    private static void ShiftLs(Skims s, int delta)
    {
        for (int i = 0; i < s.LsC.Length; i++)
        {
            if (s.LsC[i] != Skims.NoLs)
            {
                s.LsC[i] += delta;
            }
        }
    }

    [Fact]
    public void Frequency_IsInRange_AndFallsWhenAccessibilityGetsWorse()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        int r = FirstResident(c, i => (c.Residents.Flags[i] & ResidentFlags.HasCar) != 0);
        double baseMean = MeanFrequency(w, r, 6000);
        ShiftLs(c.Skims, -4000);          // цели стали ближе (время упало): туров больше
        double better = MeanFrequency(w, r, 6000);
        ShiftLs(c.Skims, 8000);           // цели стали дальше
        double worse = MeanFrequency(w, r, 6000);
        Assert.True(better > baseMean + 0.05, $"улучшение доступности не добавило туров: {baseMean:F3} -> {better:F3}");
        Assert.True(worse < baseMean - 0.01 || (worse < 0.01 && baseMean < 0.2), $"ухудшение не убавило туры: {baseMean:F3} -> {worse:F3}");
        Assert.True(better > worse);
    }

    [Fact]
    public void Frequency_NonWorkersAreMoreActive_WhenAscNonWorkerIsPositive()
    {
        using Simulation sim = Make(tweak: (ref ScenarioDesc d) => d.Town.Choice.AscNonWorkerDs = 4000);
        World w = sim.World;
        CityWorld c = w.City!;
        bool Car(int i) => (c.Residents.Flags[i] & ResidentFlags.HasCar) != 0;
        int worker = FirstResident(c, i => c.Residents.Work[i] >= 0 && Car(i));
        int idle = FirstResident(c, i => c.Residents.Work[i] < 0 && Car(i));
        int hc = c.Lots.Cell[c.Buildings.Lot[c.Residents.Home[idle]]];
        int wc = c.Lots.Cell[c.Buildings.Lot[c.Residents.Home[worker]]];
        double mi = MeanFrequency(w, idle, 4000);
        double mw = MeanFrequency(w, worker, 4000);
        Assert.True(mi > mw, $"idle {mi:F3} (ячейка {hc}) <= worker {mw:F3} (ячейка {wc}), LS {c.Skims.LsC[hc * 3]} / {c.Skims.LsC[wc * 3]}, ref {c.Skims.LsRef}");
    }

    [Fact]
    public void Destination_IsAnActiveShopOfTheSameRoadComponent()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        int r = FirstResident(c, _ => true);
        int homeComp = c.Lots.RoadComp[c.Buildings.Lot[c.Residents.Home[r]]];
        var seen = new HashSet<int>();
        for (int i = 0; i < 1500; i++)
        {
            int b = Choice.ChooseDestination(w, r);
            if (b < 0)
            {
                continue;
            }

            seen.Add(b);
            Assert.True(c.Buildings.IsAlive(b));
            Assert.Equal((byte)BuildingState.Active, c.Buildings.State[b]);
            Assert.Equal(LotZone.Commercial, c.Types.Rows[c.Buildings.Type[b]].Kind);
            Assert.Equal(homeComp, c.Lots.RoadComp[c.Buildings.Lot[b]]);
        }

        Assert.True(seen.Count > 3, $"выбор целей вырожден: {seen.Count} различных");
    }

    [Fact]
    public void Destination_ExpensiveCellIsChosenLessOften()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        Skims s = c.Skims;
        int r = FirstResident(c, _ => true);
        int homeCell = c.Lots.Cell[c.Buildings.Lot[c.Residents.Home[r]]];

        int[] Counts()
        {
            var cnt = new int[s.Cells];
            for (int i = 0; i < 3000; i++)
            {
                int b = Choice.ChooseDestination(w, r);
                if (b >= 0)
                {
                    cnt[c.Lots.Cell[c.Buildings.Lot[b]]]++;
                }
            }

            return cnt;
        }

        int[] before = Counts();
        int target = -1;
        for (int d = 0; d < s.Cells; d++)
        {
            if (d != homeCell && before[d] > 0 && (target < 0 || before[d] > before[target]))
            {
                target = d;
            }
        }

        Assert.True(target >= 0, "нет соседней ячейки с целями");
        // Ячейка-цель стала намного дороже по всем способам и периодам (как после затора).
        for (int m = 0; m < Skims.Modes; m++)
        {
            for (int p = 0; p < s.Periods; p++)
            {
                s.Skim[s.KeyOf(homeCell, target, (TravelMode)m, p)] = s.Get(homeCell, target, (TravelMode)m, p) + 6000;
            }
        }

        int[] after = Counts();
        Assert.True(after[target] < before[target] * 0.7, $"цель подорожала, но выбор не просел: {before[target]} -> {after[target]}");
    }

    [Fact]
    public void Mode_NeverCarWithoutCar_AndDearerCarLosesShare()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        Skims s = c.Skims;
        int r = FirstResident(c, i => (c.Residents.Flags[i] & ResidentFlags.HasCar) != 0 && c.Residents.Home[i] >= 0);
        int dest = -1;
        for (int k = 0; k < 200 && dest < 0; k++)
        {
            dest = Choice.ChooseDestination(w, r);
        }

        Assert.True(dest >= 0);
        int period = 1;
        int CarShare()
        {
            int n = 0;
            for (int i = 0; i < 2000; i++)
            {
                n += Choice.ChooseMode(w, r, dest, period) == TravelMode.Car ? 1 : 0;
            }

            return n;
        }

        int baseCar = CarShare();
        Assert.True(baseCar > 0, "у владельца машины на этом маршруте нет ни одной машинной поездки");
        int homeCell = c.Lots.Cell[c.Buildings.Lot[c.Residents.Home[r]]];
        int destCell = c.Lots.Cell[c.Buildings.Lot[dest]];
        for (int p = 0; p < s.Periods; p++)
        {
            s.Skim[s.KeyOf(homeCell, destCell, TravelMode.Car, p)] = 20_000;
            s.Skim[s.KeyOf(destCell, homeCell, TravelMode.Car, p)] = 20_000;
        }

        Assert.True(CarShare() < baseCar, "дорогая машина должна выбираться реже");

        c.Residents.Flags[r] = (byte)(c.Residents.Flags[r] & ~ResidentFlags.HasCar);
        for (int i = 0; i < 500; i++)
        {
            Assert.NotEqual(TravelMode.Car, Choice.ChooseMode(w, r, dest, period));
        }
    }

    [Fact]
    public void Choice_OnlyReachable_CZ8()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        int checkedTours = 0;
        for (int r = 0; r < c.Residents.HighWater && checkedTours < 400; r += 7)
        {
            if (!c.Residents.IsAlive(r) || c.Residents.Home[r] < 0)
            {
                continue;
            }

            int b = Choice.ChooseDestination(w, r);
            if (b < 0)
            {
                continue;
            }

            int period = r % c.Skims.Periods;
            Assert.True(Choice.TryChooseMode(w, r, b, period, out TravelMode m), $"житель {r}: цель {b} выбрана, но ни один способ недоступен");
            int homeLot = c.Buildings.Lot[c.Residents.Home[r]];
            int destLot = c.Buildings.Lot[b];
            bool hasCar = (c.Residents.Flags[r] & ResidentFlags.HasCar) != 0;
            Assert.NotEqual(int.MaxValue, Choice.LegCost(c, c.Skims, hasCar, homeLot, destLot, m, period));
            if (m == TravelMode.Car)
            {
                Assert.True(hasCar);
                Assert.Equal(c.Lots.RoadComp[homeLot], c.Lots.RoadComp[destLot]);
            }

            if (m == TravelMode.Walk)
            {
                float dx = c.Lots.X[homeLot] - c.Lots.X[destLot];
                float dz = c.Lots.Z[homeLot] - c.Lots.Z[destLot];
                Assert.True(MathF.Sqrt((dx * dx) + (dz * dz)) <= (c.Cfg.Choice.MaxWalkMm / 1000f) + 0.5f);
            }

            checkedTours++;
        }

        Assert.True(checkedTours > 50, $"проверено мало туров: {checkedTours}");
    }

    [Fact]
    public void Mode_WithNoWayToTheDestination_ReportsUnreachable()
    {
        using Simulation sim = Make(tweak: (ref ScenarioDesc d) => d.Town.Choice.MaxWalkMm = 0);
        World w = sim.World;
        CityWorld c = w.City!;
        // Житель без машины, пешком запрещено (max_walk = 0): автобуса внутри ячейки нет, значит способов нет.
        int r = -1, dest = -1, home = -1;
        for (int i = 0; i < c.Residents.HighWater && dest < 0; i++)
        {
            if (!c.Residents.IsAlive(i) || c.Residents.Home[i] < 0 || (c.Residents.Flags[i] & ResidentFlags.HasCar) != 0)
            {
                continue;
            }

            int h = c.Residents.Home[i];
            int hc = c.Lots.Cell[c.Buildings.Lot[h]];
            for (int b = 0; b < c.Buildings.HighWater && dest < 0; b++)
            {
                if (c.Buildings.IsAlive(b) && c.Types.Rows[c.Buildings.Type[b]].Kind == LotZone.Commercial && c.Lots.Cell[c.Buildings.Lot[b]] == hc)
                {
                    r = i;
                    home = h;
                    dest = b;
                }
            }
        }

        Assert.True(dest >= 0, "нет жителя без машины с C-зданием в своей ячейке");
        float dx = c.Lots.X[c.Buildings.Lot[home]] - c.Lots.X[c.Buildings.Lot[dest]];
        float dz = c.Lots.Z[c.Buildings.Lot[home]] - c.Lots.Z[c.Buildings.Lot[dest]];
        Assert.True(MathF.Sqrt((dx * dx) + (dz * dz)) > 0.01f, "дом и цель на одном месте");
        Assert.False(Choice.TryChooseMode(w, r, dest, 0, out _));
        Assert.Equal(TravelMode.Walk, Choice.ChooseMode(w, r, dest, 0));    // запасной ответ заглушки Z1
    }

    [Fact]
    public void DepartSlot_PeaksAtTheIdealSlot_AndPunishesLateMoreThanEarly()
    {
        using Simulation sim = Make();
        World w = sim.World;
        var hits = new Dictionary<int, int>();
        for (int i = 0; i < 20_000; i++)
        {
            int s = Choice.ChooseDepartSlot(w, 0, 100, 10);
            Assert.InRange(s, 0, CityWorld.SlotsPerDay - 1);
            hits[s] = hits.GetValueOrDefault(s) + 1;
        }

        Assert.Equal(5, hits.Count);
        int step = w.City!.Cfg.Choice.TimeStepSlots;
        int ideal = 90;
        Assert.True(hits[ideal] > hits[ideal - step] && hits[ideal - step] > hits[ideal - (2 * step)], "ранний выезд должен убывать с отклонением");
        Assert.True(hits[ideal - step] > hits[ideal + step], "опоздание дороже раннего прибытия (sdl > sde)");
        Assert.True(hits[ideal + step] >= hits[ideal + (2 * step)]);

        // Желаемое прибытие раньше, чем можно выехать, - как у заглушки Z1: слот 0.
        Assert.Equal(0, Choice.ChooseDepartSlot(w, 0, 3, 10));
    }

    [Fact]
    public void DepartSlot_BySkim_ShiftsAwayFromAPeriodThatIsExpensive()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        Skims s = c.Skims;
        int from = 0;
        int to = s.Cells > 1 ? 1 : 0;
        int desired = 200;           // прибытие в 200, ноги около 20 слотов
        int travel = 20;

        int Mean(Func<int> f)
        {
            long sum = 0;
            for (int i = 0; i < 6000; i++)
            {
                sum += f();
            }

            return (int)(sum / 6000);
        }

        int slotDs = c.SlotTicks * 10 / Simulation.TicksPerSecond;
        for (int p = 0; p < s.Periods; p++)
        {
            s.Skim[s.KeyOf(from, to, TravelMode.Car, p)] = travel * slotDs;
        }

        int flat = Mean(() => Choice.ChooseDepartSlot(w, 0, desired, travel, from, to, TravelMode.Car));
        // Период, куда попадают более ранние слоты, стал в пик в 3 раза дороже: выезд сдвигается к более позднему, где дешевле, но не позже допустимого.
        int pk = (int)((long)(desired - travel - 4) * s.Periods / CityWorld.SlotsPerDay);
        s.Skim[s.KeyOf(from, to, TravelMode.Car, pk)] = travel * slotDs * 3;
        int peak = Mean(() => Choice.ChooseDepartSlot(w, 0, desired, travel, from, to, TravelMode.Car));
        Assert.InRange(flat, desired - travel - 6, desired - travel + 2);
        Assert.NotEqual(flat, peak);
    }

    [Fact]
    public void Decisions_AllocateZeroBytes()
    {
        using Simulation sim = Make();
        World w = sim.World;
        CityWorld c = w.City!;
        int r = FirstResident(c, _ => true);
        // Прогрев: Prepare, JIT.
        for (int i = 0; i < 10; i++)
        {
            RunAll(w, r);
        }

        long bytes = Fixtures.AllocationProbe.Measure(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                RunAll(w, r);
            }
        });
        Assert.Equal(0, bytes);
    }

    private static void RunAll(World w, int r)
    {
        Choice.ChooseFrequency(w, r);
        int b = Choice.ChooseDestination(w, r);
        if (b >= 0)
        {
            Choice.ChooseMode(w, r, b, 2);
        }

        Choice.ChooseDepartSlot(w, r, 120, 8);
        Choice.ChooseDepartSlot(w, r, 120, 8, 0, 0, TravelMode.Walk);
    }

    [Fact]
    public void Decisions_AreDeterministic_AndHashIgnoresWorkerThreads()
    {
        ulong Run(int threads)
        {
            using Simulation sim = Make(threads);
            World w = sim.World;
            CityWorld c = w.City!;
            for (int r = 0; r < c.Residents.HighWater; r += 5)
            {
                if (c.Residents.IsAlive(r))
                {
                    RunAll(w, r);
                }
            }

            sim.Step(40);
            return sim.ComputeStateHash();
        }

        ulong h1 = Run(1);
        Assert.Equal(h1, Run(1));
        Assert.Equal(h1, Run(2));
        Assert.Equal(h1, Run(3));
    }
}
