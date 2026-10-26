using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Demand;

/// <summary>
/// Доступность (zoning-demand.md §6.7): <c>LsC[c, m]</c> - logsum стоимости до C-целей из ячейки <c>c</c> способом <c>m</c> с весом мест (<c>ln(ShopCap) / θ_dest</c>), <c>LsJobs[c]</c> - то же до рабочих
/// мест по всем способам (вложенный logsum с <c>θ_mode</c>). Стоимость пары - среднее по периодам суток ненулевых значений <c>Skims.Get</c> (скимы вместе с априорной оценкой) плюс <c>ASC_m</c>.
/// Дневной проход порциями по <c>CursorAccess</c> (<see cref="Step"/>); на создании города - целиком (<see cref="Init"/>), там же задаётся <c>LS_ref</c> (среднее по ячейкам с жильём).
/// Поле - состояние (блок <c>SKIM</c>), поэтому продолжение после <c>Load</c> совпадает с непрерывным прогоном. Два исключения от спеки: веса мест - ёмкость типа
/// (<c>ShopCapacity</c> и <c>Capacity</c> активных зданий, а не вакансии: вакансии меняются каждый день и не входят в кэш зданий), нет доступа - <see cref="Skims.NoLs"/>.
/// </summary>
internal static class Accessibility
{
    /// <summary>Создание города: производные, полный проход, <c>LS_ref</c>.</summary>
    public static void Init(World w)
    {
        w.City!.Skims.Prepare(w);
        RunAll(w);
    }

    /// <summary>Полный пересчёт поля (все ячейки-источники), курсор в начало.</summary>
    public static void RunAll(World w)
    {
        CityWorld c = w.City!;
        Skims s = c.Skims;
        s.Prepare(w);
        for (int o = 0; o < s.Cells; o++)
        {
            ComputeCell(c, s, o);
        }

        c.CursorAccess = 0;
        FinishPass(c, s);
    }

    /// <summary>Дневной проход: следующие <paramref name="cells"/> ячеек-источников от курсора. true - проход завершён (курсор вернулся в 0).</summary>
    public static bool Step(World w, int cells)
    {
        CityWorld c = w.City!;
        Skims s = c.Skims;
        s.Prepare(w);
        for (int k = 0; k < cells; k++)
        {
            ComputeCell(c, s, c.CursorAccess);
            c.CursorAccess++;
            if (c.CursorAccess >= s.Cells)
            {
                c.CursorAccess = 0;
                FinishPass(c, s);
                return true;
            }
        }

        return false;
    }

    /// <summary>Logsum по способам, доступным жителю (машина - при <paramref name="hasCar"/>), дс; <see cref="Skims.NoLs"/>, если целей нет.</summary>
    public static int Combined(Skims s, int cell, bool hasCar, int thetaMode64)
    {
        Span<int> costs = stackalloc int[Skims.Modes];
        for (int m = 0; m < Skims.Modes; m++)
        {
            int v = s.LsC[(cell * Skims.Modes) + m];
            costs[m] = v == Skims.NoLs || (m == (int)TravelMode.Car && !hasCar) ? DetLogit.NoCost : v;
        }

        int r = DetLogit.LogSum(costs, thetaMode64);
        return r == DetLogit.NoCost ? Skims.NoLs : r;
    }

    /// <summary>Среднее по периодам ненулевых значений скима пары и способа, дс; 0 - способ недоступен.</summary>
    internal static int AvgCost(Skims s, int o, int d, TravelMode m)
    {
        if (m == TravelMode.Walk && !s.WalkReachable(o, d))
        {
            return 0;
        }

        long sum = 0;
        int n = 0;
        for (int p = 0; p < s.Periods; p++)
        {
            int v = s.Get(o, d, m, p);
            if (v > 0)
            {
                sum += v;
                n++;
            }
        }

        return n == 0 ? 0 : (int)(sum / n);
    }

    private static int AscOf(in CityChoiceDesc ch, int m) => m == (int)TravelMode.Car ? ch.AscCarDs : (m == (int)TravelMode.Bus ? ch.AscBusDs : ch.AscWalkDs);

    private static void ComputeCell(CityWorld c, Skims s, int o)
    {
        CityChoiceDesc ch = c.Cfg.Choice;
        int nc = s.Cells;
        int[] scratch = s.ScratchInt!;
        Span<int> costs = scratch.AsSpan(0, nc);
        Span<int> avg = scratch.AsSpan(nc, nc);
        Span<int> jobLs = stackalloc int[Skims.Modes];
        int[] shop = s.CellShopCap!;
        int[] job = s.CellJobCap!;
        for (int m = 0; m < Skims.Modes; m++)
        {
            for (int d = 0; d < nc; d++)
            {
                avg[d] = shop[d] > 0 || job[d] > 0 ? AvgCost(s, o, d, (TravelMode)m) : 0;
            }

            int asc = AscOf(in ch, m);
            s.LsC[(o * Skims.Modes) + m] = LogSumOver(costs, avg, shop, asc, ch.ThetaDest64);
            jobLs[m] = LogSumOver(costs, avg, job, asc, ch.ThetaDest64);
        }

        for (int m = 0; m < Skims.Modes; m++)
        {
            if (jobLs[m] == Skims.NoLs)
            {
                jobLs[m] = DetLogit.NoCost;
            }
        }

        int r = DetLogit.LogSum(jobLs, ch.ThetaMode64);
        s.LsJobs[o] = r == DetLogit.NoCost ? Skims.NoLs : r;
    }

    /// <summary>Logsum по ячейкам-целям с весом мест: стоимость цели <c>avg + asc − ln(cap)/θ</c>; <see cref="Skims.NoLs"/>, если целей нет.</summary>
    private static int LogSumOver(Span<int> costs, ReadOnlySpan<int> avg, int[] cap, int asc, int theta64)
    {
        int n = 0;
        for (int d = 0; d < avg.Length; d++)
        {
            if (avg[d] <= 0 || cap[d] <= 0)
            {
                continue;
            }

            costs[n++] = avg[d] + asc - DetLogit.LnUnitsToDs(DetLogit.Ln64(cap[d]), theta64);
        }

        return n == 0 ? Skims.NoLs : DetLogit.LogSum(costs[..n], theta64);
    }

    /// <summary>Конец прохода: если <c>LS_ref</c> ещё не задан - среднее комбинированного logsum (все способы) по ячейкам с жильём.</summary>
    private static void FinishPass(CityWorld c, Skims s)
    {
        if (s.LsRefSet)
        {
            return;
        }

        long sum = 0;
        int n = 0;
        for (int cell = 0; cell < s.Cells; cell++)
        {
            if (s.CellResCount![cell] == 0)
            {
                continue;
            }

            int v = Combined(s, cell, true, c.Cfg.Choice.ThetaMode64);
            if (v != Skims.NoLs)
            {
                sum += v;
                n++;
            }
        }

        if (n > 0)
        {
            s.LsRef = (int)(sum / n);
            s.LsRefSet = true;
        }
    }
}
