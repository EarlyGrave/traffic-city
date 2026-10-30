using Sim.Core.Api;
using Sim.Core.Demand;

namespace Sim.Core.City;

/// <summary>Проверка параметров режима жителей и построение города в <c>Create</c> и при загрузке сейва (zoning-demand.md §1, §16).</summary>
internal static class CityBuild
{
    public const int MaxDayTicks = 1 << 26;
    public const int MaxBigCount = 1 << 28;

    /// <summary>true - включён режим жителей (<c>demand.model = "residents"</c>).</summary>
    public static bool IsEnabled(in ScenarioDesc desc) => desc.Demand.Model == DemandModel.Residents;

    /// <summary>
    /// Пределы секций и ёмкостей. Нулевой <c>Town</c> в режиме жителей недопустим (<see cref="CityDesc.Default"/> даёт полный набор). Без режима жителей не проверяется ничего,
    /// кроме <c>peds.source</c>.
    /// </summary>
    public static CreateStatus Validate(in ScenarioDesc desc)
    {
        if (desc.Demand.Model < DemandModel.Gravity || desc.Demand.Model > DemandModel.Residents)
        {
            return CreateStatus.InvalidDemand;
        }

        bool on = IsEnabled(in desc);
        if (desc.Peds.Source < PedSource.Preform || desc.Peds.Source > PedSource.City || (desc.Peds.Source == PedSource.City && (!on || !desc.Peds.Enabled)))
        {
            return CreateStatus.InvalidPeds;
        }

        if (!on)
        {
            return desc.Town.Economy.CityBudget || desc.Parking.Enabled || desc.Town.Lifecycle.Enabled ? CreateStatus.InvalidCity : CreateStatus.Ok;   // city_budget, parking и lifecycle только при residents (city-economy.md §1, parking.md §1, building-lifecycle.md §5)
        }

        SimCapacities caps = desc.Capacities;
        if (caps.MaxLots < 1 || caps.MaxBuildings < 1 || caps.MaxCityResidents < 1 || caps.SkimObsCapacity < 1 ||
            caps.MaxLots > MaxBigCount || caps.MaxBuildings > MaxBigCount || caps.MaxCityResidents > MaxBigCount || caps.SkimObsCapacity > MaxBigCount)
        {
            return CreateStatus.InvalidCity;
        }

        if (desc.Lod.CellM < 0f || !float.IsFinite(desc.Lod.CellM))
        {
            return CreateStatus.InvalidCity;
        }

        if (desc.Parking.Enabled && (!desc.Parking.IsValid() || caps.MaxParking < 0 || caps.MaxParking > MaxBigCount))
        {
            return CreateStatus.InvalidCity;
        }

        if (desc.Town.Outside.Count > 0 && desc.Parking.Enabled)
        {
            return CreateStatus.InvalidCity;   // внешние подключения пока без парковки как ресурса (ADR-0037, Y3): нога к узлу не резервирует место
        }

        return IsValid(in desc.Town) ? CreateStatus.Ok : CreateStatus.InvalidCity;
    }

    /// <summary>Пределы всех параметров <see cref="CityDesc"/> (без деления на ноль и переполнений в целочисленных формулах).</summary>
    public static bool IsValid(in CityDesc t)
    {
        CityCalendarDesc cal = t.Calendar;
        CityZoningDesc z = t.Zoning;
        CityResidentsDesc r = t.Residents;
        CityEconomyDesc e = t.Economy;
        CityChoiceDesc ch = t.Choice;
        bool ok = cal.DayTicks >= CityWorld.SlotsPerDay && cal.DayTicks <= MaxDayTicks && cal.DayTicks % CityWorld.SlotsPerDay == 0 && cal.StartHour is >= 0 and < 24;
        ok &= r.MinImmigrantsPerDay is >= 0 and <= MaxBigCount && e.InitialCityBalance is >= 0 and <= 1_000_000_000;
        ok &= z.LotFrontageMm is >= 5_000 and <= 1_000_000 && z.LotDepthMm is >= 5_000 and <= 1_000_000 && z.NodeClearMm is >= 0 and <= 1_000_000;
        ok &= z.MaxNewBuildingsPerDay is >= 0 and <= MaxBigCount && z.MaxPaintM2 >= 1;
        ok &= Q16(z.CCenterShareQ16) && Q16(z.IEdgeShareQ16) && Q16(z.UnzonedShareQ16);
        ok &= Q16(r.InitialFillQ16) && Q16(r.CarOwnershipQ16) && Q16(r.LabourShareQ16) && Q16(r.MaxRelocatePerDayQ16) && Q16(r.MaxJobSwitchPerDayQ16) && Q16(r.MaxImmigrantsPerDayQ16);
        ok &= r.TripExecution is TripExecution.Agents or TripExecution.Aggregate;
        ok &= r.SeekMaxDays is >= 1 and <= 3650 && r.EvictMaxDays is >= 1 and <= 3650 && r.WaitMaxTicks is >= 20 and <= MaxDayTicks;
        ok &= r.PlanSlot is >= 0 and < CityWorld.SlotsPerDay && r.ShopSlots is >= 1 and <= CityWorld.SlotsPerDay && r.HomeMinSlots is >= 0 and <= CityWorld.SlotsPerDay;
        ok &= r.MatchPerTick is >= 1 and <= MaxBigCount && r.VacateDays is >= 0 and <= 3650 && r.SwitchRatioQ8 is >= 0 and <= 1 << 20 && r.RBias is >= -MaxBigCount and <= MaxBigCount;
        ok &= e.WagePerShift >= 0 && e.BasketPrice >= 0 && e.GoodsPct is >= 0 and <= 100 && e.ImportPct is >= 0 and <= 100 && e.ExportPerShift >= 0;
        ok &= e.InitialResidentBalance >= 0 && e.InitialFirmBalance >= 0 && e.ConstructCost >= 0 && e.HireMarginPct >= 0;
        ok &= e.LossDaysFire is >= 1 and <= 3650 && e.LossDaysClose is >= 1 and <= 3650 && e.EwmaShift is >= 0 and <= 16 && e.CTargetPct >= 0;
        ok &= CityBudget.IsValid(in e);   // казна (city-economy.md §13)
        CityLifecycleDesc lf = t.Lifecycle;   // жизнь здания (building-lifecycle.md §5): только с казной
        ok &= !lf.Enabled || (e.CityBudget && lf.AbandonedDecayPct is >= 0 and <= 10_000 && lf.LowlandCm is >= 0 and <= 100_000 &&
            lf.WornRelocateQ16 is >= 0 and <= 65_536 && lf.RubbleCostPct is >= 0 and <= 100);
        ok &= ch.ThetaMode64 >= 0 && ch.ThetaDest64 >= 0 && ch.ThetaTime64 >= 0 && ch.ThetaFreq64 >= 0;
        ok &= ch.FreqMax is >= 0 and <= CityResidents.MaxTours && ch.WWaitQ8 >= 0 && ch.WWalkQ8 >= 0 && ch.SdeQ8 >= 0 && ch.SdlQ8 >= 0 && ch.BetaFreqQ8 >= 0;
        ok &= ch.TimeStepSlots is >= 1 and <= 144 && ch.DestCandidates is >= 1 and <= 64 && ch.MaxWalkMm >= 0;
        ok &= ch.SkimPeriods is >= 1 and <= 24 && ch.SkimShift is >= 0 and <= 8 && ch.SkimUpdatePerTick >= 1;
        ok &= IsValid(in t.Outside);
        if (!ok)
        {
            return false;
        }

        if (t.BuildingTypes != null)
        {
            if (t.BuildingTypes.Length > BuildingTypes.MaxTypes)
            {
                return false;
            }

            for (int i = 0; i < t.BuildingTypes.Length; i++)
            {
                if (!BuildingTypes.IsValid(in t.BuildingTypes[i]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Сетка города при Create: по границам мира (<c>zoning.grid_from_bounds</c>: <c>tools.world_bounds</c>, иначе протяжённость рельефа), иначе по bbox начальной сети.</summary>
    private static bool CreateGrid(World w, out CityGrid grid)
    {
        float cellM = w.LodCfg.CellM;
        if (w.Town.Zoning.GridFromBounds != 0)
        {
            ToolsDesc t = w.ToolsCfg;
            if (t.HasWorldBounds)
            {
                return CityGrid.TryCreate(t.BoundsMinX, t.BoundsMinZ, t.BoundsMaxX, t.BoundsMaxZ, cellM, out grid);
            }

            if (w.Terrain != null)
            {
                Terrain.TerrainStore s = w.Terrain;
                return CityGrid.TryCreate(s.OriginX, s.OriginZ, s.OriginX + ((s.SamplesX - 1) * s.CellM), s.OriginZ + ((s.SamplesZ - 1) * s.CellM), cellM, out grid);
            }
        }

        return CityGrid.TryCreate(w.Net, cellM, out grid);
    }

    private static bool Q16(int v) => v is >= 0 and <= 65536;

    /// <summary>Пределы секции <c>outside</c>: 0..4 подключений, узлы неотрицательны и различны, места и стоимость в пределах, сумма долей въезда = 65536 (при секции).</summary>
    public static bool IsValid(in CityOutsideDesc o)
    {
        if (o.Count == 0)
        {
            return true;
        }

        if (o.Count is < 0 or > CityOutsideDesc.Max || !Q16(o.JobsDecayQ16) || o.ArrivalByCar > 1)
        {
            return false;
        }

        long share = 0;
        for (int k = 0; k < o.Count; k++)
        {
            OutsideConnDesc c = o.Get(k);
            if (c.Node < 0 || c.Jobs is < 0 or > MaxBigCount || c.JobCostTicks is < 0 or > MaxDayTicks || !Q16(c.ShareQ16))
            {
                return false;
            }

            for (int j = 0; j < k; j++)
            {
                if (o.Get(j).Node == c.Node)
                {
                    return false;
                }
            }

            share += c.ShareQ16;
        }

        return share == 65536;
    }

    /// <summary>Узлы подключений при Create: живые узлы явной сети, флаг <see cref="NodeFlags.Outside"/> (в сейве - в столбце флагов узлов).</summary>
    private static bool MarkOutsideNodes(World w)
    {
        CityOutsideDesc o = w.Town.Outside;
        for (int k = 0; k < o.Count; k++)
        {
            int node = o.Get(k).Node;
            if (!w.Net.NodeAlive(node))
            {
                return false;
            }

            w.Net.Nodes[node].Flags |= Network.NodeFlags.Outside;
        }

        return true;
    }

    /// <summary>
    /// Строит город мира: сетка ячеек, хранилища по ёмкостям и (при <paramref name="create"/>) нарезка, автозонирование и начальное население. <c>create = false</c> -
    /// загрузчик сейва: хранилища пусты, состояние берёт из блоков, производные - <see cref="CityWorld.RestoreDerived"/> в конце загрузки. Без режима жителей ничего не делает.
    /// </summary>
    public static CreateStatus Build(World w, bool create, in CityGrid savedGrid)
    {
        if (w.Demand.Model != DemandModel.Residents)
        {
            return CreateStatus.Ok;
        }

        CityGrid grid = savedGrid;
        if (create && !CreateGrid(w, out grid))
        {
            return CreateStatus.InvalidCity;
        }

        if (grid.Cells < 1 || Skims.EntriesFor(grid.Cells, w.Town.Choice.SkimPeriods) > Skims.MaxSkimEntries)
        {
            return CreateStatus.NotSupported;   // s1m-res: двухуровневый ским - этап 4.1 (zoning-demand.md §5.1)
        }

        if (create && w.Town.Calendar.StartHour > 0)
        {
            w.Tick = (long)w.Town.Calendar.StartHour * w.Town.Calendar.DayTicks / 24;   // calendar.start_hour: мир стартует в середине суток (сутки 0)
            w.SegmentLoadsTick = w.Tick - 1;   // нагрузки сегментов "посчитаны" на тике до старта: пересчёт весов на первом тике (Tick кратен периоду) читает нули, как и в мире с нулевого тика
        }

        w.City = new CityWorld(in w.Town, in grid, in w.Caps, w);
        if (w.ParkCfg.Enabled)
        {
            w.City.Parking = new Parking.ParkingStore(in w.ParkCfg, in w.Caps, in grid);
        }

        if (create && w.City.Outside != null)
        {
            if (!MarkOutsideNodes(w))
            {
                return CreateStatus.InvalidCity;
            }

            w.City.Outside.Ensure(w, w.City);
        }

        return create ? CityInit.Run(w) : CreateStatus.Ok;
    }
}
