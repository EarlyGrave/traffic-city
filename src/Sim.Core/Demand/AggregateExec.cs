using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Demand;

/// <summary>
/// Агрегатное исполнение ног и «10 лет» (zoning-demand.md §9, ADR-0021). Нога без агента: время машины - <c>Σ t0_c · F(L / Cap) · K</c> (<see cref="CellLoads.ModelCarDs"/>), пешком и автобус - ским
/// пары (наблюдение либо априорная оценка §5.4); прибытие - событие <see cref="ResidentEvent.Arrive"/> в колесе жителя через это время, дальше обычный <c>TripLegs.CompleteLeg</c> (скимы, зарплата, визит, конец тура).
/// <para>
/// Переключение (<c>SetTripExecution</c>, команда 24) запоминается в <see cref="CityWorld.ExecPending"/> и применяется на границе суток (<see cref="OnDayBoundary"/>): micro - aggregate калибрует
/// <c>K = clamp(ским_micro / модель, 0.5, 4)</c> по только что закончившимся micro-суткам. Пока агентов нет, фазы трафика 2-10 не выполняются (<see cref="IsIdle"/>), а <c>Step</c> перепрыгивает тики,
/// на которых ничего не происходит (<see cref="IdleSpan"/>, <see cref="Jump"/>): результат тот же, что у пошагового прохода.
/// </para>
/// </summary>
internal static class AggregateExec
{
    private const int KMinQ8 = 128;     // 0.5
    private const int KMaxQ8 = 1024;    // 4.0
    private const int KUnitQ8 = 256;

    /// <summary>Применяет <c>SetTripExecution</c>: запрос ставится в очередь на границу суток; <see cref="RejectReason.None"/> - принят.</summary>
    public static RejectReason ApplySwitch(World w, in Command cmd)
    {
        CityWorld? c = w.City;
        if (c == null)
        {
            return RejectReason.NotSupported;
        }

        byte mode = cmd.SetTripExecution.Mode;
        if (mode > (byte)TripExecution.Aggregate)
        {
            return RejectReason.InvalidArgument;
        }

        c.ExecPending = mode == (byte)c.Exec ? -1 : mode;
        return RejectReason.None;
    }

    /// <summary>Граница суток (фаза 9r, после <c>Day++</c>): применяет отложенное переключение; micro - aggregate предварительно калибрует <c>K</c> по вчерашним (только что законченным) суткам.</summary>
    public static void OnDayBoundary(World w, CityWorld c)
    {
        int pending = c.ExecPending;
        if (pending < 0)
        {
            return;
        }

        c.ExecPending = -1;
        if (pending == (int)c.Exec)
        {
            return;
        }

        if (pending == (int)TripExecution.Aggregate)
        {
            Calibrate(w, c);
        }

        c.Exec = (TripExecution)pending;
    }

    // ------------------------------------------------------------------ нога без агента

    /// <summary>
    /// Нога тура <c>PlanCursor</c> без агента (аналог <c>TripLegs.StartLeg</c>): способ уточняется (машина без полос или пары - пешком, автобус без пары остановок - пешком), время - модель или ским,
    /// житель <c>InLeg</c> без агента, событие <c>Arrive</c> через это время. false - ни один способ недоступен.
    /// <para>
    /// <paramref name="reqTick"/> &gt;= 0 - нога клапана перегрузки в micro (ADR-0035): нога уже начата и учтена в <c>ResLegs*</c>, поэтому счётчики способов не растут, а <c>LegReq</c> остаётся тиком
    /// исходной заявки - стоимость ноги (и наблюдение в скимы) включает всё ожидание выезда.
    /// </para>
    /// </summary>
    internal static bool StartLeg(World w, CityWorld c, int r, bool outbound, TravelMode mode, long reqTick = -1)
    {
        CityResidents res = c.Residents;
        int tour = (r * CityResidents.MaxTours) + res.PlanCursor[r];
        int home = res.Home[r];
        int far = res.PlanDest[tour];
        int dest = outbound ? far : home;
        int lo = c.Buildings.Lot[outbound ? home : far];
        int ld = c.Buildings.Lot[dest];
        long now = w.Tick;
        int o = c.Lots.Cell[lo];
        int d = c.Lots.Cell[ld];
        Skims skims = c.Skims;
        skims.Prepare(w);
        int period = skims.PeriodOf(now, c.DayTicks);
        if (mode == TravelMode.Car && (c.Lots.CarLane[lo] < 0 || c.Lots.CarLane[ld] < 0 || c.Lots.RoadComp[lo] != c.Lots.RoadComp[ld]))
        {
            mode = TravelMode.Walk;
        }

        int ds = 0;
        if (mode == TravelMode.Car)
        {
            ds = CarDs(w, c, o, d, period);
            if (ds == 0)
            {
                mode = TravelMode.Walk;
            }
        }

        if (mode == TravelMode.Bus)
        {
            ds = skims.Get(o, d, TravelMode.Bus, period);
            if (ds == 0)
            {
                mode = TravelMode.Walk;
            }
        }

        if (mode == TravelMode.Walk)
        {
            if (c.Lots.PedComp[lo] >= 0 && c.Lots.PedComp[ld] >= 0 && c.Lots.PedComp[lo] != c.Lots.PedComp[ld])
            {
                return false;   // пешеходные компоненты разные: пешком не дойти (как в micro)
            }

            ds = skims.Get(o, d, TravelMode.Walk, period);
        }

        if (res.PlanMode[tour] != (byte)mode)
        {
            res.PlanMode[tour] = (byte)mode;   // обратная нога идёт тем же способом
        }

        res.LegMode[r] = (byte)mode;
        res.LegO[r] = (ushort)o;
        res.LegD[r] = (ushort)d;
        res.LegReq[r] = reqTick >= 0 ? reqTick : now;
        res.LegWalk[r] = 0;
        res.LegWait[r] = 0;
        res.LegRide[r] = 0;
        res.LegDest[r] = dest;
        res.LegStopA[r] = -1;
        res.LegStopB[r] = -1;
        res.Agent[r] = -1;
        res.AgentGen[r] = 0;
        res.State[r] = (byte)ResidentState.InLeg;
        res.StateTick[r] = now;
        if (reqTick < 0)
        {
            switch (mode)
            {
                case TravelMode.Car:
                    w.Counters.ResLegsCar++;
                    break;
                case TravelMode.Bus:
                    w.Counters.ResLegsBus++;
                    break;
                default:
                    w.Counters.ResLegsWalk++;
                    break;
            }
        }

        w.Counters.AggregateLegs++;
        long ticks = Math.Max(2L, Calendar.DsToTicks(Math.Max(1, ds)));
        CityPhase.SetEvent(w, c, r, now + ticks, ResidentEvent.Arrive);
        return true;
    }

    /// <summary>Время машинной ноги, дс: модель BPR на вчерашней загрузке, умноженная на <c>K</c> (0 в <c>K</c> - не калибровано, 1.0); 0 - машина для пары недоступна.</summary>
    private static int CarDs(World w, CityWorld c, int o, int d, int period)
    {
        int model = c.Loads.ModelCarDs(w, c, o, d, period, c.Day);
        if (model == 0)
        {
            return 0;
        }

        int k = c.Skims.K[((((o * c.Skims.Cells) + d)) * c.Skims.Periods) + period];
        if (k == 0)
        {
            k = KUnitQ8;
        }

        long ds = ((long)model * k) >> 8;
        return ds < 1 ? 1 : (ds > int.MaxValue ? int.MaxValue : (int)ds);
    }

    // ------------------------------------------------------------------ калибровка (§9.2)

    /// <summary>
    /// <c>K[o, d, p] = clamp(ским_micro / модель, 0.5, 4)</c> в Q8 по всем парам с машинным скимом; пары без данных получают медиану <c>K</c> своего класса расстояния (|dx| + |dz| ячеек),
    /// класс без данных - медиану по городу, без данных вообще - 1.0. Вызывается на границе суток: «вчера» - только что закончившиеся micro-сутки.
    /// </summary>
    internal static void Calibrate(World w, CityWorld c)
    {
        Skims s = c.Skims;
        s.Prepare(w);
        s.Flush(c);   // ноги длиннее периода доезжают позже: учесть наблюдения последних периодов суток
        int nc = s.Cells;
        int periods = s.Periods;
        int cx = c.Grid.CellsX;
        int cz = c.Grid.CellsZ;
        int classes = cx + cz - 1;
        const int Bins = KMaxQ8 - KMinQ8 + 1;
        var hist = new int[(classes + 1) * Bins];   // последний класс - весь город
        Array.Clear(s.K);
        for (int p = 0; p < periods; p++)
        {
            for (int o = 0; o < nc; o++)
            {
                for (int d = 0; d < nc; d++)
                {
                    int skim = s.Skim[s.KeyOf(o, d, TravelMode.Car, p)];
                    if (skim <= 0)
                    {
                        continue;
                    }

                    int model = c.Loads.ModelCarDs(w, c, o, d, p, c.Day);
                    if (model <= 0)
                    {
                        continue;
                    }

                    long k = ((long)skim * KUnitQ8) / model;
                    int kq = (int)Math.Clamp(k, KMinQ8, KMaxQ8);
                    s.K[(((o * nc) + d) * periods) + p] = kq;
                    int cls = ClassOf(o, d, cx);
                    hist[(cls * Bins) + kq - KMinQ8]++;
                    hist[(classes * Bins) + kq - KMinQ8]++;
                }
            }
        }

        int global = Median(hist, classes * Bins, Bins);
        for (int p = 0; p < periods; p++)
        {
            for (int o = 0; o < nc; o++)
            {
                for (int d = 0; d < nc; d++)
                {
                    int idx = (((o * nc) + d) * periods) + p;
                    if (s.K[idx] != 0 || s.Prior(o, d, TravelMode.Car) == 0)
                    {
                        continue;
                    }

                    int m = Median(hist, ClassOf(o, d, cx) * Bins, Bins);
                    s.K[idx] = m != 0 ? m : (global != 0 ? global : KUnitQ8);
                }
            }
        }
    }

    private static int ClassOf(int o, int d, int cellsX) => Math.Abs((o % cellsX) - (d % cellsX)) + Math.Abs((o / cellsX) - (d / cellsX));

    /// <summary>Нижняя медиана гистограммы <c>K</c> (бин <c>i</c> = <c>KMinQ8 + i</c>); 0 - пусто.</summary>
    private static int Median(int[] hist, int offset, int bins)
    {
        long total = 0;
        for (int i = 0; i < bins; i++)
        {
            total += hist[offset + i];
        }

        if (total == 0)
        {
            return 0;
        }

        long half = (total + 1) / 2;
        long acc = 0;
        for (int i = 0; i < bins; i++)
        {
            acc += hist[offset + i];
            if (acc >= half)
            {
                return KMinQ8 + i;
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ пауза трафика и прыжки (§9.2)

    /// <summary>Агрегат и в мире нет агентов: ни машин, ни пешеходов, ни заявок на них - фазы трафика 2-10 не нужны (сигналы стоят, состояние не меняется).</summary>
    public static bool IsIdle(World w)
    {
        CityWorld? c = w.City;
        if (c == null || c.Exec != TripExecution.Aggregate || w.Vehicles.AliveCount != 0 || w.Trips.Count != 0)
        {
            return false;
        }

        Peds.PedWorld? pw = w.Peds;
        return pw == null || (pw.CitySource && pw.P.AliveCount == 0 && pw.LegRequests.Count == 0);
    }

    /// <summary>
    /// Сколько ближайших тиков (не больше <paramref name="max"/>) пусты: команд нет, трафик на паузе, нет события колеса, прибытия, границы периода скимов, ячейки доступности, границы суток и дневного прохода.
    /// Такие тики ничего не меняют ни в состоянии, ни в хэше, кроме номера тика и счётчика тиков. 0 - надо выполнять тик обычным путём.
    /// </summary>
    public static long IdleSpan(World w, long max)
    {
        CityWorld? c = w.City;
        if (c == null || w.DisableJump || w.PausedTicks < 2 || w.Commands.Count != 0 || !IsIdle(w))
        {
            return 0;
        }

        Skims s = c.Skims;
        if (!c.Arrivals.IsEmpty || c.DayStage != DayStages.Idle || s.UpdateCursor < s.FrozenN || !c.Market.MatchIsNoop(c))
        {
            return 0;
        }

        long tick = w.Tick;
        int dayTicks = c.DayTicks;
        long n = max;
        if (s.AbsolutePeriodOf(tick, dayTicks) != s.CurPeriod)
        {
            return 0;
        }

        // первый тик следующего периода: наименьший t с t * P / dayTicks >= CurPeriod + 1
        long nextPeriod = (((s.CurPeriod + 1) * dayTicks) + s.Periods - 1) / s.Periods;
        n = Math.Min(n, nextPeriod - tick);
        // граница суток: тик t с (t + 1) % dayTicks == 0 исполняется обычным путём
        long boundary = tick + (dayTicks - 1 - (tick % dayTicks));
        n = Math.Min(n, boundary - tick);
        // сдвиг часа показателей (progression.md §2.2): последний тик игрового часа исполняется обычным путём
        n = Math.Min(n, CityKpi.NextHourEnd(tick, dayTicks) - tick);
        // ячейка доступности: тики, кратные step
        long step = Math.Max(1, dayTicks / Math.Max(1, s.Cells));
        long access = tick % step == 0 ? tick : tick + (step - (tick % step));
        n = Math.Min(n, access - tick);
        if (n <= 0)
        {
            return 0;
        }

        long due = c.Wheel.NextDue(tick - 1, tick + n - 1);
        if (due != long.MaxValue)
        {
            n = Math.Min(n, due - tick);
        }

        return n < 0 ? 0 : n;
    }

    /// <summary>Перепрыгивает <paramref name="n"/> пустых тиков (<see cref="IdleSpan"/>).</summary>
    public static void Jump(World w, long n)
    {
        CityWorld c = w.City!;
        c.Wheel.AdvanceTo(w.Tick - 1, w.Tick + n - 1);
        w.Tick += n;
        w.Counters.Ticks += n;
        w.SnapshotTick = w.Tick;
        w.JumpedTicks += n;
        WorldBuilder.SyncStructuralCounters(w);
    }
}
