using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Routing;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Пути пешеходов (pedestrians.md §4.4, фаза 3b): A* по графу сети пешеходов от точки <c>(ребро, s)</c> до точки <c>(ребро, s)</c>.
/// Стоимость рёбер - <c>PedEdgeData.CostTicks</c> (статический приор; измеренную часть добавит P3), эвристика - евклид, делённый на номинальную скорость.
/// Предел - <c>max_walk_m * 1.5</c> по стоимости; больше - <c>NoPath</c>. Путь - шаги <c>edge | StepCross | StepBwd</c> в арене пешехода.
/// Все буферы выделены в конструкторе; метки поколения запроса вместо очистки.
/// </summary>
internal sealed class PedRouter
{
    private const float TickPerMm = 20f / PedNetwork.NominalSpeedMmPerSec;   // тиков на мм при 1.3 м/с

    private readonly PedNetwork _net;
    private readonly IndexedHeap4 _heap;
    private readonly float[] _g;
    private readonly int[] _seen;          // метка запроса: _g/_parent валидны
    private readonly int[] _closed;        // метка запроса: узел закрыт
    private readonly int[] _parent;        // ребро, которым пришли в узел, или -1 (источник)
    private readonly int[] _rev;           // обратный путь при восстановлении
    private int _stamp;

    public PedRouter(PedNetwork net, int maxSteps)
    {
        _net = net;
        int n = Math.Max(1, net.NodeCap);
        _heap = new IndexedHeap4(n);
        _g = new float[n];
        _seen = new int[n];
        _closed = new int[n];
        _parent = new int[n];
        _rev = new int[Math.Max(1, maxSteps) + 2];
    }

    /// <summary>
    /// Ищет путь и пишет шаги в <paramref name="path"/> с <paramref name="pathBase"/> (не более <paramref name="maxSteps"/>). <paramref name="work"/> += релаксации.
    /// <paramref name="firstS0Mm"/> - смещение входа на первое ребро вдоль хода. false - пути нет в пределах стоимости или он длиннее арены.
    /// </summary>
    public bool Find(int startEdge, int startMm, int goalEdge, int goalMm, int[] path, int pathBase, int maxSteps, float limitTicks, ref long work, out int count, out int firstS0Mm)
    {
        count = 0;
        firstS0Mm = 0;
        PedEdgeData[] edges = _net.Edges;
        if ((uint)startEdge >= (uint)_net.EdgeCount || (uint)goalEdge >= (uint)_net.EdgeCount)
        {
            return false;
        }

        ref readonly PedEdgeData se = ref edges[startEdge];
        ref readonly PedEdgeData ge = ref edges[goalEdge];
        if (startEdge == goalEdge)
        {
            bool fwd = goalMm >= startMm;
            path[pathBase] = startEdge | (fwd ? 0 : PedStore.StepBwd);
            count = 1;
            firstS0Mm = fwd ? startMm : se.LenMm - startMm;
            return true;
        }

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_seen);
            Array.Clear(_closed);
            _stamp = 1;
        }

        int stamp = _stamp;
        _heap.Clear();
        Vec2 goalPos = PointAt(goalEdge, goalMm);
        float toGoalA = goalMm * TickPerMm;
        float toGoalB = (ge.LenMm - goalMm) * TickPerMm;
        int gA = ge.A;
        int gB = ge.B;
        Relax(se.A, startMm * TickPerMm, -1, goalPos, stamp);
        Relax(se.B, (se.LenMm - startMm) * TickPerMm, -1, goalPos, stamp);
        float best = float.MaxValue;
        int bestEnd = -1;
        long relax = 0;
        while (_heap.Count > 0)
        {
            int n = _heap.Pop(out float f);
            if (f >= best || f > limitTicks)
            {
                break;
            }

            _closed[n] = stamp;
            if (n == gA && _g[n] + toGoalA < best)
            {
                best = _g[n] + toGoalA;
                bestEnd = n;
            }

            if (n == gB && _g[n] + toGoalB < best)
            {
                best = _g[n] + toGoalB;
                bestEnd = n;
            }

            _net.Adjacent(n, out int first, out int cnt);
            for (int k = first; k < first + cnt; k++)
            {
                int e = _net.AdjEdge[k];
                int other = _net.Other(e, n);
                relax++;
                if (_closed[other] == stamp)
                {
                    continue;
                }

                Relax(other, _g[n] + edges[e].CostTicks, e, goalPos, stamp);
            }
        }

        work += relax;
        if (bestEnd < 0 || best > limitTicks)
        {
            return false;
        }

        // Восстановление: последний шаг - ребро цели от узла bestEnd; затем рёбра назад до источника; первый шаг - от точки старта к источнику.
        int c = 0;
        _rev[c++] = goalEdge | (bestEnd == gA ? 0 : PedStore.StepBwd);
        int node = bestEnd;
        while (_parent[node] >= 0)
        {
            int e = _parent[node];
            int prev = _net.Other(e, node);
            bool crossing = edges[e].Kind >= PedEdgeKind.Crossing;
            if (c + 1 >= maxSteps)
            {
                return false;
            }

            _rev[c++] = e | (edges[e].A == prev ? 0 : PedStore.StepBwd) | (crossing ? PedStore.StepCross : 0);
            node = prev;
        }

        bool towardA = node == se.A;
        _rev[c++] = startEdge | (towardA ? PedStore.StepBwd : 0);
        firstS0Mm = towardA ? se.LenMm - startMm : startMm;
        if (c > maxSteps)
        {
            return false;
        }

        for (int k = 0; k < c; k++)
        {
            path[pathBase + k] = _rev[c - 1 - k];
        }

        count = c;
        return true;
    }

    private void Relax(int node, float g, int viaEdge, Vec2 goal, int stamp)
    {
        if (_seen[node] == stamp && g >= _g[node])
        {
            return;
        }

        _seen[node] = stamp;
        _g[node] = g;
        _parent[node] = viaEdge;
        Vec2 d = _net.NodePos[node] - goal;
        float h = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z)) * 1000f * TickPerMm;
        _heap.PushOrDecrease(node, g + h);
    }

    /// <summary>Мировая точка ребра на расстоянии <paramref name="mm"/> от <c>A</c> (только для эвристики: линейная интерполяция по концам полилинии).</summary>
    private Vec2 PointAt(int edge, int mm)
    {
        ref readonly PedEdgeData e = ref _net.Edges[edge];
        int hint = e.FirstPoint;
        _net.Evaluate(edge, mm, false, ref hint, out float x, out float z, out _);
        return new Vec2(x, z);
    }

    /// <summary>Предел стоимости запроса в тиках: <c>max_walk_m * 1.5</c> пути на номинальной скорости (§4.4).</summary>
    public static float LimitTicks(in PedDesc cfg) => cfg.MaxWalkMm * 1.5f * TickPerMm;
}
