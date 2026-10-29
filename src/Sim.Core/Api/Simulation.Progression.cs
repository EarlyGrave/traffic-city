using System;
using Sim.Core.City;

namespace Sim.Core.Api;

// Прогрессия и показатели города (progression.md §4.5, ADR-0034): PG1 - поверхность и состояние, PG2 - показатели (кольцо часов, суточный баланс, дни до лимита).
// Проверки вех и замков - PG3, жалобы - PG4. Без режима жителей / без прогрессии запросы возвращают false/0. Стоимость вызова - O(24) (O(вех) у GetMilestone нет).
public sealed partial class Simulation
{
    /// <summary>Включена ли прогрессия (секция <c>progression</c> сценария).</summary>
    public bool HasProgression => !_disposed && _w.Progression != null;

    /// <summary>Сводка прогрессии; false - прогрессии нет. Ворота следующей вехи считаются по окну показателей (последние часы кольца + текущий).</summary>
    public bool GetProgression(out ProgressionInfo info)
    {
        info = default;
        ProgressionState? p = _disposed ? null : _w.Progression;
        CityWorld? c = _disposed ? null : _w.City;
        if (p == null || c == null)
        {
            return false;
        }

        info.Features = p.Features;
        info.Reached = p.Reached;
        info.MilestoneCount = p.Defs.Length;
        info.AreaStage = p.AreaStage;
        info.CreditDaysBonus = p.CreditDaysBonus;
        info.NextPopulation = -1;
        info.NextGateValue0 = -1;
        info.NextGateValue1 = -1;
        if (p.Reached < p.Defs.Length)
        {
            ref readonly MilestoneDesc m = ref p.Defs[p.Reached];
            info.NextPopulation = m.Population;
            KpiHour s = c.Kpi.SumWindow(in _w.Counters, true);
            bool enough = s.LegsAll >= p.MinLegs;
            info.NextGateMask = City.Progression.GateMiss(c.Kpi, in m.G0, in s, enough, out info.NextGateValue0) | (City.Progression.GateMiss(c.Kpi, in m.G1, in s, enough, out info.NextGateValue1) << 1);
        }

        return true;
    }

    /// <summary>Веха <paramref name="index"/>: описание и тик взятия (-1 - не взята); false - прогрессии нет или индекс вне диапазона.</summary>
    public bool GetMilestone(int index, out MilestoneInfo info)
    {
        info = default;
        ProgressionState? p = _disposed ? null : _w.Progression;
        if (p == null || (uint)index >= (uint)p.Defs.Length)
        {
            return false;
        }

        info.Desc = p.Defs[index];
        info.ReachedTick = p.ReachedTick[index];
        return true;
    }

    /// <summary>Профиль каталога открыт для постройки; без прогрессии - true для любого профиля каталога.</summary>
    public bool IsProfileUnlocked(int profileIndex)
    {
        if (_disposed)
        {
            return false;
        }

        ProgressionState? p = _w.Progression;
        if (p == null)
        {
            int n = _w.Net.Profiles?.Count ?? 0;
            return (uint)profileIndex < (uint)n;
        }

        return p.IsProfileUnlocked(profileIndex);
    }

    /// <summary>Признак открыт; без прогрессии - true.</summary>
    public bool IsFeatureUnlocked(Feature feature)
    {
        if (_disposed)
        {
            return false;
        }

        ProgressionState? p = _w.Progression;
        return p == null || p.IsFeatureUnlocked(feature);
    }

    /// <summary>
    /// Показатели города K1-K6 (progression.md §2.1) по окну «полные часы кольца + текущий неполный час»; false - города нет. Текст - 4 Гц, O(24).
    /// </summary>
    public bool GetCityKpi(out CityKpiInfo info)
    {
        info = default;
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return false;
        }

        CityKpi k = c.Kpi;
        KpiHour s = k.SumWindow(in _w.Counters, true);
        info.HoursFilled = k.HoursFilled;
        info.CurrentHourTicks = k.CurrentHourTicks(_w.Tick);
        info.LegsAll = s.LegsAll;
        info.LegsCar = s.LegsCar;
        info.LegsBus = s.LegsBus;
        info.LegsWalk = s.LegsWalk;
        info.LegsWork = s.LegsWork;
        info.TravelMinX10 = k.Value(KpiKind.TravelMin, in s);
        info.TravelCarMinX10 = k.MinX10(s.DurCarDs, s.LegsCar);
        info.TravelBusMinX10 = k.MinX10(s.DurBusDs, s.LegsBus);
        info.TravelWalkMinX10 = k.MinX10(s.DurWalkDs, s.LegsWalk);
        info.WorkMinX10 = k.Value(KpiKind.WorkMin, in s);
        info.BusSharePermille = k.Value(KpiKind.BusSharePct, in s);
        info.WalkSharePermille = s.LegsAll > 0 ? (int)((long)s.LegsWalk * 1000 / s.LegsAll) : -1;
        info.IdlePermille = k.Value(KpiKind.IdlePct, in s);
        info.SatisfactionPermille = k.Value(KpiKind.SatisfactionPct, in s);
        info.IncLongWait = s.IncLongWait;
        info.IncNoParking = s.IncNoParking;
        info.IncLongCommute = s.IncLongCommute;
        info.IncTourCancelled = s.IncTourCancelled;
        info.IncLongStopWait = s.IncLongStopWait;
        info.StuckNow = _w.Counters.StuckVehicles;
        info.Gridlock = _w.Counters.GridlockFlag;
        info.Balance = c.Ledger.City;
        info.HasBudget = c.Budget != null ? 1 : 0;
        info.TrendPerDay = c.Budget != null ? c.Budget.IncomeEwma - c.Budget.ExpenseEwma : 0;
        info.DaysToLimit = CityKpi.DaysToLimit(c, City.Progression.CreditDaysBonus(_w));
        info.Population = c.Residents.AliveCount;
        info.DaysFilled = k.DaysFilled;
        return true;
    }

    /// <summary>Полные часы кольца от старого к новому (до 24; текущий неполный час не входит); возвращает число записанных. Графики - 1 Гц.</summary>
    public int CopyKpiHours(Span<KpiHour> dst)
    {
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return 0;
        }

        CityKpi k = c.Kpi;
        int n = Math.Min(dst.Length, k.HoursFilled);
        int first = (k.Head - k.HoursFilled + CityKpi.Hours) % CityKpi.Hours;   // самый старый из заполненных
        int skip = k.HoursFilled - n;   // буфер короче окна: последние n часов
        for (int i = 0; i < n; i++)
        {
            dst[i] = k.Ring[(first + skip + i) % CityKpi.Hours];
        }

        return n;
    }

    /// <summary>Текущий неполный час показателей (дельты движения - по живым счётчикам); false - города нет.</summary>
    public bool GetKpiCurrentHour(out KpiHour hour)
    {
        hour = default;
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return false;
        }

        hour = c.Kpi.Cur[0];
        hour.VehTicks = _w.Counters.VehicleTicksTotal - c.Kpi.VehAtHourStart;
        hour.MovingTicks = _w.Counters.MovingVehicleTicksTotal - c.Kpi.MovingAtHourStart;
        return true;
    }

    /// <summary>Баланс казны на конце суток от старых к новым (до 32); возвращает число записанных.</summary>
    public int CopyKpiDays(Span<long> dst)
    {
        CityWorld? c = _disposed ? null : _w.City;
        if (c == null)
        {
            return 0;
        }

        CityKpi k = c.Kpi;
        int n = Math.Min(dst.Length, k.DaysFilled);
        int first = (k.DayHead - k.DaysFilled + CityKpi.Days) % CityKpi.Days;
        int skip = k.DaysFilled - n;
        for (int i = 0; i < n; i++)
        {
            dst[i] = k.DayBalance[(first + skip + i) % CityKpi.Days];
        }

        return n;
    }

    /// <summary>
    /// Жалобы жителей с порядковым номером больше <paramref name="sinceSeq"/> от старых к новым (PG4, progression.md §6.2): кольцо последних 64; возвращает число записанных.
    /// Номер новой жалобы - <c>SimEvent.B</c> события <see cref="SimEventKind.Complaint"/>. Без режима жителей - 0.
    /// </summary>
    public int CopyComplaints(Span<ComplaintInfo> dst, int sinceSeq)
    {
        CityWorld? c = _disposed ? null : _w.City;
        return c == null ? 0 : c.Kpi.Complaints.Copy(dst, sinceSeq);
    }
}
