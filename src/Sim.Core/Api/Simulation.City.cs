using System;
using Sim.Core.City;
using Sim.Core.Demand;

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
                if (r.Work[i] >= 0)
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

    /// <summary>Потоки денег по <see cref="FlowKind"/> за вчера (<paramref name="yesterday"/>), за текущие сутки или с начала симуляции; длина <paramref name="dst"/> не меньше 13.</summary>
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
            byte flags = l.Flags[i];
            if (l.Building[i] >= 0)
            {
                flags |= LotRenderFlags.HasBuilding;
            }

            dst[i] = new LotRenderData { X = l.X[i], Z = l.Z[i], Heading = l.Heading[i], Zone = l.Zone[i], Density = l.Density[i], Flags = flags };
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
            State = (ResidentState)r.State[index],
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
    /// Поле доступности по ячейкам для heatmap (zoning-demand.md §6.7). Z1: нули (поле считает дневной проход Z3); длина <paramref name="perCell"/> не меньше
    /// <see cref="CityCellCount"/>. Возвращает число ячеек.
    /// </summary>
    public int GetAccessibility(Span<int> perCell, AccessKind kind)
    {
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return 0;
        }

        int n = Math.Min(perCell.Length, c.Grid.Cells);
        perCell[..n].Clear();
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
