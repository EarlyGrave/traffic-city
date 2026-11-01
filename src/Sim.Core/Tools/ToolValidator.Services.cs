using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Tools;

// Уровни и апгрейды служб (services.md §2.8, §6, SR5): проверки UpgradeService и SetServiceUpgrade - те же для превью и применения.
internal static partial class ToolValidator
{
    /// <summary><c>RejectValue</c> при <see cref="RejectReason.ZoneRule"/> у служб: участок мал для <c>lot_min</c> уровня.</summary>
    public const int ServiceLotTooSmall = 1;

    /// <summary><c>RejectValue</c> при <see cref="RejectReason.ZoneRule"/> у служб: пристройки не идут подряд по фасаду / другая сторона или сегмент.</summary>
    public const int ServiceAnnexNotAdjacent = 2;

    /// <summary><c>RejectValue</c> при <see cref="RejectReason.ZoneRule"/> у служб: пристройку выкупить нельзя (служба, памятник).</summary>
    public const int ServiceAnnexProtected = 3;

    /// <summary>
    /// Общая часть: режим служб (иначе <c>NotSupported</c>), живое здание с совпадающим поколением (иначе <c>InvalidHandle</c>), это станция записи и она в работе - не заброшена, не сносится
    /// и не горит (иначе <c>InvalidArgument</c> / <c>Busy</c>). Точка превью - центр участка здания.
    /// </summary>
    private static RejectReason CheckStation(World w, EntityRef h, ref ToolCheck c, out int site)
    {
        site = -1;
        CityWorld? city = w.City;
        if (city == null || !city.Cfg.Lifecycle.Enabled || city.Services is not { Table.Explicit: true })
        {
            return RejectReason.NotSupported;
        }

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
        site = ServiceLevels.SiteOf(city, h.Index);
        if (site < 0 || b.State[h.Index] != (byte)BuildingState.Active)
        {
            return RejectReason.InvalidArgument;
        }

        return (b.LifeFlags[h.Index] & (BuildingLifeFlags.Demolishing | BuildingLifeFlags.Burning)) != 0 ? RejectReason.Busy : RejectReason.None;
    }

    /// <summary>
    /// <c>UpgradeService</c> (§2.8, §6): станция в работе; есть следующий уровень (иначе <c>InvalidArgument</c>); пристройки - различные существующие участки того же фасада, подряд с участком станции
    /// и её прежними пристройками (иначе <c>ZoneRule</c>, <c>RejectValue</c> 2), не Lost, без службы и памятника (<c>RejectValue</c> 3); участок станции вместе с пристройками не меньше <c>lot_min</c> следующего
    /// уровня (<c>ZoneRule</c>, <c>RejectValue</c> 1); пристроек на станцию не больше <see cref="SiteStore.MaxAnnex"/> и свободных слотов бригад хватает (<c>Capacity</c>). Цена: <c>build_cost</c> уровня +
    /// выкуп (<c>annex_buy_pct</c> % от земли + <c>demolish_cost</c> здания); выкуп undo не возвращает. С <see cref="CommandFlags.Inverse"/> - понижение: уровень выше 1, перечисленные пристройки - этой станции;
    /// цена - по undo-кредиту (нет кредита - 0).
    /// </summary>
    private static RejectReason CheckUpgradeService(World w, in Command cmd, ref ToolCheck c)
    {
        UpgradeServiceCmd p = cmd.UpgradeService;
        RejectReason r = CheckStation(w, p.Building, ref c, out int site);
        if (r != RejectReason.None)
        {
            return r;
        }

        CityWorld city = w.City!;
        CityServices sv = city.Services!;
        LotStore l = city.Lots;
        SiteStore s = sv.Sites;
        int rec = s.Svc[site];
        int level = s.Level[site];
        bool inverse = (cmd.Flags & CommandFlags.Inverse) != 0;
        for (int k = 0; k < 3; k++)
        {
            int a = p.AnnexAt(k);
            if (a == -1)
            {
                continue;
            }

            if ((uint)a >= (uint)l.Count)
            {
                return RejectReason.InvalidArgument;
            }

            for (int j = 0; j < k; j++)
            {
                if (p.AnnexAt(j) == a)
                {
                    return RejectReason.InvalidArgument;
                }
            }
        }

        if (inverse)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = p.AnnexAt(k);
                if ((a >= 0 && !ServiceLevels.IsAnnexOf(s, site, a)) || level <= 1)
                {
                    return RejectReason.InvalidArgument;
                }
            }

            return ServiceLevels.ResizeCheck(sv, site, ServiceLevels.MaxFor(sv, in sv.Table.LevelRow(rec, level - 1), 0));
        }

        if (level >= sv.Table.Records[rec].LevelCount)
        {
            return RejectReason.InvalidArgument;
        }

        int main = city.Buildings.Lot[p.Building.Index];
        int existing = ServiceLevels.AnnexCount(s, site);
        int adding = 0;
        long buyout = 0;
        int demolish = 0;
        int frontage = ServiceLevels.FrontCells(l, main);
        int depth = ServiceLevels.DepthCells(l, main);
        Span<int> ranks = stackalloc int[SiteStore.MaxAnnex + 1];
        ranks[0] = ServiceLevels.Rank(l, main);
        int rn = 1;
        for (int k = 0; k < SiteStore.MaxAnnex; k++)
        {
            int a = s.Annex[(site * SiteStore.MaxAnnex) + k];
            if (a >= 0)
            {
                frontage += ServiceLevels.FrontCells(l, a);
                depth = Math.Max(depth, ServiceLevels.DepthCells(l, a));
                ranks[rn++] = ServiceLevels.Rank(l, a);
            }
        }

        long landUnit = 0;
        for (int k = 0; k < 3; k++)
        {
            int a = p.AnnexAt(k);
            if (a < 0)
            {
                continue;
            }

            c.RejectValue = ServiceAnnexNotAdjacent;
            if (a == main || (l.Flags[a] & (LotFlags.Lost | LotFlags.Annex)) != 0 || !ServiceLevels.SameFrontage(l, a, main))
            {
                return RejectReason.ZoneRule;
            }

            if (existing + adding + 1 > SiteStore.MaxAnnex)
            {
                c.RejectValue = 0;
                return RejectReason.Capacity;
            }

            int old = l.Building[a];
            if (old >= 0)
            {
                c.RejectValue = ServiceAnnexProtected;
                BuildingStore b = city.Buildings;
                if (!b.IsAlive(old) || city.Types.Rows[b.Type[old]].Kind == LotZone.Service || (b.LifeFlags[old] & BuildingLifeFlags.Monument) != 0)
                {
                    return RejectReason.ZoneRule;
                }

                if ((b.LifeFlags[old] & BuildingLifeFlags.Burning) != 0)
                {
                    return RejectReason.Busy;
                }

                if ((b.LifeFlags[old] & BuildingLifeFlags.Demolishing) == 0)
                {
                    buyout += city.Types.Rows[b.Type[old]].DemolishCost;
                    demolish++;
                }
            }

            c.RejectValue = 0;
            if (city.Budget != null)
            {
                landUnit = BudgetFlows.LandAt(city, city.Budget, a);
                buyout += landUnit * Math.Max(1, ServiceLevels.FrontCells(l, a) * ServiceLevels.DepthCells(l, a)) * city.Cfg.Services.AnnexBuyPct / 100;
            }

            frontage += ServiceLevels.FrontCells(l, a);
            depth = Math.Max(depth, ServiceLevels.DepthCells(l, a));
            ranks[rn++] = ServiceLevels.Rank(l, a);
            adding++;
        }

        int lo = ranks[0];
        int hi = ranks[0];
        for (int i = 1; i < rn; i++)
        {
            lo = Math.Min(lo, ranks[i]);
            hi = Math.Max(hi, ranks[i]);
        }

        if (hi - lo + 1 != rn)
        {
            c.RejectValue = ServiceAnnexNotAdjacent;
            return RejectReason.ZoneRule;
        }

        ref readonly ServiceLevelDesc next = ref sv.Table.LevelRow(rec, level + 1);
        if (l.Orient[main] != LotOrient.Legacy && (frontage < next.LotMinF || depth < next.LotMinD))
        {
            c.RejectValue = ServiceLotTooSmall;
            return RejectReason.ZoneRule;
        }

        RejectReason fit = ServiceLevels.ResizeCheck(sv, site, ServiceLevels.MaxFor(sv, in next, 0));
        if (fit != RejectReason.None)
        {
            return fit;
        }

        c.RejectValue = 0;
        c.Cost = next.BuildCost + buyout;
        c.NoRefundCost = buyout;
        c.BuyoutBuildings = demolish;
        return RejectReason.None;
    }

    /// <summary>
    /// <c>SetServiceUpgrade</c> (§2.8, §6): станция в работе; номер апгрейда есть у её уровня и состояние меняется (иначе <c>InvalidArgument</c>); при росте или снятии апгрейда <c>units</c> хватает
    /// слотов бригад / есть кому уйти (<c>Capacity</c> / <c>Busy</c>). Цена включения - <c>cost</c>; выключение не возвращает ничего, кроме undo-кредита (обратная команда с битом Inverse).
    /// </summary>
    private static RejectReason CheckSetServiceUpgrade(World w, in Command cmd, ref ToolCheck c)
    {
        SetServiceUpgradeCmd p = cmd.SetServiceUpgrade;
        RejectReason r = CheckStation(w, p.Building, ref c, out int site);
        if (r != RejectReason.None)
        {
            return r;
        }

        CityServices sv = w.City!.Services!;
        SiteStore s = sv.Sites;
        ref readonly ServiceLevelDesc l = ref sv.Table.LevelRow(s.Svc[site], s.Level[site]);
        bool on = p.On != 0;
        if ((uint)p.Id >= (uint)l.UpgradeCount || ((s.UpgradeMask[site] & (1 << p.Id)) != 0) == on)
        {
            return RejectReason.InvalidArgument;
        }

        byte mask = on ? (byte)(s.UpgradeMask[site] | (1 << p.Id)) : (byte)(s.UpgradeMask[site] & ~(1 << p.Id));
        RejectReason fit = ServiceLevels.ResizeCheck(sv, site, ServiceLevels.MaxFor(sv, in l, mask));
        if (fit != RejectReason.None)
        {
            return fit;
        }

        c.Cost = on ? sv.Table.Upgrades[l.FirstUpgrade + p.Id].Cost : 0;   // подделанный бит Inverse у включения цену не снимает (как у прочих инструментов)
        return RejectReason.None;
    }
}
