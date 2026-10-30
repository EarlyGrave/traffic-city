using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Save;

// Проверки прогрессии и показателей после загрузки (progression.md §4.4): определения - тем же кодом, что Create; открытое согласовано со взятыми вехами; кольца - в диапазонах, суммы >= 0.
internal static partial class SaveValidator
{
    /// <summary>Блок CKPI: головы и заполненность колец в диапазонах, все суммы часов неотрицательны, движущихся машино-тиков не больше всех.</summary>
    public static LoadStatus ValidateKpi(LoadContext c)
    {
        CityKpi k = c.W.City!.Kpi;
        if ((uint)k.Head >= CityKpi.Hours || (uint)k.HoursFilled > CityKpi.Hours || (uint)k.DayHead >= CityKpi.Days || (uint)k.DaysFilled > CityKpi.Days ||
            k.VehAtHourStart < 0 || k.MovingAtHourStart < 0 || k.MovingAtHourStart > k.VehAtHourStart ||
            k.VehAtHourStart > c.W.Counters.VehicleTicksTotal || k.MovingAtHourStart > c.W.Counters.MovingVehicleTicksTotal)
        {
            return Bad(c, 0, 1);
        }

        for (int i = 0; i < CityKpi.Hours; i++)
        {
            if (!HourOk(in k.Ring[i]))
            {
                return Bad(c, i, 8);
            }
        }

        if (!HourOk(in k.Cur[0]) || k.Cur[0].VehTicks != 0 || k.Cur[0].MovingTicks != 0)
        {
            return Bad(c, 0, 23);   // дельты движения текущего часа пишутся только на сдвиге
        }

        return LoadStatus.Ok;
    }

    private static bool HourOk(in KpiHour h) =>
        h.LegsCar >= 0 && h.LegsBus >= 0 && h.LegsWalk >= 0 && h.LegsWork >= 0 && h.LegsWork <= h.LegsAll &&
        h.DurCarDs >= 0 && h.DurBusDs >= 0 && h.DurWalkDs >= 0 && h.DurWorkDs >= 0 &&
        h.VehTicks >= 0 && h.MovingTicks >= 0 && h.MovingTicks <= h.VehTicks &&
        h.IncLongWait >= 0 && h.IncNoParking >= 0 && h.IncLongCommute >= 0 && h.IncTourCancelled >= 0 && h.IncLongStopWait >= 0 && h.IncBuildingWorn >= 0;

    /// <summary>
    /// Блок PROG: определения проходят <see cref="ProgressionState.Validate"/>; <c>Reached &lt;= Defs</c>; тик взятия есть ровно у взятых и не больше текущего; открытые признаки - подмножество всех,
    /// содержат открытые на старте и маски взятых; профили взятых открыты, хвост битсета за каталогом пуст; бонус кредита и стадия области равны выводу из взятых вех.
    /// </summary>
    public static LoadStatus ValidateProgression(LoadContext c)
    {
        ProgressionState p = c.W.Progression!;
        if (!ProgressionState.Validate(p.Defs, p.ProfileIdx, p.Areas, p.MinLegs, p.LockedFeatures, p.ProfileCount))
        {
            return Bad(c, 0, 7);
        }

        if ((uint)p.Reached > (uint)p.Defs.Length || (p.Features & ~ProgressionState.AllFeatures) != 0 || (p.GatedNotified >> p.Defs.Length) != 0)
        {
            return Bad(c, 0, 27);
        }

        ulong opened = ProgressionState.AllFeatures & ~p.LockedFeatures;
        int credit = 0;
        int area = 0;
        for (int i = 0; i < p.Defs.Length; i++)
        {
            bool reached = i < p.Reached;
            long t = p.ReachedTick[i];
            if (reached ? t < 0 || t > c.W.Tick : t != -1)
            {
                return Bad(c, i, 21);
            }

            if (!reached)
            {
                continue;
            }

            ref readonly MilestoneDesc m = ref p.Defs[i];
            opened |= m.Features;
            credit += m.CreditDays;
            if (m.Area > area)
            {
                area = m.Area;
            }

            for (int k = 0; k < m.ProfileCount; k++)
            {
                if (!p.IsProfileUnlocked(p.ProfileIdx[m.ProfileFirst + k]))
                {
                    return Bad(c, i, 29);
                }
            }
        }

        if ((p.Features & opened) != opened || credit != p.CreditDaysBonus || area != p.AreaStage)
        {
            return Bad(c, 0, 28);
        }

        for (int b = p.ProfileCount; b < p.Profiles.Length * 64; b++)
        {
            if ((p.Profiles[b >> 6] & (1UL << (b & 63))) != 0)
            {
                return Bad(c, b, 29);
            }
        }

        return LoadStatus.Ok;
    }
}
