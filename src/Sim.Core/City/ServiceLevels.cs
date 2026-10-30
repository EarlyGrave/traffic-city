using System;
using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Уровни служб, апгрейд на месте, пристройки и локальные апгрейды (services.md §2.8, SR5). Здание станции меняет тип на тип следующего уровня (<c>BuildingStore.Type</c>), станция
/// хранит уровень, маску локальных апгрейдов уровня и до <see cref="SiteStore.MaxAnnex"/> участков-пристроек (<see cref="LotFlags.Annex"/>). Всё - в фазе команд главного потока; 0 Б/тик
/// (команды редкие, обходы по участкам и станциям не в горячем цикле).
/// </summary>
internal static class ServiceLevels
{
    /// <summary>Станция живого здания <paramref name="bi"/> или -1.</summary>
    public static int SiteOf(CityWorld c, int bi)
    {
        CityServices? sv = c.Services;
        if (sv == null || !c.Buildings.IsAlive(bi))
        {
            return -1;
        }

        int gen = c.Buildings.Pool.GenerationOf(bi);
        SiteStore s = sv.Sites;
        for (int i = 0; i < s.Capacity; i++)
        {
            if (s.Building[i] == bi && s.Gen[i] == gen)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Пристроек станции <paramref name="site"/>.</summary>
    public static int AnnexCount(SiteStore s, int site)
    {
        int n = 0;
        for (int k = 0; k < SiteStore.MaxAnnex; k++)
        {
            n += s.Annex[(site * SiteStore.MaxAnnex) + k] >= 0 ? 1 : 0;
        }

        return n;
    }

    /// <summary>Участок <paramref name="lot"/> - пристройка станции <paramref name="site"/>.</summary>
    public static bool IsAnnexOf(SiteStore s, int site, int lot)
    {
        for (int k = 0; k < SiteStore.MaxAnnex; k++)
        {
            if (s.Annex[(site * SiteStore.MaxAnnex) + k] == lot)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Бригад (занятых слотов) станции <paramref name="site"/>.</summary>
    public static int CrewCount(CrewStore u, int site)
    {
        int n = 0;
        for (int k = 0; k < u.Capacity; k++)
        {
            n += u.Site[k] == site && u.State[k] != CrewState.Free ? 1 : 0;
        }

        return n;
    }

    /// <summary><c>UnitsMax</c> уровня <paramref name="l"/> при маске апгрейдов <paramref name="mask"/> (§2.4).</summary>
    public static int MaxFor(CityServices sv, in ServiceLevelDesc l, byte mask) =>
        Utilities.UnitsMaxOf(l.Units, sv.Table.UpgradeEffect(in l, mask, ServiceUpgradeEffect.Units));

    /// <summary>
    /// Можно ли привести станцию к <paramref name="newMax"/> машин: не хватает - нужны свободные слоты бригад (<see cref="RejectReason.Capacity"/>), лишних должно быть кому уйти -
    /// только стоящие на станции (<see cref="RejectReason.Busy"/>: едущие и работающие доезжают, потом повторить).
    /// </summary>
    public static RejectReason ResizeCheck(CityServices sv, int site, int newMax)
    {
        int have = CrewCount(sv.Crews, site);
        if (have < newMax)
        {
            return sv.Crews.FreeSlots() >= newMax - have ? RejectReason.None : RejectReason.Capacity;
        }

        int spare = 0;
        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity; k++)
        {
            spare += u.Site[k] == site && u.State[k] is CrewState.Idle or CrewState.OffDuty ? 1 : 0;
        }

        return have - newMax <= spare ? RejectReason.None : RejectReason.Busy;
    }

    /// <summary>
    /// Привести бригады станции к <c>UnitsMax</c> текущего уровня и маски: недостающие слоты - снятыми с линии, лишние стоящие - распускаются (с большего индекса), затем <c>Duty</c> по ползунку и
    /// расстановка на линии.
    /// </summary>
    public static void Resize(CityServices sv, int site)
    {
        CrewStore u = sv.Crews;
        int max = Utilities.UnitsMax(sv, site);
        sv.Sites.UnitsMax[site] = (byte)max;
        int have = CrewCount(u, site);
        for (int k = 0; k < u.Capacity && have < max; k++)
        {
            if (u.State[k] == CrewState.Free)
            {
                u.Reset(k);
                u.Site[k] = site;
                u.State[k] = CrewState.OffDuty;
                have++;
            }
        }

        for (int k = u.Capacity - 1; k >= 0 && have > max; k--)
        {
            if (u.Site[k] == site && u.State[k] is CrewState.Idle or CrewState.OffDuty)
            {
                u.Reset(k);
                have--;
            }
        }

        sv.Sites.Duty[site] = (byte)Math.Min(Utilities.DutyOf(sv, site), max);
        Utilities.Rebalance(sv, site);
    }

    /// <summary>Здание станции удаляется из пула (<see cref="Growth"/>): пристройки возвращаются в обычные участки.</summary>
    public static void OnBuildingRemoved(CityWorld c, int bi)
    {
        CityServices? sv = c.Services;
        if (sv == null || !sv.Table.Explicit || c.Types.Rows[c.Buildings.Type[bi]].Kind != LotZone.Service)
        {
            return;
        }

        int site = SiteOf(c, bi);
        if (site >= 0)
        {
            for (int k = 0; k < SiteStore.MaxAnnex; k++)
            {
                ReleaseAnnex(c, site, k);
            }
        }
    }

    private static void ReleaseAnnex(CityWorld c, int site, int k)
    {
        SiteStore s = c.Services!.Sites;
        int at = (site * SiteStore.MaxAnnex) + k;
        int lot = s.Annex[at];
        if (lot < 0)
        {
            return;
        }

        c.Lots.Flags[lot] &= unchecked((byte)~LotFlags.Annex);
        c.Lots.Version++;
        s.Annex[at] = -1;
    }

    // ------------------------------------------------------------------ геометрия пристроек

    /// <summary>Участки <paramref name="a"/> и <paramref name="b"/> на одной стороне одного сегмента (поколение сегмента совпадает).</summary>
    public static bool SameFrontage(LotStore l, int a, int b) => l.Seg[a] == l.Seg[b] && l.SegGen[a] == l.SegGen[b] && l.Side[a] == l.Side[b];

    /// <summary>Порядковый номер участка в ряду своей стороны сегмента: сколько участков (любых, в том числе Lost) того же фасада стоят по <c>S</c> раньше (ничья - по индексу).</summary>
    public static int Rank(LotStore l, int lot)
    {
        long mid = (long)l.S0Mm[lot] + l.S1Mm[lot];
        int r = 0;
        for (int x = 0; x < l.Count; x++)
        {
            if (x == lot || !SameFrontage(l, x, lot))
            {
                continue;
            }

            long m = (long)l.S0Mm[x] + l.S1Mm[x];
            r += m < mid || (m == mid && x < lot) ? 1 : 0;
        }

        return r;
    }

    /// <summary>Клеток по фасаду участка (legacy-участок без сетки - 1).</summary>
    public static int FrontCells(LotStore l, int lot) => l.Orient[lot] == LotOrient.Legacy ? 1 : l.CellsF[lot];

    /// <summary>Клеток в глубину.</summary>
    public static int DepthCells(LotStore l, int lot) => l.Orient[lot] == LotOrient.Legacy ? 1 : l.CellsD[lot];

    // ------------------------------------------------------------------ применение

    /// <summary>
    /// Повышение уровня на месте (после валидатора): занятые пристройки выкупаются со сносом (<see cref="CityLife.Demolish"/>), участки получают <see cref="LotFlags.Annex"/>, здание
    /// принимает тип следующего уровня и «новую стройку» (<c>Cond = Ceil = 100 %</c>), маска локальных апгрейдов нового уровня пуста, машины и ползунок пересчитываются.
    /// </summary>
    public static void Upgrade(World w, CityWorld c, int bi, in UpgradeServiceCmd p)
    {
        CityServices sv = c.Services!;
        int site = SiteOf(c, bi);
        for (int k = 0; k < 3; k++)
        {
            int lot = p.AnnexAt(k);
            if (lot < 0)
            {
                continue;
            }

            int old = c.Lots.Building[lot];
            if (old >= 0 && c.Buildings.IsAlive(old) && (c.Buildings.LifeFlags[old] & BuildingLifeFlags.Demolishing) == 0)
            {
                CityLife.Demolish(w, c, old);
            }

            c.Lots.Flags[lot] |= LotFlags.Annex;
            c.Lots.Version++;
            for (int m = 0; m < SiteStore.MaxAnnex; m++)
            {
                if (sv.Sites.Annex[(site * SiteStore.MaxAnnex) + m] < 0)
                {
                    sv.Sites.Annex[(site * SiteStore.MaxAnnex) + m] = lot;
                    break;
                }
            }
        }

        SetLevel(c, site, bi, sv.Sites.Level[site] + 1, fresh: true);
    }

    /// <summary>Понижение уровня (обратная к <see cref="Upgrade"/>, после валидатора): перечисленные пристройки освобождаются, здание получает тип предыдущего уровня.</summary>
    public static void Downgrade(CityWorld c, int bi, in UpgradeServiceCmd p)
    {
        CityServices sv = c.Services!;
        int site = SiteOf(c, bi);
        for (int k = 0; k < 3; k++)
        {
            int lot = p.AnnexAt(k);
            for (int m = 0; lot >= 0 && m < SiteStore.MaxAnnex; m++)
            {
                if (sv.Sites.Annex[(site * SiteStore.MaxAnnex) + m] == lot)
                {
                    ReleaseAnnex(c, site, m);
                }
            }
        }

        SetLevel(c, site, bi, sv.Sites.Level[site] - 1, fresh: false);
    }

    private static void SetLevel(CityWorld c, int site, int bi, int level, bool fresh)
    {
        CityServices sv = c.Services!;
        BuildingStore b = c.Buildings;
        int rec = sv.Sites.Svc[site];
        b.Type[bi] = sv.Table.LevelRow(rec, level).Type;
        sv.Sites.Level[site] = (byte)level;
        sv.Sites.UpgradeMask[site] = 0;
        if (fresh)
        {
            b.Cond[bi] = CityLife.Full;
            b.Ceil[bi] = CityLife.Full;
            b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.WornNotified);
        }

        b.Version++;
        Resize(sv, site);
    }

    /// <summary>Локальный апгрейд (после валидатора): бит маски станции, машины пересчитываются (эффект <c>units</c>).</summary>
    public static void SetUpgrade(CityWorld c, int bi, in SetServiceUpgradeCmd p)
    {
        CityServices sv = c.Services!;
        int site = SiteOf(c, bi);
        if (p.On != 0)
        {
            sv.Sites.UpgradeMask[site] |= (byte)(1 << p.Id);
        }
        else
        {
            sv.Sites.UpgradeMask[site] &= (byte)~(1 << p.Id);
        }

        c.Buildings.Version++;
        Resize(sv, site);
    }
}
