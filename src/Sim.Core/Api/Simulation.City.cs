using System;
using Sim.Core.City;
using Sim.Core.Demand;
using Sim.Core.Tools;

namespace Sim.Core.Api;

// Город (ADR-0011, zoning-demand.md §13): сводка, здания, участки, инспекторы. Без режима жителей все запросы возвращают false/0/пусто.
// События хода (BuildingSpawned и др.) добавляют Z2/Z4 - Z1 их не испускает.
public sealed partial class Simulation
{
    /// <summary>Есть ли в мире город (<c>demand.model = "residents"</c>).</summary>
    public bool HasCity => !_disposed && _w.City != null;

    /// <summary>Живых жителей города.</summary>
    public int CityPopulation => _disposed || _w.City == null ? 0 : _w.City.Residents.AliveCount;

    /// <summary>Номер суток города (0 без города).</summary>
    public int CityDay => _disposed || _w.City == null ? 0 : _w.City.Day;

    /// <summary>Длительность суток города в тиках (0 без города).</summary>
    public int CityDayTicks => _disposed || _w.City == null ? 0 : _w.City.DayTicks;

    /// <summary>
    /// Начало окна утреннего пика внутри суток, тик: за 6 слотов до самого раннего начала смены типов зданий (выезды рабочих кучкуются у начала смен ±3 слота), не меньше 0.
    /// Окно <c>bench --window am_peak</c> - 6 000 тиков отсюда (zoning-demand.md §11).
    /// </summary>
    public int CityAmPeakStartTickOfDay
    {
        get
        {
            CityWorld? c = _disposed ? null : _w.City;
            if (c == null)
            {
                return 0;
            }

            int earliest = CityWorld.SlotsPerDay;
            for (int i = 0; i < c.Types.Rows.Length; i++)
            {
                if (c.Types.Rows[i].ShiftSlots > 0 && c.Types.Rows[i].ShiftStartSlot < earliest)
                {
                    earliest = c.Types.Rows[i].ShiftStartSlot;
                }
            }

            return Math.Max(0, (earliest == CityWorld.SlotsPerDay ? 0 : earliest) - 6) * c.SlotTicks;
        }
    }

    /// <summary>Тиков, перепрыгнутых агрегатом без работы (zoning-demand.md §9.2): диагностика, в сейв и хэш не входит.</summary>
    public long CityJumpedTicks => _disposed ? 0 : _w.JumpedTicks;

    /// <summary>Пересечений клеток машинными ногами за вчерашние сутки (Σ <c>L</c> по клеткам и периодам); умноженное на <c>cell_m</c> - машино-км в сутки (оценка по линиям между центрами ячеек).</summary>
    public long CityCarCellCrossingsYesterday
    {
        get
        {
            CityWorld? c = _disposed ? null : _w.City;
            if (c == null)
            {
                return 0;
            }

            long sum = 0;
            int slot = (c.Day + 1) & 1;
            for (int i = slot; i < c.Loads.L.Length; i += 2)
            {
                sum += c.Loads.L[i];
            }

            return sum;
        }
    }

    /// <summary>Сторона ячейки города, м (0 без города).</summary>
    public float CityCellM => _disposed || _w.City == null ? 0f : _w.City.Grid.CellM;

    /// <summary>Парки и вода генератора (city.landuse, visual-style.md §8): снапшот-чтение полигонов, пусто без землепользования. Статично после Create; сохраняется в блоке LUSE (world_version 10) и восстанавливается в Load (сейвы до v10 полигонов не несут - после их миграции пусто).</summary>
    public ReadOnlySpan<LandUsePoly> LandUse => _disposed ? default : _w.LandUse;

    /// <summary>Версия землепользования: 0 - нет, 1 - построено в Create (инструменты игрока полигоны не меняют); клиент перечитывает <see cref="LandUse"/> только при смене.</summary>
    public int LandUseVersion => _disposed ? 0 : _w.LandUseVersion;

    /// <summary>Версия зданий: клиент перечитывает <see cref="GetBuildings"/> только при её смене.</summary>
    public int BuildingsVersion => _disposed || _w.City == null ? 0 : _w.City.Buildings.Version;

    /// <summary>Версия участков: клиент перечитывает <see cref="GetLots"/> только при её смене.</summary>
    public int LotsVersion => _disposed || _w.City == null ? 0 : _w.City.Lots.Version;

    /// <summary>Число слотов зданий (HighWater): минимальная длина буфера <see cref="GetBuildings"/>.</summary>
    public int BuildingSlotCount => _disposed || _w.City == null ? 0 : _w.City.Buildings.HighWater;

    /// <summary>Число участков: минимальная длина буфера <see cref="GetLots"/>.</summary>
    public int LotCount => _disposed || _w.City == null ? 0 : _w.City.Lots.Count;

    /// <summary>Число ячеек сетки города (длина буфера <see cref="GetAccessibility"/>).</summary>
    public int CityCellCount => _disposed || _w.City == null ? 0 : _w.City.Grid.Cells;

    /// <summary>Сводка города на конец последнего тика; false - города нет. O(жителей + зданий).</summary>
    public bool GetCityStats(out CityStats stats)
    {
        stats = default;
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return false;
        }

        BuildingStore b = c.Buildings;
        CityResidents r = c.Residents;
        stats.Day = c.Day;
        stats.Population = r.AliveCount;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (!r.IsAlive(i))
            {
                continue;
            }

            if ((r.Flags[i] & ResidentFlags.Labour) != 0)
            {
                stats.Labour++;
                if (r.Work[i] != -1)
                {
                    stats.Employed++;
                }
                else
                {
                    stats.Seeking++;
                }
            }

            stats.BalanceResidents += r.Account[i];
        }

        for (int i = 0; i < b.HighWater; i++)
        {
            if (!b.IsAlive(i))
            {
                continue;
            }

            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[i]];
            switch (t.Kind)
            {
                case LotZone.Residential:
                    stats.BuildingsR++;
                    break;
                case LotZone.Commercial:
                    stats.BuildingsC++;
                    break;
                case LotZone.Service:
                    break;   // депо службы (building-lifecycle.md §2.10) - не R/C/I
                default:
                    stats.BuildingsI++;
                    break;
            }

            switch ((BuildingState)b.State[i])
            {
                case BuildingState.Constructing:
                    stats.Constructing++;
                    break;
                case BuildingState.Active:
                    stats.Active++;
                    if (t.Kind == LotZone.Residential)
                    {
                        stats.FreeHousing += Math.Max(0, t.Capacity - b.Occupants[i]);
                    }
                    else
                    {
                        stats.Vacancies += Math.Max(0, b.Jobs[i] - b.Occupants[i]);
                    }

                    break;
                default:
                    stats.Abandoned++;
                    break;
            }

            stats.BalanceFirms += b.Account[i];
        }

        LotStore l = c.Lots;
        stats.LotsTotal = l.Count;
        for (int i = 0; i < l.Count; i++)
        {
            stats.LotsZoned += l.Zone[i] != 0 ? 1 : 0;
            stats.LotsLost += (l.Flags[i] & LotFlags.Lost) != 0 ? 1 : 0;
        }

        stats.DemandR = c.DemandR;
        stats.DemandC = c.DemandC;
        stats.DemandI = c.DemandI;
        int y = (c.Day + 1) & 1;   // «вчера»: другой элемент пары накопителей
        stats.ToursYesterday = c.ToursDay[y];
        stats.LegsYesterdayCar = c.LegsDay[(y * Skims.Modes) + (int)TravelMode.Car];
        stats.LegsYesterdayBus = c.LegsDay[(y * Skims.Modes) + (int)TravelMode.Bus];
        stats.LegsYesterdayWalk = c.LegsDay[(y * Skims.Modes) + (int)TravelMode.Walk];
        stats.AvgCostCarDs = Avg(c, y, TravelMode.Car);
        stats.AvgCostBusDs = Avg(c, y, TravelMode.Bus);
        stats.AvgCostWalkDs = Avg(c, y, TravelMode.Walk);
        stats.BalanceExternal = c.Ledger.External;
        stats.BalanceCity = c.Ledger.City;
        stats.ResStuck = _w.Counters.ResStuck;
        stats.ExecMode = (int)c.Exec;
        return true;
    }

    private static int Avg(CityWorld c, int y, TravelMode m)
    {
        int idx = (y * Skims.Modes) + (int)m;
        return c.LegsDay[idx] > 0 ? (int)(c.LegCostDay[idx] / c.LegsDay[idx]) : 0;
    }

    /// <summary>Число видов потоков денег (<see cref="FlowKind"/>): минимальная длина буфера <see cref="CopyFlowTotals"/>.</summary>
    public const int FlowKindCount = Ledger.FlowCount;

    /// <summary>Потоки денег по <see cref="FlowKind"/> за вчера, за текущие сутки или с начала симуляции (<paramref name="period"/>); длина <paramref name="dst"/> не меньше <see cref="FlowKindCount"/> (22).</summary>
    public bool CopyFlowTotals(Span<long> dst, FlowPeriod period)
    {
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null || dst.Length < Ledger.FlowCount)
        {
            return false;
        }

        long[] src = period switch
        {
            FlowPeriod.Yesterday => c.Ledger.FlowYesterday,
            FlowPeriod.Today => c.Ledger.FlowDay,
            _ => c.Ledger.FlowTotal,
        };
        src.AsSpan().CopyTo(dst);
        return true;
    }

    /// <summary>
    /// Состояние казны (city-economy.md §10); false - города нет либо режим <c>city_budget</c> выключен. Кредитный лимит - <c>credit_days * IncomeEwma</c>; ноги без оплаты и выселения за долг - за вчера.
    /// </summary>
    public bool GetCityBudget(out CityBudgetInfo info)
    {
        info = default;
        CityWorld? c = _disposed ? null : _w.City;
        CityBudget? b = c?.Budget;
        if (c == null || b == null)
        {
            return false;
        }

        int y = (c.Day + 1) & 1;   // «вчера»: другой элемент пары накопителей
        info = new CityBudgetInfo
        {
            Balance = c.Ledger.City,
            IncomeEwma = b.IncomeEwma,
            ExpenseEwma = b.ExpenseEwma,
            CreditLimit = b.CreditLimit(in c.Cfg.Economy, City.Progression.CreditDaysBonus(_w)),
            MaintRoadDay = b.MaintRoadDay,
            MaintBusDay = b.MaintBusDay,
            FareEvadedYesterday = b.FareEvadedDay[y],
            DebtEvictionsYesterday = b.DebtEvictionsDay[y],
            DebtDays = b.DebtDays,
            OverLimitDays = b.OverLimitDays,
            Insolvent = b.Insolvent,
            HasPending = b.HasPending,
            Policy = b.Policy,
            PendingPolicy = b.PendingPolicy,
        };
        return true;
    }

    /// <summary>
    /// Цена места по ячейкам для heatmap (city-economy.md §4, §10): тот же массив, что берёт земельный налог; деньги. Длина <paramref name="perCell"/> не меньше <see cref="CityCellCount"/>.
    /// Возвращает число записанных ячеек; 0 - города или режима <c>city_budget</c> нет.
    /// </summary>
    public int GetLandValue(Span<int> perCell)
    {
        CityBudget? b = _disposed ? null : _w.City?.Budget;
        if (b == null)
        {
            return 0;
        }

        int n = Math.Min(perCell.Length, b.LandValue.Length);
        b.LandValue.AsSpan(0, n).CopyTo(perCell);
        return n;
    }

    /// <summary>Здания для клиента по слотам пула; возвращает число записанных слотов (<c>min(BuildingSlotCount, dst.Length)</c>), мёртвые слоты - <c>Kind = 0</c>.</summary>
    public int GetBuildings(Span<BuildingRenderData> dst)
    {
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return 0;
        }

        BuildingStore b = c.Buildings;
        int n = Math.Min(dst.Length, b.HighWater);
        long tick = _w.Tick;
        for (int i = 0; i < n; i++)
        {
            if (!b.IsAlive(i))
            {
                dst[i] = default;
                continue;
            }

            int lot = b.Lot[i];
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[i]];
            int fill;
            if (b.State[i] == (byte)BuildingState.Constructing)
            {
                long total = Math.Max(1L, (long)t.ConstructDays * c.DayTicks);
                fill = (int)Math.Clamp((tick - b.StateTick[i]) * 100L / total, 0L, 100L);
            }
            else
            {
                int cap = t.Kind == LotZone.Residential ? t.Capacity : Math.Max(1, b.Jobs[i]);
                fill = Math.Min(100, b.Occupants[i] * 100 / Math.Max(1, cap));
            }

            dst[i] = new BuildingRenderData
            {
                X = lot >= 0 ? c.Lots.X[lot] : 0f,
                Z = lot >= 0 ? c.Lots.Z[lot] : 0f,
                Heading = lot >= 0 ? c.Lots.Heading[lot] : 0f,
                Type = (ushort)b.Type[i],
                Kind = (byte)t.Kind,
                State = b.State[i],
                FillPct = (byte)fill,
                R0 = c.Cfg.Lifecycle.Enabled ? (byte)(b.Cond[i] / 100) : (byte)0,              // building-lifecycle.md §3: состояние, % (без режима 0)
                R1 = c.Cfg.Lifecycle.Enabled ? CityLife.RenderFlags(c, i) : (ushort)0,    // BuildingRenderFlags
                Gen = b.Pool.GenerationOf(i),
            };
        }

        return n;
    }

    /// <summary>Участки для клиента по номеру; возвращает число записанных (<c>min(LotCount, dst.Length)</c>).</summary>
    public int GetLots(Span<LotRenderData> dst)
    {
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return 0;
        }

        LotStore l = c.Lots;
        int n = Math.Min(dst.Length, l.Count);
        for (int i = 0; i < n; i++)
        {
            byte flags = (byte)(l.Flags[i] & (LotFlags.Lost | LotFlags.Painted));
            flags |= (l.Flags[i] & LotFlags.Annex) != 0 ? LotRenderFlags.Annex : (byte)0;
            if (l.Building[i] >= 0)
            {
                flags |= LotRenderFlags.HasBuilding;
                if (c.Buildings.State[l.Building[i]] == (byte)BuildingState.Abandoned)
                {
                    flags |= LotRenderFlags.Abandoned;
                }
            }

            dst[i] = new LotRenderData
            {
                X = l.X[i],
                Z = l.Z[i],
                Heading = l.Heading[i],
                Zone = l.Zone[i],
                Density = l.Density[i],
                Flags = flags,
                Orient = l.Orient[i],
                CellsF = l.CellsF[i],
                CellsD = l.CellsD[i],
                CellX = l.CellX[i],
                CellZ = l.CellZ[i],
            };
        }

        return n;
    }

    /// <summary>Поколение живого жителя или 0 (индекс не жив): пара с индексом для <see cref="TryGetResidentInfo"/>.</summary>
    public int GetResidentGeneration(int index)
    {
        CityWorld? c = _disposed ? null : _w.City;
        return c != null && c.Residents.IsAlive(index) ? c.Residents.Pool.GenerationOf(index) : 0;
    }

    /// <summary>Поколение живого здания или 0.</summary>
    public int GetBuildingGeneration(int index)
    {
        CityWorld? c = _disposed ? null : _w.City;
        return c != null && c.Buildings.IsAlive(index) ? c.Buildings.Pool.GenerationOf(index) : 0;
    }

    /// <summary>Копия полей жителя; false - города нет или пара (индекс, поколение) устарела. (Заменяется на <c>EntityRef</c> с волной инструментов T1.)</summary>
    public bool TryGetResidentInfo(int index, int generation, out ResidentInfo info)
    {
        info = default;
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null || !c.Residents.Pool.IsAlive(index, generation))
        {
            return false;
        }

        CityResidents r = c.Residents;
        info = new ResidentInfo
        {
            Home = r.Home[index],
            Work = r.Work[index],
            State = (r.Flags[index] & ResidentFlags.Arriving) != 0 ? ResidentState.Arriving : (ResidentState)r.State[index],
            Flags = r.Flags[index],
            Mode = r.LegMode[index],
            Agent = r.Agent[index],
            AgentGen = r.AgentGen[index],
            Balance = r.Account[index],
            StateTick = r.StateTick[index],
            EvTick = r.EvTick[index],
        };
        return true;
    }

    /// <summary>Копия полей здания; false - города нет или пара (индекс, поколение) устарела.</summary>
    public bool TryGetBuildingInfo(int index, int generation, out BuildingInfo info)
    {
        info = default;
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null || !c.Buildings.Pool.IsAlive(index, generation))
        {
            return false;
        }

        BuildingStore b = c.Buildings;
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[index]];
        info = new BuildingInfo
        {
            Lot = b.Lot[index],
            Type = b.Type[index],
            Kind = t.Kind,
            State = (BuildingState)b.State[index],
            Occupants = b.Occupants[index],
            Jobs = b.Jobs[index],
            Capacity = t.Capacity,
            Balance = b.Account[index],
            ProfitEwma = b.ProfitEwma[index],
            StateTick = b.StateTick[index],
        };
        return true;
    }

    /// <summary>
    /// Жизнь здания для панели (building-lifecycle.md §3, §6): состояние/потолок, возраст, значимость, признаки, ремонт ЖКХ, цены капремонта/ремонта/сноса.
    /// false - города или режима <c>city.lifecycle</c> нет либо пара (индекс, поколение) устарела.
    /// </summary>
    public bool TryGetBuildingLife(int index, int generation, out BuildingLifeInfo info)
    {
        info = default;
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null || !c.Cfg.Lifecycle.Enabled || !c.Buildings.Pool.IsAlive(index, generation))
        {
            return false;
        }

        BuildingStore b = c.Buildings;
        var st = (BuildingState)b.State[index];
        info.CondBp = b.Cond[index];
        info.CeilBp = b.Ceil[index];
        info.AgeDays = Math.Max(0, c.Day - b.BornDay[index]);
        info.Signif = b.Signif[index];
        info.SignifLevel = CityLife.Level(b.Signif[index]);
        info.Flags = CityLife.RenderFlags(c, index);
        info.RepairState = RepairStateOf(c, index);
        info.HarmStage = st == BuildingState.Abandoned && (b.LifeFlags[index] & BuildingLifeFlags.Demolishing) == 0 ? CityLife.HarmStage(b.Cond[index]) : (byte)0;
        info.DemolishPrice = c.Types.Rows[b.Type[index]].DemolishCost;
        info.RepairPrice = Utilities.RepairCost(c, index);
        var probe = Command.MakeRestoreBuilding(new EntityRef { Index = index, Generation = generation });
        var chk = default(ToolCheck);
        if (ToolValidator.Check(_w, in probe, ref chk) == RejectReason.None)
        {
            info.CanRestore = 1;
            info.RestorePrice = chk.Cost;
        }

        return true;
    }

    private static RepairState RepairStateOf(CityWorld c, int bi)
    {
        CrewStore? u = c.Services?.Crews;
        if (u == null || (c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Requested) == 0)
        {
            return RepairState.None;
        }

        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.Target[k] == bi && u.TargetGen[k] == c.Buildings.Pool.GenerationOf(bi))
            {
                return u.State[k] == CrewState.Working ? RepairState.Working : RepairState.CrewEnRoute;
            }
        }

        return RepairState.Queued;
    }

    /// <summary>
    /// Фонд и работа ЖКХ (building-lifecycle.md §2.10): баланс, тариф и субсидия (действующие и ожидающие), очередь, бригады, ремонтов в сутки, средняя задержка, «фонд пуст».
    /// false - города, режима <c>city.lifecycle</c> или казны нет.
    /// </summary>
    public bool GetUtilityInfo(out UtilityInfo info)
    {
        info = default;
        CityWorld? c = _disposed ? null : _w.City;
        CrewStore? u = c?.Services?.Crews;
        CityBudget? bud = c?.Budget;
        if (c == null || u == null || bud == null || !c.Cfg.Lifecycle.Enabled)
        {
            return false;
        }

        BudgetPolicy pend = bud.HasPending != 0 ? bud.PendingPolicy : bud.Policy;
        info.Fund = c.Ledger.Utility;
        info.RepairsDone = _w.Counters.RepairsDone;
        info.RepairDelayTicksTotal = _w.Counters.RepairDelayTicksTotal;
        info.AvgDelayTicks = info.RepairsDone > 0 ? info.RepairDelayTicksTotal / info.RepairsDone : 0;
        info.RepairsPerDayX100 = info.RepairsDone * 100 / Math.Max(1, c.Day);
        info.Tariff = bud.Policy.R0;
        info.PendingTariff = pend.R0;
        info.MaxTariff = c.Cfg.Lifecycle.MaxUtilityTariff;
        info.QueueLength = c.Services!.UtilityQueue?.Count ?? 0;
        info.Crews = u.Crews();
        for (int k = 0; k < u.Capacity; k++)
        {
            info.CrewsBusy += u.State[k] is CrewState.ToSite or CrewState.Working or CrewState.Return ? 1 : 0;
        }

        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].ServiceKind == ServiceKind.Utility)
            {
                info.Depots++;
            }
        }

        info.Subsidy = (bud.Policy.Flags & BudgetPolicyFlags.UtilitySubsidy) != 0 ? (byte)1 : (byte)0;
        info.PendingSubsidy = (pend.Flags & BudgetPolicyFlags.UtilitySubsidy) != 0 ? (byte)1 : (byte)0;
        info.FundEmpty = info.Fund <= 0 ? (byte)1 : (byte)0;
        return true;
    }

    /// <summary>
    /// Поле доступности по ячейкам для heatmap (zoning-demand.md §6.7): <see cref="AccessKind.Shops"/> - logsum до C-целей по всем способам (с машиной), <see cref="AccessKind.Jobs"/> -
    /// до рабочих мест; децисекунды, меньше - доступнее (значения могут быть отрицательными), 0 - целей нет. Длина <paramref name="perCell"/> не меньше <see cref="CityCellCount"/>. Возвращает число ячеек.
    /// </summary>
    public int GetAccessibility(Span<int> perCell, AccessKind kind)
    {
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return 0;
        }

        int n = Math.Min(perCell.Length, c.Grid.Cells);
        Skims s = c.Skims;
        for (int i = 0; i < n; i++)
        {
            int v = kind == AccessKind.Jobs ? s.LsJobs[i] : Accessibility.Combined(s, i, true, c.Cfg.Choice.ThetaMode64);
            perCell[i] = v == Skims.NoLs ? 0 : v;
        }

        return n;
    }
}

/// <summary>Период суммы потоков денег (<see cref="Simulation.CopyFlowTotals"/>).</summary>
public enum FlowPeriod : byte
{
    Yesterday = 0,
    Today = 1,
    Total = 2,
}
