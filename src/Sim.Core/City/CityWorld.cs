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

    public readonly CityDesc Cfg;              // с нормализованной таблицей типов зданий
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

    // ---- производные Z2 (в сейв не входят): колесо событий жителей, кольцо прибытий, scratch тика ----
    public readonly TimerWheel Wheel;          // ключ = индекс жителя; ровно один ключ на жителя с EvTick >= 0
    public readonly ArrivalRing Arrivals;
    public readonly int[] DueScratch;          // ключи колеса текущего тика

    // ---- состояние суток и исполнения (WRLD) ----
    public int Day;                            // номер суток (= Tick / DayTicks на границе; хранится явно: агрегат прыгает по тикам)
    public TripExecution Exec;                 // текущий исполнитель ног
    public int ExecPending = -1;               // запрошенное переключение (-1 нет), применяется на границе суток (Z5)

    // ---- курсоры дневных проходов (LEDG) ----
    public int CursorResidents;
    public int CursorBuildings;
    public int CursorAccess;

    // ---- сглаженный спрос, как показывается клиенту (LEDG, Z4) ----
    public int DemandR, DemandC, DemandI;

    // ---- накопители суток «сегодня/вчера» (LEDG): туры, ноги по способам и их стоимость ----
    public readonly int[] ToursDay = new int[2];          // [day & 1]
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
        Lots = new LotStore(caps.MaxLots);
        Buildings = new BuildingStore(caps.MaxBuildings);
        Residents = new CityResidents(caps.MaxCityResidents);
        Ledger = new Ledger(Residents, Buildings);
        Skims = new Skims(grid.Cells, cfg.Choice.SkimPeriods, caps.SkimObsCapacity);
        Loads = new CellLoads(grid.Cells, cfg.Choice.SkimPeriods);
        LotGen = new LotBuilder(grid, cfg.Zoning.LotDepthMm);
        Wheel = new TimerWheel(caps.MaxCityResidents);
        Arrivals = new ArrivalRing(caps.MaxCityResidents);
        DueScratch = new int[Math.Max(1, caps.MaxCityResidents)];
    }

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
        RebuildWheel(w.Tick - 1);
        TripLegs.RestoreDerived(w);
    }

    /// <summary>Колесо жителей из <c>EvTick</c> (события не раньше <paramref name="now"/> + 1): в <c>Create</c> now = -1, после <c>Load</c> now = Tick - 1.</summary>
    public void RebuildWheel(long now)
    {
        Wheel.Clear();
        Arrivals.Clear();
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
