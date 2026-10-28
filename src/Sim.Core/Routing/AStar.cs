using System;
using Sim.Core.Util;

namespace Sim.Core.Routing;

internal enum SearchStatus : byte
{
    Found = 0,
    NoPath = 1,        // цель недостижима (очередь исчерпана)
    ExpansionCap = 2,  // упёрлись в MaxExpansions
    HeapOverflow = 3,  // не должно случаться по построению
    PathTooLong = 4,   // буфер результата мал
}

/// <summary>
/// A* по <see cref="RouteGraph"/> (routing.md 2): евклидова эвристика / VMax, предвыделенная бинарная
/// min-куча с ленивым обновлением, generation-stamped G/Parent/Closed (O(1) старт). Тай-брейк - меньший DirSeg.
/// Без аллокаций после конструктора.
/// </summary>
internal sealed class AStarSearcher
{
    /// <summary>Эвристика занижена на 5 % (константа масштаба сохраняет монотонность): длина connector'а и боковые смещения полос.</summary>
    private const float HeuristicScale = 0.95f;
    private const int MaxStarts = 64;

    private readonly RouteGraph _g;
    private readonly float[] _gScore;
    private readonly int[] _parent;
    private readonly uint[] _seen;
    private readonly uint[] _closed;
    private readonly float[] _heapKey;
    private readonly int[] _heapVal;
    private uint _searchId;
    private int _heapCount;

    public AStarSearcher(RouteGraph graph)
    {
        _g = graph;
        int n = graph.DirSegCapacity;
        _gScore = new float[n];
        _parent = new int[n];
        _seen = new uint[n];
        _closed = new uint[n];
        int heap = graph.ArcCapacity + MaxStarts + 16;
        _heapKey = new float[heap];
        _heapVal = new int[heap];
    }

    /// <summary>Стоимость найденного пути (G цели), с.</summary>
    public float LastCost { get; private set; }

    /// <summary>Число раскрытых вершин последнего поиска.</summary>
    public int LastExpanded { get; private set; }

    /// <summary>
    /// Поиск. Цель - вершина <paramref name="goalSeg"/> либо (если goalSeg &lt; 0) любая вершина, заканчивающаяся
    /// в узле <paramref name="goalNode"/>. <paramref name="startG"/> - начальная стоимость каждой стартовой вершины.
    /// Путь (от старта к цели, включая обе вершины) пишется в <paramref name="path"/>.
    /// </summary>
    public SearchStatus Search(
        ReadOnlySpan<int> starts,
        ReadOnlySpan<float> startG,
        int goalSeg,
        int goalNode,
        Vec2 goalPos,
        int maxExpansions,
        Span<int> path,
        out int pathLength)
    {
        pathLength = 0;
        LastCost = 0f;
        LastExpanded = 0;
        if (++_searchId == 0)
        {
            Array.Clear(_seen);
            Array.Clear(_closed);
            _searchId = 1;
        }

        uint id = _searchId;
        _heapCount = 0;
        float invV = HeuristicScale / _g.VMax;
        int expanded = 0;

        for (int i = 0; i < starts.Length; i++)
        {
            int u = starts[i];
            float g0 = startG[i];
            if (_seen[u] == id && _gScore[u] <= g0)
            {
                continue;
            }

            _seen[u] = id;
            _gScore[u] = g0;
            _parent[u] = -1;
            if (!Push(g0 + (H(u, goalPos) * invV), u))
            {
                return SearchStatus.HeapOverflow;
            }
        }

        while (_heapCount > 0)
        {
            int v = Pop();
            if (_closed[v] == id)
            {
                continue;
            }

            _closed[v] = id;
            if (v == goalSeg || (goalSeg < 0 && _g.EndNode[v] == goalNode))
            {
                LastCost = _gScore[v];
                LastExpanded = expanded;
                return Reconstruct(v, path, out pathLength);
            }

            if (expanded >= maxExpansions)
            {
                LastExpanded = expanded;
                return SearchStatus.ExpansionCap;
            }

            expanded++;
            float gv = _gScore[v];
            int end = _g.ArcFirst[v + 1];
            for (int a = _g.ArcFirst[v]; a < end; a++)
            {
                int w = _g.ArcTo[a];
                if (_closed[w] == id)
                {
                    continue;
                }

                float ng = gv + _g.ArcCost[a] + _g.SegCost[w];
                if (float.IsPositiveInfinity(ng) || (_seen[w] == id && ng >= _gScore[w]))
                {
                    continue;
                }

                _seen[w] = id;
                _gScore[w] = ng;
                _parent[w] = v;
                if (!Push(ng + (H(w, goalPos) * invV), w))
                {
                    LastExpanded = expanded;
                    return SearchStatus.HeapOverflow;
                }
            }
        }

        LastExpanded = expanded;
        return SearchStatus.NoPath;
    }

    private float H(int v, Vec2 goal)
    {
        float dx = _g.EndPos[v].X - goal.X;
        float dz = _g.EndPos[v].Z - goal.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private SearchStatus Reconstruct(int goal, Span<int> path, out int length)
    {
        int len = 0;
        for (int v = goal; v >= 0; v = _parent[v])
        {
            len++;
        }

        length = len;
        if (len > path.Length)
        {
            length = 0;
            return SearchStatus.PathTooLong;
        }

        int k = len;
        for (int v = goal; v >= 0; v = _parent[v])
        {
            path[--k] = v;
        }

        return SearchStatus.Found;
    }

    // Куча: лексикографически по (ключ, DirSeg).
    private static bool Less(float ka, int va, float kb, int vb) => ka < kb || (ka == kb && va < vb);

    private bool Push(float key, int val)
    {
        if (_heapCount >= _heapKey.Length)
        {
            return false;
        }

        int i = _heapCount++;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (!Less(key, val, _heapKey[p], _heapVal[p]))
            {
                break;
            }

            _heapKey[i] = _heapKey[p];
            _heapVal[i] = _heapVal[p];
            i = p;
        }

        _heapKey[i] = key;
        _heapVal[i] = val;
        return true;
    }

    private int Pop()
    {
        int top = _heapVal[0];
        int n = --_heapCount;
        if (n > 0)
        {
            float key = _heapKey[n];
            int val = _heapVal[n];
            int i = 0;
            while (true)
            {
                int c = (2 * i) + 1;
                if (c >= n)
                {
                    break;
                }

                if (c + 1 < n && Less(_heapKey[c + 1], _heapVal[c + 1], _heapKey[c], _heapVal[c]))
                {
                    c++;
                }

                if (!Less(_heapKey[c], _heapVal[c], key, val))
                {
                    break;
                }

                _heapKey[i] = _heapKey[c];
                _heapVal[i] = _heapVal[c];
                i = c;
            }

            _heapKey[i] = key;
            _heapVal[i] = val;
        }

        return top;
    }
}
