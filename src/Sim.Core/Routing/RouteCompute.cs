using System;
using Sim.Core.Network;

namespace Sim.Core.Routing;

/// <summary>Вход запроса маршрута (ровно аргументы <see cref="Router.Build"/>).</summary>
internal readonly record struct RouteQuery(int StartLane, float StartS, int DestLane, float DestS);

/// <summary>На какой стадии закончился <see cref="RouteCompute.Run"/>.</summary>
internal enum QueryStage : byte
{
    QueryFail = 0,        // поиск не нашёл путь (RouteFailed)
    MaterializeFail = 1,  // развёртывание первых DirSeg не удалось (RouteFailed)
    LayoutFail = 2,       // раскладка на полосы не удалась (RouteFailed)
    Found = 3,
    Overflow = 4,         // зарезервировано для пакета (P2): стек не влез в арену воркера
}

/// <summary>
/// Результат <see cref="RouteCompute.Run"/>: числа для счётчиков и бюджета (фиксирует <see cref="Router.Commit"/>), а шаги окна и стек плана
/// лежат в <see cref="RouteScratch"/>, на котором шёл счёт (<c>Steps[0..StepCount)</c>, <c>Stk[0..StackLen)</c>), до его следующего использования.
/// </summary>
internal struct QueryResult
{
    public QueryStage Stage;
    public float Cost;       // OverlaySearcher.PlanCost
    public int QueryWork;    // OverlaySearcher.LastWork
    public int Expanded;     // OverlaySearcher.LastExpanded
    public int Unpacks;      // развёртываний, включая неудавшееся
    public int UnpackWork;
    public int StepCount;
    public int StackLen;
}

/// <summary>
/// Чистая часть построения маршрута (routing-parallel.md 3.1): запрос, развёртывание первых DirSeg, раскладка на полосы. Не получает
/// <c>World</c>: пишет только <see cref="RouteScratch"/> и результат, читает сеть, граф и оверлей. Поэтому может идти на любом воркере.
/// </summary>
internal static class RouteCompute
{
    public static void Run(in RouteQuery q, NetworkStore net, RouteGraph g, Overlay? ov, RouteScratch s, ref QueryResult r)
    {
        System.Diagnostics.Debug.Assert(ReferenceEquals(s.Searcher.Overlay, ov), "scratch собран под другой оверлей");
        r = default;
        OverlaySearcher se = s.Searcher;
        SearchStatus st = se.Query(q.StartLane, q.StartS, q.DestLane, q.DestS, s.Plan, out int pl);
        r.QueryWork = se.LastWork;
        r.Expanded = se.LastExpanded;
        if (st != SearchStatus.Found)
        {
            r.Stage = QueryStage.QueryFail;
            return;
        }

        r.Cost = se.PlanCost;
        s.Dirs[0] = s.Plan[0] & OverlaySearcher.SegMask;
        s.Nd = 1;
        s.Sp = 0;
        for (int i = pl - 1; i >= 1; i--)
        {
            s.Stk[s.Sp++] = s.Plan[i];
        }

        st = Materialize(s, s.MaxDirs, ref r.Unpacks, ref r.UnpackWork);
        r.StackLen = s.Sp;
        if (st != SearchStatus.Found)
        {
            r.Stage = QueryStage.MaterializeFail;
            return;
        }

        int layoutDest = s.Sp == 0 ? q.DestLane : FirstLane(net, s.Dirs[s.Nd - 1]);
        int n = RouteLayout.LayoutLanes(net, g, new ReadOnlySpan<int>(s.Dirs, 0, s.Nd), s.Nd, q.StartLane, layoutDest, s.Steps);
        if (n < 0)
        {
            r.Stage = QueryStage.LayoutFail;
            return;
        }

        r.StepCount = n;
        r.Stage = QueryStage.Found;
    }

    public static int FirstLane(NetworkStore net, int dirSeg) =>
        (dirSeg & 1) == 0 ? net.Segments[dirSeg >> 1].FirstLaneAB : net.Segments[dirSeg >> 1].FirstLaneBA;

    // Снимает элементы стека s.Stk[0..s.Sp) в s.Dirs, пока там меньше target DirSeg: уровень 0 - сразу, выше - ленивое развёртывание
    // (routing.md 9.5: глубина <= Levels, одно развёртывание <= UnpackCap элементов). Развёртывания считаются и при сбое.
    public static SearchStatus Materialize(RouteScratch s, int target, ref int unpacks, ref int unpackWork)
    {
        while (s.Nd < target && s.Sp > 0)
        {
            int e = s.Stk[--s.Sp];
            int u = e & OverlaySearcher.SegMask;
            int q = e >> OverlaySearcher.LevelShift;
            if (q == 0)
            {
                s.Dirs[s.Nd++] = u;
                continue;
            }

            SearchStatus st = s.Searcher.Unpack(s.Dirs[s.Nd - 1], u, q, s.Ubuf, out int ul);
            unpacks++;
            unpackWork += s.Searcher.LastUnpackWork;
            if (st != SearchStatus.Found)
            {
                return SearchStatus.NoPath;
            }

            if (s.Sp + ul > s.Stk.Length)
            {
                return SearchStatus.PathTooLong;
            }

            for (int k = ul - 1; k >= 0; k--)
            {
                s.Stk[s.Sp++] = s.Ubuf[k];
            }
        }

        return SearchStatus.Found;
    }
}
