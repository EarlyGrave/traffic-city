using System;
using Sim.Core.Api;
using Sim.Core.Metrics;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Показатели города (progression.md §2.3, ADR-0034; задача PG2): кольцо 24 игровых часов, текущий неполный час, отметки счётчиков движения на начале часа и суточное кольцо
/// баланса казны (32 значения). Существует только в режиме жителей (<see cref="CityWorld.Kpi"/>). Все поля целые; пишется только в однопоточных фазах (2r, 9, 9r, остановки)
/// в фиксированном порядке. В сейве - всегда при жителях (блок <c>CKPI</c>), в хэше - только при прогрессии (§2.4): без неё кольцо - чистый наблюдатель.
/// </summary>
internal sealed class CityKpi
{
    public const int Hours = 24;
    public const int Days = 32;

    /// <summary>[Старт] порог «долгой дороги на работу», игровые минуты (progression.md §6.1, вид 3).</summary>
    public const int LongCommuteGameMin = 60;

    /// <summary>[Старт] множитель свободного потока (скима) для «долгой дороги на работу».</summary>
    public const int LongCommuteSkimRatio = 2;

    /// <summary>[Старт] порог «долгого ожидания автобуса», игровые минуты (вид 5).</summary>
    public const int LongStopWaitGameMin = 20;

    /// <summary>Полные часы в физическом порядке; следующий записывается в <see cref="Head"/> (при заполненном кольце это самый старый).</summary>
    public readonly KpiHour[] Ring = new KpiHour[Hours];

    /// <summary>Текущий неполный час (массив из одного элемента: хуки берут <c>ref Cur[0]</c>, сейв - столбцами полей). <c>VehTicks/MovingTicks</c> заполняются только на сдвиге.</summary>
    public readonly KpiHour[] Cur = new KpiHour[1];

    public int Head;
    public int HoursFilled;
    public long VehAtHourStart;
    public long MovingAtHourStart;

    /// <summary>Баланс казны (City) на конце суток, физический порядок; следующий - в <see cref="DayHead"/>.</summary>
    public readonly long[] DayBalance = new long[Days];
    public int DayHead;
    public int DaysFilled;

    /// <summary>Жалобы жителей (PG4, progression.md §6.2): накопитель часа и кольцо выпуска. Вне хэша и сейва.</summary>
    public readonly Complaints Complaints;

    // производные календаря (не сохраняются): пороги случаев в тиках/дс
    private readonly int _dayTicks;
    private readonly int _longCommuteDs;
    private readonly long _longStopWaitTicks;

    public CityKpi(int dayTicks)
    {
        _dayTicks = Math.Max(1, dayTicks);
        _longCommuteDs = (int)((long)_dayTicks * LongCommuteGameMin / (24 * 60 * 2));   // игровые минуты -> тики (сутки = 1440 мин) -> дс (2 тика)
        _longStopWaitTicks = (long)_dayTicks * LongStopWaitGameMin / (24 * 60);
        Complaints = new Complaints(_dayTicks);
    }

    /// <summary>Игровой час суток тика (0..23); границы часов совпадают с границами суток при любом <c>DayTicks</c>.</summary>
    public static int HourOf(long tick, int dayTicks) => (int)((tick % dayTicks) * Hours / dayTicks);

    /// <summary>Тик <paramref name="tick"/> - последний в своём игровом часе (на нём фаза 9r сдвигает кольцо).</summary>
    public static bool IsHourEnd(long tick, int dayTicks) => HourOf(tick + 1, dayTicks) != HourOf(tick, dayTicks) || (tick + 1) % dayTicks == 0;

    /// <summary>Ближайший тик сдвига часа, не раньше <paramref name="tick"/> (для прыжков агрегата: такой тик исполняется обычным путём).</summary>
    public static long NextHourEnd(long tick, int dayTicks)
    {
        long dayStart = tick - (tick % dayTicks);
        int h = HourOf(tick, dayTicks);
        long nextOffset = (((long)(h + 1) * dayTicks) + Hours - 1) / Hours;   // наименьшее o: o * 24 / D >= h + 1
        return dayStart + nextOffset - 1;
    }

    // ------------------------------------------------------------------ хуки (однопоточные фазы)

    /// <summary>Закрытая нога жителя (<c>TripLegs.CompleteLeg</c>): длительность от заявки до прибытия, дс; <paramref name="toWork"/> - нога к работе.</summary>
    public void OnLeg(TravelMode mode, int durDs, bool toWork, int skimDs, int home = -1, int homeGen = 0)
    {
        ref KpiHour h = ref Cur[0];
        switch (mode)
        {
            case TravelMode.Car:
                h.LegsCar++;
                h.DurCarDs += durDs;
                break;
            case TravelMode.Bus:
                h.LegsBus++;
                h.DurBusDs += durDs;
                break;
            default:
                h.LegsWalk++;
                h.DurWalkDs += durDs;
                break;
        }

        if (toWork)
        {
            h.LegsWork++;
            h.DurWorkDs += durDs;
            long limit = Math.Max(_longCommuteDs, (long)LongCommuteSkimRatio * Math.Max(0, skimDs));
            if (durDs >= limit)
            {
                h.IncLongCommute++;
                Complaints.Add(ComplaintKind.LongCommute, home, homeGen);   // жалоба - у дома жителя
            }
        }
    }

    /// <summary>Посадка после ожидания <paramref name="waitTicks"/> на остановке (только агенты-пешеходы).</summary>
    public void OnBoarded(long waitTicks, int stop = -1, int stopGen = 0)
    {
        if (waitTicks >= _longStopWaitTicks)
        {
            Cur[0].IncLongStopWait++;
            Complaints.Add(ComplaintKind.LongStopWait, stop, stopGen);   // жалоба - у остановки посадки
        }
    }

    /// <summary>Тур снят (дедлайн выезда, сорванный тур клапана): случай <c>TourCancelled</c>, жалоба - у дома жителя <paramref name="r"/>.</summary>
    public void OnTourCancelled(CityWorld c, int r)
    {
        Cur[0].IncTourCancelled++;
        int home = c.Residents.Home[r];
        Complaints.Add(ComplaintKind.TourCancelled, home, home >= 0 ? c.Buildings.Pool.GenerationOf(home) : 0);
    }

    /// <summary>Первый отказ места на ногу: случай <c>NoParking</c>, жалоба - у здания-цели.</summary>
    public void OnNoParking(CityWorld c, int building)
    {
        Cur[0].IncNoParking++;
        Complaints.Add(ComplaintKind.NoParking, building, building >= 0 ? c.Buildings.Pool.GenerationOf(building) : 0);
    }

    /// <summary>Эпизод стояния машины >= 60 с (<c>QualityMetrics.Sample</c>): случай <c>LongWait</c>, жалоба - у сегмента полосы машины (-1 - не на сети).</summary>
    public void OnLongWait(int segment, int segmentGen)
    {
        Cur[0].IncLongWait++;
        Complaints.Add(ComplaintKind.LongWait, segment, segmentGen);
    }

    /// <summary>Суточные случаи «дом ветшает» (building-lifecycle.md §2.2): <paramref name="incidents"/> в K4 и столько же случаев жалобы у здания.</summary>
    public void OnBuildingWorn(int building, int gen, int incidents)
    {
        Cur[0].IncBuildingWorn += incidents;
        Complaints.Add(ComplaintKind.BuildingWorn, building, gen, incidents);
    }

    /// <summary>Вред заброшки H3 (building-lifecycle.md §2.4): случаи K4 у жителей клетки и жалоба <c>Blight</c> у дома.</summary>
    public void OnBlight(int building, int gen, int incidents)
    {
        Cur[0].IncBuildingWorn += incidents;
        Complaints.Add(ComplaintKind.Blight, building, gen, incidents);
    }

    /// <summary>Снос значимого здания (§2.5): разовые случаи K4 жителей в радиусе и жалоба <c>HeritageLost</c> у участка.</summary>
    public void OnHeritageLost(int building, int gen, int incidents)
    {
        Cur[0].IncBuildingWorn += incidents;
        Complaints.Add(ComplaintKind.HeritageLost, building, gen, incidents);
    }

    // ------------------------------------------------------------------ фаза 9r

    /// <summary>Фаза 9r, каждый тик: на последнем тике игрового часа час уходит в кольцо (дельты счётчиков движения - здесь).</summary>
    public void OnTickEnd(World w)
    {
        if (!IsHourEnd(w.Tick, _dayTicks))
        {
            return;
        }

        ref KpiHour cur = ref Cur[0];
        cur.VehTicks = w.Counters.VehicleTicksTotal - VehAtHourStart;
        cur.MovingTicks = w.Counters.MovingVehicleTicksTotal - MovingAtHourStart;
        Ring[Head] = cur;
        Head = Head + 1 == Hours ? 0 : Head + 1;
        if (HoursFilled < Hours)
        {
            HoursFilled++;
        }

        VehAtHourStart = w.Counters.VehicleTicksTotal;
        MovingAtHourStart = w.Counters.MovingVehicleTicksTotal;
        cur = default;
        Progression.CheckMilestone(w, w.City!);   // вехи (PG3, progression.md §3.3): после сдвига кольца; без прогрессии - выход первой строкой
        Complaints.EmitHour(w, w.City!);   // жалобы часа (PG4, §6.2)
    }

    /// <summary>Граница суток (после казны): баланс в суточное кольцо.</summary>
    public void OnDayEnd(long balance)
    {
        DayBalance[DayHead] = balance;
        DayHead = DayHead + 1 == Days ? 0 : DayHead + 1;
        if (DaysFilled < Days)
        {
            DaysFilled++;
        }
    }

    // ------------------------------------------------------------------ чтение

    /// <summary>Сумма окна: полные часы кольца плюс текущий неполный час (дельты движения текущего часа - по живым счётчикам).</summary>
    public KpiHour SumWindow(in SimCounters k, bool includeCurrent)
    {
        KpiHour s = default;
        for (int i = 0; i < HoursFilled; i++)
        {
            Add(ref s, in Ring[(Head - 1 - i + Hours) % Hours]);
        }

        if (includeCurrent)
        {
            KpiHour cur = Cur[0];
            cur.VehTicks = k.VehicleTicksTotal - VehAtHourStart;
            cur.MovingTicks = k.MovingVehicleTicksTotal - MovingAtHourStart;
            Add(ref s, in cur);
        }

        return s;
    }

    /// <summary>Значение показателя по сумме окна (×10, единицы <see cref="KpiKind"/>); -1 - данных нет.</summary>
    public int Value(KpiKind kind, in KpiHour s) => kind switch
    {
        KpiKind.TravelMin => MinX10(s.DurCarDs + s.DurBusDs + s.DurWalkDs, s.LegsAll),
        KpiKind.WorkMin => MinX10(s.DurWorkDs, s.LegsWork),
        KpiKind.IdlePct => s.VehTicks > 0 ? (int)((s.VehTicks - s.MovingTicks) * 1000 / s.VehTicks) : -1,
        KpiKind.BusSharePct => s.LegsAll > 0 ? (int)((long)s.LegsBus * 1000 / s.LegsAll) : -1,
        KpiKind.SatisfactionPct => s.LegsAll > 0 ? 1000 - (int)Math.Min(1000L, (long)s.Incidents * 1000 / s.LegsAll) : -1,
        _ => -1,
    };

    /// <summary>Средняя длительность, игровые минуты ×10: <c>Σ дс · 1440 · 10 / (day_s · 10) / legs</c> = <c>Σ дс · 28 800 / (DayTicks · legs)</c>.</summary>
    public int MinX10(long sumDs, int legs) => legs > 0 ? (int)Math.Min(int.MaxValue, sumDs * 28_800 / ((long)_dayTicks * legs)) : -1;

    /// <summary>Тиков текущего неполного часа.</summary>
    public int CurrentHourTicks(long tick)
    {
        long dayStart = tick - (tick % _dayTicks);
        int h = HourOf(tick, _dayTicks);
        long hourStart = dayStart + ((((long)h * _dayTicks) + Hours - 1) / Hours);
        return (int)(tick - hourStart);
    }

    /// <summary>
    /// Дней до кредитного лимита (progression.md §2.1 K5): <c>ceil((City + CreditLimit) / (ExpenseEwma - IncomeEwma))</c> при отрицательном тренде; 0 - лимит уже пройден; -1 - тренд &gt;= 0 или казны нет.
    /// </summary>
    public static int DaysToLimit(CityWorld c, int bonusDays)
    {
        CityBudget? b = c.Budget;
        if (b == null)
        {
            return -1;
        }

        long deficit = b.ExpenseEwma - b.IncomeEwma;
        if (deficit <= 0)
        {
            return -1;
        }

        long room = c.Ledger.City + b.CreditLimit(in c.Cfg.Economy, bonusDays);
        if (room <= 0)
        {
            return 0;
        }

        return (int)Math.Min(int.MaxValue, (room + deficit - 1) / deficit);
    }

    /// <summary>Раздел хэша (только при прогрессии, §2.4): кольцо в физическом порядке, текущий час, отметки, суточное кольцо.</summary>
    public void AddToHash(ref Fnv1a64 h, bool lifecycle)
    {
        h.Add(Head);
        h.Add(HoursFilled);
        h.Add(VehAtHourStart);
        h.Add(MovingAtHourStart);
        for (int i = 0; i < Hours; i++)
        {
            AddHour(ref h, in Ring[i], lifecycle);
        }

        AddHour(ref h, in Cur[0], lifecycle);
        h.Add(DayHead);
        h.Add(DaysFilled);
        for (int i = 0; i < Days; i++)
        {
            h.Add(DayBalance[i]);
        }
    }

    // Случаи «дом ветшает» - только с режимом city.lifecycle: без него хэш побитово прежний (building-lifecycle.md §5).
    private static void AddHour(ref Fnv1a64 h, in KpiHour k, bool lifecycle)
    {
        if (lifecycle)
        {
            h.Add(k.IncBuildingWorn);
        }

        h.Add(k.LegsCar);
        h.Add(k.LegsBus);
        h.Add(k.LegsWalk);
        h.Add(k.LegsWork);
        h.Add(k.DurCarDs);
        h.Add(k.DurBusDs);
        h.Add(k.DurWalkDs);
        h.Add(k.DurWorkDs);
        h.Add(k.VehTicks);
        h.Add(k.MovingTicks);
        h.Add(k.IncLongWait);
        h.Add(k.IncNoParking);
        h.Add(k.IncLongCommute);
        h.Add(k.IncTourCancelled);
        h.Add(k.IncLongStopWait);
    }

    private static void Add(ref KpiHour s, in KpiHour k)
    {
        s.LegsCar += k.LegsCar;
        s.LegsBus += k.LegsBus;
        s.LegsWalk += k.LegsWalk;
        s.LegsWork += k.LegsWork;
        s.DurCarDs += k.DurCarDs;
        s.DurBusDs += k.DurBusDs;
        s.DurWalkDs += k.DurWalkDs;
        s.DurWorkDs += k.DurWorkDs;
        s.VehTicks += k.VehTicks;
        s.MovingTicks += k.MovingTicks;
        s.IncLongWait += k.IncLongWait;
        s.IncNoParking += k.IncNoParking;
        s.IncLongCommute += k.IncLongCommute;
        s.IncTourCancelled += k.IncTourCancelled;
        s.IncLongStopWait += k.IncLongStopWait;
        s.IncBuildingWorn += k.IncBuildingWorn;
    }
}
