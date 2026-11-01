using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Правила прогрессии (progression.md §3.3, §4.3, ADR-0034; задача PG3): замки команд (одна чистая функция на превью и применение), открытые области карты и проверка вех в фазе 9r.
/// Без прогрессии (<c>World.Progression == null</c>) каждая функция выходит первой строкой - поведение, хэш и сейв побитово прежние. Без PRNG и аллокаций, однопоточно.
/// </summary>
internal static class Progression
{
    /// <summary>
    /// Замок команды (§4.3): признак по виду команды плюс профиль дороги. <see cref="RejectReason.Locked"/> - закрыто, <paramref name="milestone"/> - индекс вехи, которая откроет (-1 - не откроется).
    /// Команда с битом <see cref="CommandFlags.Inverse"/> (undo) замок не проверяет. Зовётся после структурных проверок: handle'ы целей уже проверены.
    /// </summary>
    public static RejectReason Check(World w, in Command cmd, out int milestone)
    {
        milestone = -1;
        ProgressionState? p = w.Progression;
        if (p == null || (cmd.Flags & CommandFlags.Inverse) != 0)
        {
            return RejectReason.None;
        }

        int feature = -1;
        int profile = -1;
        int svcMilestone = 0;
        NetworkStore n = w.Net;
        switch (cmd.Kind)
        {
            case CommandKind.BuildRoad:
                if ((cmd.BuildRoad.Flags & BuildRoadFlags.Curve) != 0)
                {
                    feature = (int)Feature.Curves;
                }

                profile = cmd.BuildRoad.Profile - 1;
                break;
            case CommandKind.UpgradeRoad:
                {
                    UpgradeRoadCmd u = cmd.UpgradeRoad;
                    if ((uint)u.Seg.Index >= (uint)n.SegmentCount)
                    {
                        break;
                    }

                    ref readonly SegmentData sd = ref n.Segments[u.Seg.Index];
                    bool mirrored = (u.Flags & UpgradeRoadFlags.Mirrored) != 0;
                    bool curMirrored = (sd.Flags & SegmentFlags.Mirrored) != 0;
                    bool newProfile = u.Profile != 0 && (u.Profile - 1 != sd.Profile || mirrored != curMirrored);
                    bool layout = newProfile || ((u.Flags & UpgradeRoadFlags.NoProfile) != 0 && sd.Profile >= 0) ||
                        (u.LanesAB != 255 && u.LanesAB != sd.LanesAB) || (u.LanesBA != 255 && u.LanesBA != sd.LanesBA) ||
                        ((u.Flags & UpgradeRoadFlags.KeepKind) == 0 && u.Kind != sd.Kind);
                    if (layout)
                    {
                        feature = (int)Feature.ArterialUpgrade;   // смена только лимита скорости открыта
                    }

                    if (newProfile)
                    {
                        profile = u.Profile - 1;
                    }

                    break;
                }

            case CommandKind.SetNodeControl:
                if (cmd.SetNodeControl.Control == NodeControl.Signal)
                {
                    feature = (int)Feature.SignalsFixed;
                }

                break;
            case CommandKind.SetSignalPlan:
                feature = cmd.SetSignalPlan.Mode == SignalMode.Adaptive ? (int)Feature.SignalsAdaptive : (int)Feature.SignalsFixed;
                break;
            case CommandKind.SetTurnBan:
            case CommandKind.SetArmControl:
                feature = (int)Feature.TurnRules;
                break;
            case CommandKind.CreateBusRoute:
            case CommandKind.InsertBusStop:
            case CommandKind.AppendBusStop:
            case CommandKind.RemoveBusStop:
            case CommandKind.RemoveBusRoute:
            case CommandKind.SetBusRouteParams:
                feature = (int)Feature.BusLines;
                break;
            case CommandKind.SetLaneFlags:
                if ((cmd.SetLaneFlags.Mask & LaneFlags.BusOnly) != 0)
                {
                    feature = (int)Feature.BusLanes;
                }

                break;
            case CommandKind.PaintZone:
                feature = (int)Feature.Zoning;
                break;
            case CommandKind.SetStreetAccess:
                break;   // запрет улицы открыт с начала игры (vehicle-classes.md §9.3), пока FR не решит, с какой вехи появляются фуры
            case CommandKind.SetBudgetPolicy:
                {
                    CityBudget? b = w.City?.Budget;
                    BudgetPolicy np = cmd.SetBudgetPolicy.Policy;
                    if (b != null && (np.TaxRBp != b.Policy.TaxRBp || np.TaxCBp != b.Policy.TaxCBp || np.TaxIBp != b.Policy.TaxIBp ||
                        np.LandTaxBpDay != b.Policy.LandTaxBpDay || np.Fare != b.Policy.Fare))
                    {
                        feature = (int)Feature.Taxes;
                    }

                    break;
                }

            case CommandKind.SetServiceBudget:
                feature = (int)Feature.Taxes;   // ползунок бюджета служб - вместе с налогами (services.md §6)
                break;
            case CommandKind.BuildService:
            case CommandKind.UpgradeService:
            case CommandKind.SetServiceUpgrade:
                svcMilestone = ServiceMilestone(w, in cmd);   // уровни и апгрейды служб открывает веха записи (services.md §6): поле milestone, не Feature
                break;
        }

        if (svcMilestone > p.Reached)
        {
            milestone = svcMilestone - 1 < p.Defs.Length ? svcMilestone - 1 : -1;   // веха Mk = Defs[k - 1]
            return RejectReason.Locked;
        }

        if (feature >= 0 && !p.IsFeatureUnlocked((Feature)feature))
        {
            milestone = p.MilestoneForFeature((Feature)feature);
            return RejectReason.Locked;
        }

        if (profile >= 0 && profile < p.ProfileCount && !p.IsProfileUnlocked(profile))
        {
            milestone = p.MilestoneForProfile(profile);
            return RejectReason.Locked;
        }

        return RejectReason.None;
    }

    /// <summary>
    /// Веха Mk (k &gt;= 1; 0 - с начала игры), которая открывает уровень или апгрейд службы команды <paramref name="cmd"/> (<c>milestone</c> уровня / апгрейда записи, services.md §6). Ссылки уже проверены
    /// структурно; негодные (нет режима, нет станции) дают 0 - отказ даст валидатор.
    /// </summary>
    private static int ServiceMilestone(World w, in Command cmd)
    {
        CityWorld? city = w.City;
        CityServices? sv = city?.Services;
        if (city == null || sv == null || !sv.Table.Explicit)
        {
            return 0;
        }

        ServiceTable tb = sv.Table;
        if (cmd.Kind == CommandKind.BuildService)
        {
            int type = cmd.BuildService.Type;
            return (uint)type < (uint)city.Types.Count && tb.RecOf(type) >= 0 ? tb.LevelRow(tb.RecOf(type), tb.LevelOf(type)).Milestone : 0;
        }

        EntityRef h = cmd.Kind == CommandKind.UpgradeService ? cmd.UpgradeService.Building : cmd.SetServiceUpgrade.Building;
        int site = h.Index >= 0 && h.Index < city.Buildings.Capacity ? ServiceLevels.SiteOf(city, h.Index) : -1;
        if (site < 0)
        {
            return 0;
        }

        int rec = sv.Sites.Svc[site];
        int level = sv.Sites.Level[site];
        if (cmd.Kind == CommandKind.UpgradeService)
        {
            return level < tb.Records[rec].LevelCount ? tb.LevelRow(rec, level + 1).Milestone : 0;
        }

        ref readonly ServiceLevelDesc l = ref tb.LevelRow(rec, level);
        int id = cmd.SetServiceUpgrade.Id;
        return cmd.SetServiceUpgrade.On != 0 && (uint)id < (uint)l.UpgradeCount ? tb.Upgrades[l.FirstUpgrade + id].Milestone : 0;
    }

    /// <summary>Точка в объединении открытых областей карты (0..<c>AreaStage</c>); без прогрессии или без областей - true.</summary>
    public static bool InOpenArea(World w, Vec2 pt)
    {
        ProgressionState? p = w.Progression;
        if (p == null || p.Areas.Length == 0)
        {
            return true;
        }

        int last = p.AreaStage < p.Areas.Length ? p.AreaStage : p.Areas.Length - 1;
        for (int i = 0; i <= last; i++)
        {
            ref readonly AreaRect a = ref p.Areas[i];
            if (pt.X >= a.MinX && pt.X <= a.MaxX && pt.Z >= a.MinZ && pt.Z <= a.MaxZ)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ворота не выполнены (1) или выполнены / ворот нет (0); <paramref name="value"/> - текущее значение показателя (-1 - нет данных или ворот).</summary>
    public static int GateMiss(CityKpi k, in KpiGate g, in KpiHour s, bool enough, out int value)
    {
        if (g.Kpi == KpiKind.None)
        {
            value = -1;
            return 0;
        }

        value = k.Value(g.Kpi, in s);
        bool ok = enough && value >= 0 && (g.IsMin != 0 ? value >= g.Value : value <= g.Value);
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Проверка следующей вехи (§3.3): фаза 9r на сдвиге часа KPI (после сдвига кольца) и на границе суток (после казны). Население есть и ворота выполнены - веха взята:
    /// признаки, профили, бонус кредита, область, грант казне (<see cref="FlowKind.Subsidy"/>, только при <c>city_budget</c>), событие <see cref="SimEventKind.MilestoneReached"/>.
    /// Население есть, ворот нет - один раз на веху <see cref="SimEventKind.MilestoneGated"/>. Проверяется только следующая веха: не больше одной за вызов.
    /// </summary>
    public static void CheckMilestone(World w, CityWorld c)
    {
        ProgressionState? p = w.Progression;
        if (p == null || p.Reached >= p.Defs.Length)
        {
            return;
        }

        int i = p.Reached;
        ref readonly MilestoneDesc m = ref p.Defs[i];
        int pop = c.Residents.AliveCount;
        if (pop < m.Population)
        {
            return;
        }

        int miss = 0;
        bool pass = true;
        if (m.G0.Kpi != KpiKind.None)
        {
            KpiHour s = c.Kpi.SumWindow(in w.Counters, true);
            bool enough = s.LegsAll >= p.MinLegs;
            miss = GateMiss(c.Kpi, in m.G0, in s, enough, out _) | (GateMiss(c.Kpi, in m.G1, in s, enough, out _) << 1);
            bool any = (m.Flags & MilestoneFlags.GatesAny) != 0 && m.G1.Kpi != KpiKind.None;
            pass = any ? miss != 3 : miss == 0;
        }

        if (!pass)
        {
            ulong bit = 1UL << i;
            if ((p.GatedNotified & bit) == 0)
            {
                p.GatedNotified |= bit;
                w.Emit(SimEventKind.MilestoneGated, 0, i, miss);
            }

            return;
        }

        p.Reached = i + 1;
        p.ReachedTick[i] = w.Tick;
        p.Features |= m.Features;
        for (int k = 0; k < m.ProfileCount; k++)
        {
            int prof = p.ProfileIdx[m.ProfileFirst + k];
            p.Profiles[prof >> 6] |= 1UL << (prof & 63);
        }

        p.CreditDaysBonus += m.CreditDays;
        if (m.Area > p.AreaStage)
        {
            p.AreaStage = m.Area;
        }

        if (c.Budget != null && m.Grant > 0)
        {
            c.Ledger.Transfer(c.Ledger.ExternalAccount, c.Ledger.CityAccount, m.Grant, FlowKind.Subsidy);
        }

        w.Emit(SimEventKind.MilestoneReached, 0, i, pop);
    }

    /// <summary>Бонус кредитных дней от вех (0 без прогрессии): <c>CreditLimit = (credit_days + бонус) · IncomeEwma</c>.</summary>
    public static int CreditDaysBonus(World w) => w.Progression?.CreditDaysBonus ?? 0;
}
