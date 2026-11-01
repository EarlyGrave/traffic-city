using System;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.City;

/// <summary>
/// Кодирование «места» внешнего подключения в полях жителя, где обычно индекс здания (<c>Work</c>, <c>PlanDest</c>, <c>LegDest</c>): <c>-2 - k</c>. <c>-1</c> по-прежнему «нет».
/// Все проверки «есть здание» в коде - <c>&gt;= 0</c>, поэтому код подключения их не проходит; «есть работа» - <c>!= -1</c>.
/// </summary>
internal static class OutsideRef
{
    public static bool Is(int place) => place <= -2;

    public static int Conn(int place) => -2 - place;

    public static int Of(int k) => -2 - k;
}

/// <summary>
/// Внешние подключения города (new-game-scratch.md §2.1-§2.5, ADR-0037): узлы у края карты, через которые въезжают переселенцы (машиной от узла до дома) и куда ездят работники
/// виртуальных внешних фирм. Состояние (сейв <c>OUTS</c>, раздел хэша) - вакансии, занято, счётчики въезда; производное - виртуальные участки узлов в <see cref="LotStore"/> (слоты
/// <c>Capacity + k</c>) и полоса въезда в узел, пересчёт по версии сети. Существует только при секции <c>outside</c>. Без аллокаций на тик (кроме пересчёта после правки сети).
/// </summary>
internal sealed class OutsideState
{
    /// <summary>Смена внешней работы: 08:00 (слот 96), 9 часов (108 слотов) - как у I-зданий по умолчанию плюс обед [Старт].</summary>
    public const int ShiftStartSlot = 96;
    public const int ShiftSlots = 108;

    public readonly int Count;
    public readonly int DecayQ16;
    public readonly bool ByCar;
    public readonly int[] Node = new int[CityOutsideDesc.Max];
    public readonly int[] JobCostDs = new int[CityOutsideDesc.Max];
    public readonly int[] ShareQ16 = new int[CityOutsideDesc.Max];

    // ---- состояние (OUTS) ----
    public readonly int[] Jobs = new int[CityOutsideDesc.Max];          // внешние места (вакансии + занятые)
    public readonly int[] Taken = new int[CityOutsideDesc.Max];         // занято жителями
    public readonly int[] ArrivalsDay = new int[CityOutsideDesc.Max];   // доехало домой за текущие сутки
    public readonly int[] ArrivalsHour = new int[CityOutsideDesc.Max];  // доехало за текущий игровой час (событие ImmigrantsArrived)
    public long Arrivals;            // всего доехало
    public long ArrivalTimeouts;     // не дождались выезда за wait_max: домой агрегатом (клапан), не застревание

    // ---- производное (вне сейва и хэша) ----
    public readonly int[] InLane = new int[CityOutsideDesc.Max];        // полоса въезда в узел (конец ноги на внешнюю работу) или -1
    private int _netVersion = -1;

    public OutsideState(in CityOutsideDesc d)
    {
        Count = d.Count;
        DecayQ16 = d.JobsDecayQ16;
        ByCar = d.ArrivalByCar != 0;
        for (int k = 0; k < Count; k++)
        {
            OutsideConnDesc cd = d.Get(k);
            Node[k] = cd.Node;
            Jobs[k] = cd.Jobs;
            JobCostDs[k] = Calendar.TicksToDs(cd.JobCostTicks);
            ShareQ16[k] = cd.ShareQ16;
        }

        Array.Fill(InLane, -1);
    }

    /// <summary>Свободные внешние места подключения <paramref name="k"/>.</summary>
    public int Vac(int k) => Math.Max(0, Jobs[k] - Taken[k]);

    /// <summary>Свободные внешние места всех подключений.</summary>
    public long TotalVac()
    {
        long v = 0;
        for (int k = 0; k < Count; k++)
        {
            v += Vac(k);
        }

        return v;
    }

    /// <summary>Производные устарели (загрузка): пересчитать при следующем <see cref="Ensure"/>.</summary>
    public void Invalidate() => _netVersion = -1;

    /// <summary>
    /// Виртуальные участки узлов подключения по текущей сети (после правок и загрузки): центр = ось узла, ячейка сетки города, полоса выезда из узла (<c>CarLane</c>, начало ноги
    /// от узла), полоса въезда (<see cref="InLane"/>), дорожная компонента - метка узла как у участков. Нет одной из полос - <c>CarLane = -1</c> (машиной нельзя).
    /// </summary>
    public void Ensure(World w, CityWorld c)
    {
        NetworkStore net = w.Net;
        if (_netVersion == net.Version)
        {
            return;
        }

        _netVersion = net.Version;
        int[] labels = LotBuilder.RoadLabels(net);
        LotStore l = c.Lots;
        for (int k = 0; k < Count; k++)
        {
            int lot = l.Capacity + k;
            int node = Node[k];
            int outLane = -1;
            int inLane = -1;
            float x = 0f;
            float z = 0f;
            if (net.NodeAlive(node))
            {
                ref readonly NodeData nd = ref net.Nodes[node];
                x = nd.Position.X;
                z = nd.Position.Z;
                for (int j = 0; j < nd.ArmCount; j++)
                {
                    int s = net.NodeArms[nd.FirstArm + j];
                    if (!net.SegmentAlive(s))
                    {
                        continue;
                    }

                    ref readonly SegmentData sd = ref net.Segments[s];
                    if (outLane < 0)
                    {
                        outLane = sd.NodeA == node && sd.LanesAB > 0 ? sd.FirstLaneAB : (sd.NodeB == node && sd.LanesBA > 0 ? sd.FirstLaneBA : -1);
                    }

                    if (inLane < 0)
                    {
                        inLane = sd.NodeB == node && sd.LanesAB > 0 ? sd.FirstLaneAB : (sd.NodeA == node && sd.LanesBA > 0 ? sd.FirstLaneBA : -1);
                    }
                }
            }

            bool car = outLane >= 0 && inLane >= 0;
            l.X[lot] = x;
            l.Z[lot] = z;
            l.Heading[lot] = 0f;
            l.Cell[lot] = c.Grid.CellOf(x, z);
            l.CarLane[lot] = car ? outLane : -1;
            l.CarS[lot] = 0f;
            l.PedEdge[lot] = -1;
            l.PedSMm[lot] = 0;
            l.PedComp[lot] = -1;
            l.RoadComp[lot] = car && (uint)node < (uint)labels.Length ? labels[node] : -1;
            InLane[k] = car ? inLane : -1;
        }
    }

    /// <summary>Затухание (§2.4, раз в сутки): внешние места сокращаются на <c>decay · свои вакансии города</c>, но не ниже занятых (никого не увольняем).</summary>
    public void DayDecay(long localVac)
    {
        long dec = ((long)DecayQ16 * Math.Max(0L, localVac)) >> 16;
        for (int k = 0; k < Count; k++)
        {
            Jobs[k] = (int)Math.Max(Taken[k], Jobs[k] - dec);
        }
    }

    /// <summary>Граница суток: счётчик въезда за сутки с нуля.</summary>
    public void OnDayEnd() => Array.Clear(ArrivalsDay);

    /// <summary>Конец игрового часа: событие <see cref="SimEventKind.ImmigrantsArrived"/> по подключениям, где кто-то доехал.</summary>
    public void OnHourEnd(World w)
    {
        for (int k = 0; k < Count; k++)
        {
            if (ArrivalsHour[k] > 0)
            {
                w.Emit(SimEventKind.ImmigrantsArrived, 0, ArrivalsHour[k], k);
                ArrivalsHour[k] = 0;
            }
        }
    }

    /// <summary>Подключение въезда для нового жителя: по <c>immigrant_share</c>, один вызов PRNG <c>Demand</c> на жителя (только при нескольких подключениях).</summary>
    public int PickArrival(World w)
    {
        if (Count <= 1)
        {
            return 0;
        }

        uint x = w.RngDemand.NextUInt() >> 16;
        uint acc = 0;
        for (int k = 0; k < Count; k++)
        {
            acc += (uint)ShareQ16[k];
            if (x < acc)
            {
                return k;
            }
        }

        return Count - 1;
    }
}
