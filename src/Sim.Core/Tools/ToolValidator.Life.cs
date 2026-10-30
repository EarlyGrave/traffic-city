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
    /// участок без здания, не <c>Lost</c>, с полосой (иначе <c>ZoneRule</c>); тип не ЖКХ - <c>NotSupported</c> (до SR5); свободных слотов бригад не меньше <c>Vehicles</c> типа и есть место станции (иначе <c>Capacity</c>). Цена - <c>build_cost</c>; точка - центр участка.
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
        if (l.Building[lot] >= 0 || (l.Flags[lot] & LotFlags.Lost) != 0 || l.CarLane[lot] < 0 || l.RoadComp[lot] < 0)
        {
            return RejectReason.ZoneRule;
        }

        if (city.Types.Rows[type].ServiceKind != ServiceKind.Utility)
        {
            return RejectReason.NotSupported;   // станции без выезда ЖКХ (медицина, пожарные) - с SR5
        }

        if (city.Services.Crews.FreeSlots() < city.Types.Rows[type].Vehicles || !city.Services.CanAllocSite(city.Buildings))
        {
            return RejectReason.Capacity;
        }

        c.Cost = city.Types.Rows[type].BuildCost;
        return RejectReason.None;
    }
}
