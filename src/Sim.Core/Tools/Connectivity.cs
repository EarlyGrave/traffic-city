using System;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Tools;

/// <summary>
/// Компоненты сильной связности графа направленных сегментов (DirSeg = seg * 2 + dir) для предупреждений превью (player-tools.md §7.3, урок T6).
/// Итеративный Tarjan на scratch; смежность - CSR по полосам и connector'ам сети (независимо от графа маршрутизации, который строится лениво в фазе маршрутов).
/// Кэш базовых компонент строится лениво по <c>Net.Version</c>; вариант «без сегмента» считается на втором массиве. Производное: вне хэша и сейва,
/// на результат применения не влияет. Массивы выделяются один раз при первом использовании (по ёмкости сети) и дальше не растут.
/// </summary>
internal sealed class Connectivity
{
    private readonly NetworkStore _n;
    private readonly int _v;                 // ёмкость DirSeg
    private readonly int[] _arcFirst;        // [_v + 1]
    private readonly int[] _arcTo;
    private readonly byte[] _ok;             // DirSeg жив и открыт
    private readonly int[] _idx;
    private readonly int[] _low;
    private readonly int[] _stack;
    private readonly int[] _callV;
    private readonly int[] _callE;
    private readonly int[] _weight;          // по компоненте: полос зон / вершин
    private int _builtVersion = -1;
    private int _builtSegCount = -1;

    /// <summary>Номер компонент базового графа: <c>CompBase[u]</c> или -1 (вершины нет).</summary>
    public readonly int[] CompBase;

    /// <summary>Компоненты графа без одного сегмента (<see cref="RunWithout"/>).</summary>
    public readonly int[] CompAlt;

    public Connectivity(NetworkStore net)
    {
        _n = net;
        _v = 2 * net.Segments.Length;
        _arcFirst = new int[_v + 1];
        _arcTo = new int[Math.Max(1, net.LaneNext.Length)];
        _ok = new byte[_v];
        _idx = new int[_v];
        _low = new int[_v];
        _stack = new int[_v];
        _callV = new int[_v];
        _callE = new int[_v];
        _weight = new int[_v];
        CompBase = new int[_v];
        CompAlt = new int[_v];
    }

    /// <summary>Эталонная компонента базового графа (-1 - графа нет).</summary>
    public int RefBase { get; private set; } = -1;

    /// <summary>Эталонная компонента варианта «без сегмента».</summary>
    public int RefAlt { get; private set; } = -1;

    public int CompCountBase { get; private set; }

    /// <summary>Сколько раз пересобирался базовый кэш (тест кэширования).</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Базовые компоненты по текущей версии сети (пересборка - только при смене <c>Net.Version</c>).</summary>
    public void EnsureBase()
    {
        NetworkStore n = _n;
        if (_builtVersion == n.Version && _builtSegCount == n.SegmentCount)
        {
            return;
        }

        BuildArcs();
        CompCountBase = Tarjan(-1, CompBase);
        RefBase = PickReference(CompBase, CompCountBase, -1);
        _builtVersion = n.Version;
        _builtSegCount = n.SegmentCount;
        Rebuilds++;
    }

    /// <summary>Компоненты графа, из которого убран сегмент <paramref name="seg"/> (обе его вершины). Требует <see cref="EnsureBase"/>.</summary>
    public void RunWithout(int seg)
    {
        int count = Tarjan(seg, CompAlt);
        RefAlt = PickReference(CompAlt, count, seg);
    }

    /// <summary>DirSeg полосы или -1 (не обычная полоса).</summary>
    public int DirSegOf(int lane)
    {
        if ((uint)lane >= (uint)_n.LaneCount)
        {
            return -1;
        }

        ref readonly LaneData l = ref _n.Lanes[lane];
        return l.Kind == LaneKind.Normal && l.Segment >= 0 ? (l.Segment * 2) + l.Direction : -1;
    }

    private void BuildArcs()
    {
        NetworkStore n = _n;
        int segs = n.SegmentCount;
        int arc = 0;
        int cap = _arcTo.Length;
        for (int u = 0; u < segs * 2; u++)
        {
            _arcFirst[u] = arc;
            int seg = u >> 1;
            int dir = u & 1;
            _ok[u] = 0;
            if (!n.SegmentAlive(seg))
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[seg];
            int lanes = dir == 0 ? sd.LanesAB : sd.LanesBA;
            if (lanes == 0 || (sd.Flags & SegmentFlags.Closing) != 0)
            {
                continue;
            }

            _ok[u] = 1;
            int first = dir == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            for (int k = 0; k < lanes; k++)
            {
                ref readonly LaneData l = ref n.Lanes[first + k];
                for (int j = 0; j < l.NextCount && arc < cap; j++)
                {
                    int conn = n.LaneNext[l.FirstNext + j];
                    int to = n.Lanes[conn].ToLane;
                    int v = DirSegOf(to);
                    if (v >= 0)
                    {
                        _arcTo[arc++] = v;
                    }
                }
            }
        }

        for (int u = segs * 2; u <= _v; u++)
        {
            _arcFirst[u] = arc;
        }
    }

    // Итеративный Tarjan; skipSeg >= 0 - вершины этого сегмента исключены. Возвращает число компонент; comp[u] = -1 для отсутствующих.
    private int Tarjan(int skipSeg, int[] comp)
    {
        int vcount = _n.SegmentCount * 2;
        for (int u = 0; u < vcount; u++)
        {
            _idx[u] = -1;
            comp[u] = -1;
        }

        int counter = 0;
        int comps = 0;
        int sp = 0;
        int cs = 0;
        for (int r = 0; r < vcount; r++)
        {
            if (_ok[r] == 0 || (r >> 1) == skipSeg || _idx[r] >= 0)
            {
                continue;
            }

            _callV[cs] = r;
            _callE[cs++] = _arcFirst[r];
            _idx[r] = _low[r] = counter++;
            _stack[sp++] = r;
            while (cs > 0)
            {
                int v = _callV[cs - 1];
                int e = _callE[cs - 1];
                if (e < _arcFirst[v + 1])
                {
                    _callE[cs - 1] = e + 1;
                    int to = _arcTo[e];
                    if (_ok[to] == 0 || (to >> 1) == skipSeg)
                    {
                        continue;
                    }

                    if (_idx[to] < 0)
                    {
                        _callV[cs] = to;
                        _callE[cs++] = _arcFirst[to];
                        _idx[to] = _low[to] = counter++;
                        _stack[sp++] = to;
                    }
                    else if (comp[to] < 0 && _idx[to] < _low[v])
                    {
                        _low[v] = _idx[to];   // to на стеке Tarjan (посещён, компонента ещё не назначена)
                    }
                }
                else
                {
                    if (_low[v] == _idx[v])
                    {
                        int w;
                        do
                        {
                            w = _stack[--sp];
                            comp[w] = comps;
                        }
                        while (w != v);
                        comps++;
                    }

                    cs--;
                    if (cs > 0)
                    {
                        int p = _callV[cs - 1];
                        if (_low[v] < _low[p])
                        {
                            _low[p] = _low[v];
                        }
                    }
                }
            }
        }

        return comps;
    }

    // Эталонная компонента: больше всего полос появления зон (ничья - меньший номер); без зон - самая большая по числу DirSeg.
    private int PickReference(int[] comp, int count, int skipSeg)
    {
        if (count == 0)
        {
            return -1;
        }

        NetworkStore n = _n;
        Array.Clear(_weight, 0, count);
        int best = -1;
        for (int z = 0; z < n.ZoneCount; z++)
        {
            ref readonly ZoneData zd = ref n.Zones[z];
            for (int k = 0; k < zd.SpawnLaneCount; k++)
            {
                int u = DirSegOf(n.ZoneLanes[zd.FirstSpawnLane + k]);
                if (u >= 0 && (u >> 1) != skipSeg && comp[u] >= 0)
                {
                    _weight[comp[u]]++;
                }
            }
        }

        int bestW = 0;
        for (int c = 0; c < count; c++)
        {
            if (_weight[c] > bestW)
            {
                bestW = _weight[c];
                best = c;
            }
        }

        if (best >= 0)
        {
            return best;
        }

        Array.Clear(_weight, 0, count);
        int vcount = n.SegmentCount * 2;
        for (int u = 0; u < vcount; u++)
        {
            if (comp[u] >= 0)
            {
                _weight[comp[u]]++;
            }
        }

        for (int c = 0; c < count; c++)
        {
            if (_weight[c] > bestW)
            {
                bestW = _weight[c];
                best = c;
            }
        }

        return best;
    }
}
