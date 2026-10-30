using System;
using Sim.Core.Api;
using Sim.Core.Demand;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Город (ADR-0011, zoning-demand.md §2): контейнер участков, зданий, жителей, денег и скимов. Существует только при <c>demand.model = "residents"</c> (<c>World.City</c>
/// иначе <c>null</c>, ни одна ветка города не исполняется и ничего не выделено). Z1 создаёт и сохраняет состояние; события, расписание, рынки и рост добавляют Z2-Z6.
/// Всё состояние - целые числа и плотные массивы (float только в производной геометрии участков, CZ7).
/// </summary>
internal sealed class CityWorld
{
    public const int SlotsPerDay = 288;

    public CityDesc Cfg;           // с нормализованной таблицей типов зданий
    public readonly BuildingTypes Types;
    public readonly CityGrid Grid;
    public readonly int DayTicks;
    public readonly int SlotTicks;             // DayTicks / 288

    public readonly LotStore Lots;
    public readonly BuildingStore Buildings;
    public readonly CityResidents Residents;
    public readonly Ledger Ledger;
    public readonly Skims Skims;
    public readonly CellLoads Loads;
    public readonly LotBuilder LotGen;
    public readonly Market Market;             // производные рынков труда и жилья (Z4); в сейв не входят

    /// <summary>Поля квартала (building-lifecycle.md §2.6); <c>null</c> без <c>city.lifecycle</c>.</summary>
    public readonly NeighborhoodFields? Fields;

    /// <summary>Службы (services.md §2.3, SR1): записи, станции, кольца вызовов, бригады ЖКХ BL; <c>null</c> без <c>city.lifecycle</c>.</summary>
    public readonly CityServices? Services;

    /// <summary>Казна города (city-economy.md §2.2); <c>null</c> без <c>economy.city_budget</c> (E2 создаёт её же при <c>Enable</c>).</summary>
    public CityBudget? Budget;

    /// <summary>Объекты стоянки (parking.md §2.1, ADR-0026); <c>null</c> без <c>parking.model = "resource"</c> - ни одна ветка парковки не исполняется (PK1).</summary>
    public Sim.Core.Parking.ParkingStore? Parking;

    /// <summary>Внешние подключения (new-game-scratch.md §2, ADR-0037); <c>null</c> без секции <c>outside</c> - ни одна ветка подключений не исполняется.</summary>
    public readonly OutsideState? Outside;

    /// <summary>Цели сценария (scenarios-mode.md §2.3, ADR-0038); <c>null</c> без секции <c>goals</c> - ни одна ветка целей не исполняется.</summary>
    public GoalsState? Goals;

    /// <summary>Показатели города (progression.md §2.3): кольцо 24 игровых часов; есть всегда в режиме жителей (сейв CKPI), в хэш - только при прогрессии.</summary>
    public readonly CityKpi Kpi;

    // ---- производные Z2 (в сейв не входят): колесо событий жителей, кольцо прибытий, scratch тика ----
    public readonly TimerWheel Wheel;          // ключ = индекс жителя; ровно один ключ на жителя с EvTick >= 0
    public readonly ArrivalRing Arrivals;
    public readonly int[] DueScratch;          // ключи колеса текущего тика

    // ---- состояние суток и исполнения (WRLD) ----
    public int Day;                            // номер суток (= Tick / DayTicks на границе; хранится явно: агрегат прыгает по тикам)
    public TripExecution Exec;                 // текущий исполнитель ног
    public int ExecPending = -1;               // запрошенное переключение (-1 нет), применяется на границе суток (Z5)

    // ---- клапан перегрузки (ADR-0035): потолок машин по полосо-км - производное сети (кэш по версии); счётчики - диагностика, вне хэша и сейва (сброс при Load) ----
    public int ValveCapCache;
    public int ValveNetVersion = -1;
    public readonly ValveStats Valve = new();

    // ---- курсоры дневных проходов (LEDG) ----
    public int CursorResidents;
    public int CursorBuildings;
    public int CursorAccess;

    /// <summary>Стадия дневного прохода (<see cref="DayStages"/>, LEDG): 0 здания, 1 жители, 2 рынки и рост, 3 - сегодня всё сделано.</summary>
    public int DayStage = DayStages.Idle;

    // ---- сглаженный спрос, как показывается клиенту (LEDG, Z4) ----
    public int DemandR, DemandC, DemandI;

    // ---- накопители суток «сегодня/вчера» (LEDG): туры, ноги по способам и их стоимость ----
    public readonly int[] ToursDay = new int[2];          // [day & 1]
    public readonly int[] VisitsDayTotal = new int[2];    // [day & 1] визитов в C за сутки (LEDG, Z4)
    public readonly int[] UnservedDayTotal = new int[2];  // [day & 1] визитов впустую за сутки (LEDG, Z4)
    public readonly int[] LegsDay = new int[2 * Skims.Modes];            // [(day & 1) * 3 + mode]
    public readonly long[] LegCostDay = new long[2 * Skims.Modes];       // Σ обобщённой стоимости, дс

    public CityWorld(in CityDesc cfg, in CityGrid grid, in SimCapacities caps, World w)
    {
        Types = new BuildingTypes(cfg.BuildingTypes);
        CityDesc own = cfg;
        own.BuildingTypes = Types.Rows;
        Cfg = own;
        Grid = grid;
        DayTicks = cfg.Calendar.DayTicks;
        SlotTicks = DayTicks / SlotsPerDay;
        Exec = cfg.Residents.TripExecution;
        Kpi = new CityKpi(DayTicks);
        Lots = new LotStore(caps.MaxLots);
        Buildings = new BuildingStore(caps.MaxBuildings);
        Residents = new CityResidents(caps.MaxCityResidents);
        Ledger = new Ledger(Residents, Buildings);
        if (cfg.Economy.CityBudget)
        {
            Budget = new CityBudget(in cfg.Economy, grid.Cells);
            Ledger.Carry = Budget.Carry;
            Budget.RecomputeDerived(this);
        }

        if (cfg.Lifecycle.Enabled)
        {
            Fields = new NeighborhoodFields(in grid, cfg.Lifecycle.CellM);
            Services = new CityServices(in own, Types, caps.MaxBuildings);
            if (Budget != null)
            {
                Budget.Policy.R0 = (ushort)cfg.Lifecycle.UtilityTariff;   // тариф ЖКХ по умолчанию (§2.10)
            }
        }

        Skims = new Skims(grid.Cells, cfg.Choice.SkimPeriods, caps.SkimObsCapacity);
        Loads = new CellLoads(grid.Cells, cfg.Choice.SkimPeriods);
        LotGen = new LotBuilder(grid, cfg.Zoning.LotDepthMm, cfg.Zoning.LotFrontageMm, w);
        Market = new Market(grid.Cells, caps.MaxBuildings, Math.Max(1, cfg.Zoning.MaxNewBuildingsPerDay));
        Wheel = new TimerWheel(caps.MaxCityResidents);
        Arrivals = new ArrivalRing(caps.MaxCityResidents);
        DueScratch = new int[Math.Max(1, caps.MaxCityResidents)];
        if (cfg.Outside.Count > 0)
        {
            Outside = new OutsideState(in cfg.Outside);
        }
    }

    /// <summary>Участок «места»: здание (<c>&gt;= 0</c>) - его участок, код подключения (<see cref="OutsideRef"/>) - виртуальный участок узла.</summary>
    public int LotOf(int place) => place >= 0 ? Buildings.Lot[place] : Lots.Capacity + OutsideRef.Conn(place);

    /// <summary>Период скимов тика (0..P-1).</summary>
    public int PeriodOf(long tick) => Skims.PeriodOf(tick, DayTicks);

    /// <summary>Слот суток тика (0..287).</summary>
    public int SlotOf(long tick) => (int)((tick % DayTicks) / SlotTicks);

    /// <summary>Тик начала слота суток <paramref name="day"/>.</summary>
    public long TickOfSlot(int day, int slot) => ((long)day * DayTicks) + ((long)slot * SlotTicks);

    /// <summary>Производные после загрузки сейва: геометрия участков, сетка занятости, колесо жителей из <c>EvTick/EvKind</c>, очередь заявок ног пешеходов.</summary>
    public void RestoreDerived(World w)
    {
        LotGen.RestoreDerived(w);
        Outside?.Invalidate();
        Outside?.Ensure(w, this);
        Market.Invalidate();
        Budget?.RecomputeDerived(this);
        BudgetFlows.RecomputeMaintenance(w, this);   // содержание - производное сети и маршрутов (city-economy.md §6.1)
        RebuildWheel(w.Tick - 1);
        TripLegs.RestoreDerived(w);
    }

    /// <summary>Колесо жителей из <c>EvTick</c> (события не раньше <paramref name="now"/> + 1): в <c>Create</c> now = -1, после <c>Load</c> now = Tick - 1.</summary>
    public void RebuildWheel(long now)
    {
        Wheel.Clear();
        Arrivals.Clear();
        Services?.Crews.Arrivals.Clear();
        CityResidents r = Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (r.Pool.IsAlive(i) && r.EvTick[i] >= 0)
            {
                Wheel.Schedule(i, r.EvTick[i], Math.Min(now, r.EvTick[i] - 1));   // честный сейв: EvTick > now; прыжок Tick в тесте не должен ронять Schedule
            }
        }
    }
}
