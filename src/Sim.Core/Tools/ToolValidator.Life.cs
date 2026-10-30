using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Tools;

// Жизнь здания (building-lifecycle.md §2.5, §6.2, BL1): проверки DemolishBuilding - те же для превью и применения.
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
}
