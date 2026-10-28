using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.City;

/// <summary>
/// Экономика, рынки и рост (Z4, zoning-demand.md §19 «Экономика и рост»): проводки и сохранение денег (CZ2), рынок труда, дедлайны жителя и здания (CZ4), въезд и отъезд (CZ3),
/// выселение и снос, рост зданий, детерминизм по потокам, сейв посреди дневного прохода, ноль байт на тик.
/// Тесты рынка идут на «коротких сутках» (1440 тиков): туры в такие сутки не помещаются, поэтому остаются только дневные проходы, а не движение машин.
/// </summary>
public sealed class EconomyTests
{
    private const int ShortDay = 1440;

    /// <summary>Дневной проход (здания, жители, рост) укладывается в полсуток после границы.</summary>
    private const int PassDone = ShortDay + (ShortDay * 3 / 4);

    private static ScenarioDesc ShortCity(int dayTicks = ShortDay, double fill = 0.1)
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Calendar.DayTicks = dayTicks;
        d.Town.Residents.InitialFillQ16 = CityDesc.Q16(fill);
        return d;
    }

    private static Simulation Make(ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static CityWorld City(Simulation sim) => sim.World.City!;

    /// <summary>Инвариант CZ2 и сохранение людей: Σ счетов = 0, состояния покрывают живых жителей, сейв-проверка (списки, ссылки, события) проходит.</summary>
    private static void AssertInvariants(Simulation sim)
    {
        CityWorld c = City(sim);
        Assert.Equal(0, c.Ledger.SumAll());
        CityResidents r = c.Residents;
        int alive = 0;
        for (int i = 0; i < r.HighWater; i++)
        {
            alive += r.Pool.IsAlive(i) ? 1 : 0;
        }

        Assert.Equal(r.AliveCount, alive);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation again = WorldSaveFixtures.Load(bytes);   // строгая проверка состояния города при загрузке
        Assert.Equal(sim.ComputeStateHash(), again.ComputeStateHash());
    }

    // ------------------------------------------------------------------ деньги

    [Fact]
    public void Ledger_RandomTransfers_KeepSumAtZero()
    {
        using Simulation sim = Make(ShortCity());
        CityWorld c = City(sim);
        Ledger lg = c.Ledger;
        ulong x = 0x9E3779B97F4A7C15UL;
        ulong Next()
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            return x;
        }

        int Account()
        {
            switch (Next() % 4)
            {
                case 0:
                    return lg.ExternalAccount;
                case 1:
                    return lg.CityAccount;
                case 2:
                    {
                        int r = (int)(Next() % (ulong)c.Residents.HighWater);
                        return c.Residents.Pool.IsAlive(r) ? Ledger.AccountOfResident(r) : lg.CityAccount;
                    }

                default:
                    {
                        int b = (int)(Next() % (ulong)c.Buildings.HighWater);
                        return c.Buildings.IsAlive(b) ? lg.AccountOfBuilding(b) : lg.ExternalAccount;
                    }
            }
        }

        for (int i = 0; i < 20_000; i++)
        {
            long amount = (long)(Next() % 2_000_001) - 1_000_000;
            lg.Transfer(Account(), Account(), amount, (FlowKind)(Next() % Ledger.FlowCount));
            if (i % 500 == 0)
            {
                Assert.Equal(0, lg.SumAll());
            }
        }

        Assert.Equal(0, lg.SumAll());
    }

    [Fact]
    public void Flows_TwoDays_FollowTheLedger_AndNobodyIsStuck()
    {
        ScenarioDesc d = CityFixtures.S1kResPeds();
        d.Town.Calendar.DayTicks = 14_400;   // сутки 12 минут: смена и ноги помещаются, два дня - около 30 с в Debug
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        long people = c.Residents.AliveCount;
        for (int t = 0; t < 2 * 14_400; t += 1200)
        {
            sim.Step(1200);
            Assert.Equal(0, c.Ledger.SumAll());
        }

        Span<long> total = stackalloc long[Ledger.FlowCount];
        Assert.True(sim.CopyFlowTotals(total, FlowPeriod.Total));
        Assert.True(total[(int)FlowKind.Wage] > 0, "зарплата не выплачена");
        Assert.True(total[(int)FlowKind.Purchase] > 0, "покупок нет");
        Assert.True(total[(int)FlowKind.Goods] > 0 && total[(int)FlowKind.Import] > 0, "товары не оплачены (дневной проход фирм не прошёл)");
        Assert.True(total[(int)FlowKind.Export] > 0, "нет экспорта");
        Assert.True(sim.GetCityStats(out CityStats s));
        Assert.Equal(0, s.BalanceResidents + s.BalanceFirms + s.BalanceExternal + s.BalanceCity);
        Assert.Equal(0, sim.Counters.ResStuck);
        Assert.Equal(people + sim.Counters.Immigrants - sim.Counters.Emigrants, c.Residents.AliveCount);
        Assert.True(sim.Counters.ResVisits > 100, $"визитов {sim.Counters.ResVisits}");
        AssertInvariants(sim);
    }

    // ------------------------------------------------------------------ рынок труда

    [Fact]
    public void InitialJobs_AreSpreadOverFirms()
    {
        using Simulation sim = Make(CityFixtures.S1kResPeds());
        CityWorld c = City(sim);
        BuildingStore b = c.Buildings;
        int firms = 0;
        int max = 0;
        long workers = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && c.Types.Rows[b.Type[i]].Kind != Sim.Core.Api.LotZone.Residential && b.Occupants[i] > 0)
            {
                firms++;
                max = Math.Max(max, b.Occupants[i]);
                workers += b.Occupants[i];
            }
        }

        // Прежнее правило «первая фирма со свободным местом» набивало первые фирмы до ёмкости (10-60 мест) и вело сотни людей в несколько зданий.
        Assert.True(workers > 500, $"работников {workers}");
        Assert.True(firms * 4 >= workers, $"работники собраны в {firms} фирмах (среднее {workers / (double)firms:F1} на фирму)");
        Assert.True(max <= 20, $"в одной фирме {max} работников");
    }

    [Fact]
    public void Seeker_IsMatched_AndListsStayConsistent()
    {
        using Simulation sim = Make(ShortCity());
        CityWorld c = City(sim);
        CityResidents r = c.Residents;
        int who = -1;
        for (int i = 0; i < r.HighWater && who < 0; i++)
        {
            if (r.Pool.IsAlive(i) && r.Work[i] >= 0)
            {
                who = i;
            }
        }

        int old = r.Work[who];
        int occ = c.Buildings.Occupants[old];
        c.Market.Unemploy(c, who);
        Assert.Equal(occ - 1, c.Buildings.Occupants[old]);
        Market.StartSeeking(c, who);
        Assert.Equal(-1, r.Work[who]);
        sim.Step(10);
        Assert.True(r.Work[who] >= 0, "ищущий не нашёл работу при тысячах вакансий");
        Assert.Equal(0, r.SeekDays[who]);
        Assert.Equal(0, r.Flags[who] & ResidentFlags.InSeekQueue);
        AssertInvariants(sim);
    }

    [Fact]
    public void NoVacancies_SeekersLeaveAfterTheDeadline()
    {
        ScenarioDesc d = ShortCity();
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        CityResidents r = c.Residents;
        int labour = 0;
        for (int i = 0; i < r.HighWater; i++)
        {
            labour += r.Pool.IsAlive(i) && (r.Flags[i] & ResidentFlags.Labour) != 0 ? 1 : 0;
        }

        for (int bi = 0; bi < c.Buildings.HighWater; bi++)
        {
            if (c.Buildings.IsAlive(bi) && c.Types.Rows[c.Buildings.Type[bi]].Kind != LotZone.Residential)
            {
                c.Market.SetJobs(sim.World, c, bi, 0);   // все места закрыты: работники уволены и встают в очередь
            }
        }

        Assert.Equal(labour, r.SeekQueue.Count);
        sim.Step(ShortDay * (d.Town.Residents.SeekMaxDays + 4));
        Assert.Equal(labour, sim.Counters.Emigrants);
        Assert.Equal(0, sim.Counters.ResStuck);
        for (int i = 0; i < r.HighWater; i++)
        {
            Assert.False(r.Pool.IsAlive(i) && (r.Flags[i] & ResidentFlags.Labour) != 0, "рабочий остался в городе");
        }

        AssertInvariants(sim);
    }

    [Fact]
    public void ClosedFirm_ReleasesWorkers_AndTheBuildingIsRemoved()
    {
        using Simulation sim = Make(ShortCity());
        CityWorld c = City(sim);
        BuildingStore b = c.Buildings;
        int firm = -1;
        for (int i = 0; i < b.HighWater && firm < 0; i++)
        {
            if (b.IsAlive(i) && c.Types.Rows[b.Type[i]].Kind == LotZone.Commercial && b.Occupants[i] >= 2)
            {
                firm = i;
            }
        }

        Assert.True(firm >= 0);
        int lot = b.Lot[firm];
        int workers = b.Occupants[firm];
        b.ProfitEwma[firm] = -10_000;
        b.LossDays[firm] = 20;
        b.Account[firm] -= 100_000;   // сумма остаётся нулевой: пара проводкой ниже
        c.Ledger.External += 100_000;
        sim.Step(PassDone);   // сутки 1: проход зданий закрывает фирму
        Assert.Equal(1, sim.Counters.FirmClosures);
        Assert.Equal(workers, sim.Counters.ResFired);
        sim.Step(ShortDay);
        Assert.Equal(-1, c.Lots.Building[lot]);
        Assert.Equal(1, sim.Counters.BuildingsRemoved);
        Assert.Equal(0, sim.Counters.ResStuck);
        AssertInvariants(sim);
    }

    // ------------------------------------------------------------------ жильё

    [Fact]
    public void EvictedResidents_Relocate_AndTheHouseIsRemoved()
    {
        using Simulation sim = Make(ShortCity());
        CityWorld c = City(sim);
        BuildingStore b = c.Buildings;
        int house = -1;
        for (int i = 0; i < b.HighWater && house < 0; i++)
        {
            if (b.IsAlive(i) && c.Types.Rows[b.Type[i]].Kind == LotZone.Residential && b.Occupants[i] >= 3)
            {
                house = i;
            }
        }

        Assert.True(house >= 0);
        int lot = b.Lot[house];
        int n = b.Occupants[house];
        int pop = c.Residents.AliveCount;
        Growth.Abandon(sim.World, c, house);
        Assert.Equal((byte)BuildingState.Abandoned, b.State[house]);
        sim.Step((2 * ShortDay) + 100);
        Assert.False(b.IsAlive(house), "заброшенный дом не снесён");
        Assert.Equal(-1, c.Lots.Building[lot]);
        Assert.Equal(pop, c.Residents.AliveCount - (int)sim.Counters.Immigrants + (int)sim.Counters.Emigrants);
        Assert.True(sim.Counters.ResRelocations >= n, $"переезжало {sim.Counters.ResRelocations} из {n}");
        Assert.Equal(0, sim.Counters.Emigrants);
        Assert.Equal(0, sim.Counters.ResStuck);
        AssertInvariants(sim);
    }

    [Fact]
    public void NoHousingLeft_EvictedResidentsLeaveByTheDeadline()
    {
        ScenarioDesc d = ShortCity();
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        BuildingStore b = c.Buildings;
        int pop = c.Residents.AliveCount;
        int firstSeq = c.Residents.NextSeq;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && c.Types.Rows[b.Type[i]].Kind == LotZone.Residential)
            {
                Growth.Abandon(sim.World, c, i);
            }
        }

        sim.Step(ShortDay * (d.Town.Residents.EvictMaxDays + 4));
        // Опустевшие участки зонированы под жильё: рост строит новые дома, и в них въезжают новые жители; прежних (Seq до начала прогона) не должно остаться.
        for (int i = 0; i < c.Residents.HighWater; i++)
        {
            Assert.False(c.Residents.Pool.IsAlive(i) && c.Residents.Seq[i] < firstSeq, $"остался выселенный житель {i}");
        }

        Assert.Equal(sim.Counters.Immigrants, c.Residents.AliveCount);
        Assert.Equal(pop, sim.Counters.Emigrants);
        Assert.Equal(0, sim.Counters.ResStuck);
        AssertInvariants(sim);
    }

    [Fact]
    public void Immigrants_MoveIntoFreeHousing_WithEndowment()
    {
        using Simulation sim = Make(ShortCity());
        CityWorld c = City(sim);
        int pop = c.Residents.AliveCount;
        Span<long> total = stackalloc long[Ledger.FlowCount];
        Assert.True(sim.CopyFlowTotals(total, FlowPeriod.Total));
        long before = total[(int)FlowKind.Endowment];
        sim.Step((3 * ShortDay) + 100);
        long imm = sim.Counters.Immigrants;
        Assert.True(imm >= 3, $"въехало {imm}");
        Assert.Equal(pop + imm, c.Residents.AliveCount);
        Assert.True(sim.CopyFlowTotals(total, FlowPeriod.Total));
        long extra = total[(int)FlowKind.Endowment] - before - (imm * c.Cfg.Economy.InitialResidentBalance);
        Assert.True(extra >= 0 && extra % c.Cfg.Economy.InitialFirmBalance == 0, $"лишние вклады {extra}");   // остальное - стартовый баланс новых фирм
        AssertInvariants(sim);
    }

    // ------------------------------------------------------------------ рост

    [Fact]
    public void Growth_BuildsOnEmptyZonedLots_ThenTheBuildingsOpen()
    {
        ScenarioDesc d = ShortCity();
        d.Town.Zoning.MaxNewBuildingsPerDay = 5;
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        LotStore l = c.Lots;
        int painted = 0;
        for (int i = 0; i < l.Count && painted < 30; i++)
        {
            if (l.Zone[i] == (byte)LotZone.None && l.Building[i] < 0)
            {
                l.Zone[i] = (byte)LotZone.Commercial;
                painted++;
            }
        }

        Assert.Equal(30, painted);
        c.DemandC = 1_000_000;
        int before = c.Buildings.AliveCount;
        sim.Step(PassDone);   // сутки 1: стадия роста
        int built = c.Buildings.AliveCount - before;
        Assert.InRange(built, 1, 5);
        int constructing = 0;
        for (int i = 0; i < c.Buildings.HighWater; i++)
        {
            constructing += c.Buildings.IsAlive(i) && c.Buildings.State[i] == (byte)BuildingState.Constructing ? 1 : 0;
        }

        Assert.Equal(built, constructing);
        AssertInvariants(sim);
        sim.Step(ShortDay * 8);   // ConstructDays = 3 (6 для плотных), новые заказы продолжаются
        Assert.True(sim.Counters.BuildingsBuilt >= built, $"построено {sim.Counters.BuildingsBuilt}");
        Assert.True(c.Buildings.AliveCount > before + built, "рост не продолжился на следующие сутки");
        Assert.Equal(0, sim.Counters.ResStuck);
        AssertInvariants(sim);
    }

    // ------------------------------------------------------------------ детерминизм, сейв, аллокации

    [Fact]
    public void StateHash_IsIndependentOfWorkerThreads_WithMarketsAndGrowth()
    {
        ulong reference = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            ScenarioDesc d = ShortCity();
            using Simulation sim = Make(d, threads);
            PaintForGrowth(City(sim));
            sim.Step(4 * ShortDay);
            ulong h = sim.ComputeStateHash();
            if (threads == 1)
            {
                reference = h;
                Assert.True(sim.Counters.Immigrants > 0 && sim.Counters.BuildingsBuilt + sim.Counters.BuildingsAbandoned + sim.Counters.ResRelocations > 0);
            }

            Assert.Equal(reference, h);
        }
    }

    [Fact]
    public void SaveLoad_InTheMiddleOfADayPass_ContinuesIdentically()
    {
        ScenarioDesc d = ShortCity();
        using Simulation a = Make(d);
        PaintForGrowth(City(a));
        CityWorld c = City(a);
        int guard = 0;
        while ((c.DayStage != DayStages.Residents || c.CursorResidents < 50 || c.CursorResidents >= c.Residents.HighWater - 50) && guard++ < 6 * ShortDay)
        {
            a.Step(1);
        }

        Assert.Equal(DayStages.Residents, c.DayStage);
        byte[] bytes = WorldSaveFixtures.SaveBytes(a);
        using Simulation b = WorldSaveFixtures.Load(bytes);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.True(bytes.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(b)));
        a.Step(4 * ShortDay);
        b.Step(4 * ShortDay);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.Counters.Immigrants, b.Counters.Immigrants);
    }

    [Fact]
    public void Step_AllocatesZeroBytes_WithDayPassesAndGrowth()
    {
        ScenarioDesc d = ShortCity();
        using Simulation sim = Make(d);
        PaintForGrowth(City(sim));
        sim.Step(2 * ShortDay);   // прогрев: первый проход, строительство, въезд
        long bytes = AllocationProbe.Measure(() =>
        {
            City(sim).DemandC = 1_000_000;
            sim.Step(2 * ShortDay);
        }, out _);
        Assert.Equal(0, bytes);
    }

    private static void PaintForGrowth(CityWorld c)
    {
        LotStore l = c.Lots;
        int painted = 0;
        for (int i = 0; i < l.Count && painted < 60; i++)
        {
            if (l.Zone[i] == (byte)LotZone.None && l.Building[i] < 0)
            {
                l.Zone[i] = (byte)(painted % 3 == 0 ? LotZone.Residential : (painted % 3 == 1 ? LotZone.Commercial : LotZone.Industrial));
                painted++;
            }
        }

        c.DemandC = 100_000;
        c.DemandI = 100_000;
    }

    // ------------------------------------------------------------------ CZ4 на 30 сутках

    [Fact]
    [Trait("Category", "Long")]
    public void ThirtyDays_Micro_NoStuck_MoneyAndPeopleAreConserved()
    {
        if (Environment.GetEnvironmentVariable("TC_LONG") != "1")
        {
            return;   // ~7 минут в Debug: включается TC_LONG=1 (отчётные прогоны; в pre-commit не входит)
        }

        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Calendar.DayTicks = 14_400;   // сутки 12 минут: смена 4 минуты, ноги 1-3 минуты помещаются
        d.Town.Residents.InitialFillQ16 = CityDesc.Q16(0.1);
        d.Town.Choice.AscFreqDs = 8500;
        using Simulation sim = Make(d);
        CityWorld c = City(sim);
        long people = c.Residents.AliveCount;
        for (int day = 0; day < 30; day++)
        {
            sim.Step(14_400);
            Assert.Equal(0, c.Ledger.SumAll());
            Assert.Equal(0, sim.Counters.ResStuck);
            Assert.Equal(people + sim.Counters.Immigrants - sim.Counters.Emigrants, c.Residents.AliveCount);
        }

        Assert.True(sim.Counters.ResVisits > 1000, $"визитов {sim.Counters.ResVisits}");
        AssertInvariants(sim);
    }
}
