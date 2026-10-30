using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Tools;

// Жизнь здания (building-lifecycle.md §2.5, §2.10, §6.2, BL1/BL3): проверки DemolishBuilding и BuildService - те же для превью и применения.
internal static partial class ToolValidator
{
    /// <summary>
    /// <c>DemolishBuilding</c>: режим <c>city.lifecycle</c> (иначе <c>NotSupported</c>), живой слот с совпадающим поколением (иначе <c>InvalidHandle</c>), ещё не сносится
    /// (иначе <c>InvalidArgument</c>). Цена - <c>demolish_cost</c> типа здания; точка превью - центр участка.
    /// </summary>
    private static RejectReason CheckDemolishBuilding(World w, in Command cmd, ref ToolCheck c)
    {
        CityWorld? city = w.City;
        if (city == null || !city.Cfg.Lifecycle.Enabled)
        {
            return RejectReason.NotSupported;
        }

        EntityRef h = cmd.DemolishBuilding.Building;
        BuildingStore b = city.Buildings;
        if ((uint)h.Index >= (uint)b.Capacity || !b.IsAlive(h.Index) || b.Pool.GenerationOf(h.Index) != h.Generation)
        {
            return RejectReason.InvalidHandle;
        }

        int lot = b.Lot[h.Index];
        if ((uint)lot < (uint)city.Lots.X.Length)
        {
            c.AX = city.Lots.X[lot];
            c.AZ = city.Lots.Z[lot];
        }

        c.IssueKind = EntityKind.Building;
        c.IssueIndex = h.Index;
        c.IssueGen = h.Generation;
        c.IssueX = c.AX;
        c.IssueZ = c.AZ;
        if ((b.LifeFlags[h.Index] & BuildingLifeFlags.Demolishing) != 0)
        {
            return RejectReason.InvalidArgument;
        }

        c.Cost = city.Types.Rows[b.Type[h.Index]].DemolishCost;
        return RejectReason.None;
    }
    /// <summary>
    /// <c>RestoreBuilding</c> (building-lifecycle.md §2.9, §6.2): режим <c>city.lifecycle</c> (иначе <c>NotSupported</c>), живой слот с совпадающим поколением (иначе <c>InvalidHandle</c>);
    /// не <c>Demolishing</c>, не <c>Constructing</c>, <c>Cond</c> не ниже 50 % (ремонту не подлежит) и есть что чинить (<c>Cond &lt; 100 %</c> или <c>Ceil &lt; 100 %</c>) - иначе <c>InvalidArgument</c>;
    /// заброшенное - вид здания = зона участка и участок не <c>Lost</c>, иначе <c>ZoneRule</c>. Цена - <see cref="CityLife.RestoreCost"/>; точка превью - центр участка.
    /// </summary>
    private static RejectReason CheckRestoreBuilding(World w, in Command cmd, ref ToolCheck c)
    {
        CityWorld? city = w.City;
        if (city == null || !city.Cfg.Lifecycle.Enabled)
        {
            return RejectReason.NotSupported;
        }

        EntityRef h = cmd.RestoreBuilding.Building;
        BuildingStore b = city.Buildings;
        if ((uint)h.Index >= (uint)b.Capacity || !b.IsAlive(h.Index) || b.Pool.GenerationOf(h.Index) != h.Generation)
        {
            return RejectReason.InvalidHandle;
        }

        int lot = b.Lot[h.Index];
        if ((uint)lot < (uint)city.Lots.X.Length)
        {
            c.AX = city.Lots.X[lot];
            c.AZ = city.Lots.Z[lot];
        }

        c.IssueKind = EntityKind.Building;
        c.IssueIndex = h.Index;
        c.IssueGen = h.Generation;
        c.IssueX = c.AX;
        c.IssueZ = c.AZ;
        var st = (BuildingState)b.State[h.Index];
        if ((b.LifeFlags[h.Index] & BuildingLifeFlags.Demolishing) != 0 || st is not (BuildingState.Active or BuildingState.Abandoned)
            || b.Cond[h.Index] < CityLife.AbandonBp || (b.Cond[h.Index] >= CityLife.Full && b.Ceil[h.Index] >= CityLife.Full))
        {
            return RejectReason.InvalidArgument;
        }

        if (st == BuildingState.Abandoned && ((uint)lot >= (uint)city.Lots.X.Length || (city.Lots.Flags[lot] & LotFlags.Lost) != 0
            || city.Types.Rows[b.Type[h.Index]].Kind != (LotZone)city.Lots.Zone[lot]))
        {
            return RejectReason.ZoneRule;
        }

        c.Cost = CityLife.RestoreCost(city, h.Index);
        return RejectReason.None;
    }

    /// <summary>
    /// <c>BuildService</c> (building-lifecycle.md §2.10, §6.2): режим <c>city.lifecycle</c> (иначе <c>NotSupported</c>); участок в пределах и тип вида <c>Service</c> (иначе <c>InvalidArgument</c>);
    /// участок без здания, не <c>Lost</c>, не пристройка службы, с полосой (иначе <c>ZoneRule</c>); без режима <c>city.services</c> тип только ЖКХ, в режиме - уровень любой записи (иначе <c>NotSupported</c>);
    /// в режиме участок на сетке не меньше <c>lot_min</c> уровня (<c>ZoneRule</c>, <c>RejectValue</c> 1); свободных слотов бригад не меньше нужного и есть место станции (иначе <c>Capacity</c>).
    /// Цена без режима - <c>build_cost</c> типа, в режиме - сумма <c>build_cost</c> уровней записи до выбранного (services.md §2.8); веху уровня проверяет <c>Progression.Check</c>. Точка - центр участка.
    /// </summary>
    private static RejectReason CheckBuildService(World w, in Command cmd, ref ToolCheck c)
    {
        CityWorld? city = w.City;
        if (city == null || !city.Cfg.Lifecycle.Enabled || city.Services == null)
        {
            return RejectReason.NotSupported;
        }

        int lot = cmd.BuildService.Lot;
        int type = cmd.BuildService.Type;
        LotStore l = city.Lots;
        if ((uint)lot >= (uint)l.Count || (uint)type >= (uint)city.Types.Count || city.Types.Rows[type].Kind != LotZone.Service)
        {
            return RejectReason.InvalidArgument;
        }

        c.AX = l.X[lot];
        c.AZ = l.Z[lot];
        c.IssueX = c.AX;
        c.IssueZ = c.AZ;
        if (l.Building[lot] >= 0 || (l.Flags[lot] & (LotFlags.Lost | LotFlags.Annex)) != 0 || l.CarLane[lot] < 0 || l.RoadComp[lot] < 0)
        {
            return RejectReason.ZoneRule;
        }

        // Без режима city.services - только депо ЖКХ (BL); в режиме - любая станция записи таблицы (services.md §2.3, SR2)
        bool ok = city.Services.Table.Explicit ? city.Services.Table.RecOf(type) >= 0 : city.Types.Rows[type].ServiceKind == ServiceKind.Utility;
        if (!ok)
        {
            return RejectReason.NotSupported;
        }

        if (city.Services.Crews.FreeSlots() < Utilities.CrewsFor(city, type) || !city.Services.CanAllocSite(city.Buildings))
        {
            return RejectReason.Capacity;
        }

        if (!city.Services.Table.Explicit)
        {
            c.Cost = city.Types.Rows[type].BuildCost;
            return RejectReason.None;
        }

        // Режим city.services (§2.8): участок не меньше lot_min уровня (участок на сетке; legacy без клеток не проверяется), цена - сумма build_cost уровней до выбранного
        ServiceTable tb = city.Services.Table;
        int rec = tb.RecOf(type);
        int level = tb.LevelOf(type);
        ref readonly ServiceLevelDesc want = ref tb.LevelRow(rec, level);
        if (l.Orient[lot] != LotOrient.Legacy && (l.CellsF[lot] < want.LotMinF || l.CellsD[lot] < want.LotMinD))
        {
            c.RejectValue = ServiceLotTooSmall;
            return RejectReason.ZoneRule;
        }

        long cost = 0;
        for (int k = 1; k <= level; k++)
        {
            cost += tb.LevelRow(rec, k).BuildCost;
        }

        c.Cost = cost;
        return RejectReason.None;
    }
}
