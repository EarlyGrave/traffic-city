using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Снапшот пешеходов (фаза 10, pedestrians.md §10.1): двойной буфер <see cref="PedestrianRenderData"/> без копии (как у машин, ADR-0010), длина публикуемого
/// спана - <c>HighWater</c> пула. Позиция - аналитическая функция <c>d = S0 + v·(t - T0)</c> ребра. Вид (<c>SetRenderView</c>): вид «всё» пишет все слоты,
/// прямоугольник - только пешеходов рёбер, чей ограничивающий прямоугольник его пересекает (списки рёбер, очереди переходов и остановок); слоты прошлой
/// записи вне вида обнуляются. Вид на состояние и хэш не влияет.
/// <para>
/// Параллельность (ADR-0017): список слотов собирает главный поток между двумя параллельными заданиями (обнуление прошлого списка, запись нового);
/// каждое пишет только <c>buf[slot]</c> и <c>RenderHint[slot]</c> своих слотов, слот входит в список ровно один раз (пешеход - в одном списке ребра или очереди).
/// </para>
/// </summary>
internal sealed class PedSnapshot
{
    public const float BaseCellM = 200f;
    public const int MaxCells = 1 << 18;
    private const float Pad = 3f;
    private const float RankStepM = 0.6f;

    public readonly PedestrianRenderData[][] Buf;
    public readonly int[] Slots = new int[2];

    private readonly int _capacity;
    private readonly int[]?[] _written = new int[2][];
    private readonly int[] _writtenCount = new int[2];
    private readonly bool[] _writtenAll = new bool[2];
    private readonly PedSnapshotJob _job = new();
    private readonly PedClearJob _clear = new();

    // ---- сетка рёбер вида (производное сети) ----
    private int _builtStamp = -1;
    private float _cellM, _originX, _originZ;
    private int _cellsX, _cellsZ;
    private int[] _cellStart = Array.Empty<int>();
    private int[] _cellEdges = Array.Empty<int>();
    private float[] _bx0 = Array.Empty<float>(), _bz0 = Array.Empty<float>(), _bx1 = Array.Empty<float>(), _bz1 = Array.Empty<float>();
    private int[] _edgeStamp = Array.Empty<int>();
    private int _stamp;

    public PedSnapshot(int capacity)
    {
        _capacity = capacity;
        Buf = new[] { new PedestrianRenderData[capacity], new PedestrianRenderData[capacity] };
    }

    /// <summary>Заполняет <c>Buf[snapTick &amp; 1]</c> состоянием на начало тика <paramref name="snapTick"/> и ставит длину спана.</summary>
    public void Run(World w, long snapTick)
    {
        PedWorld pw = w.Peds!;
        int b = (int)(snapTick & 1);
        PedestrianRenderData[] buf = Buf[b];
        int high = pw.P.HighWater;
        RenderViewState rv = w.RenderView;
        if (rv.All)
        {
            FillRanks(pw);
            _job.Prepare(w, buf, null, snapTick);
            RunJob(w, _job, high);
            _writtenAll[b] = true;
            _writtenCount[b] = 0;
        }
        else
        {
            if (_writtenAll[b])
            {
                _clear.Prepare(buf, null);
                RunJob(w, _clear, Slots[b]);
            }
            else if (_writtenCount[b] > 0)
            {
                _clear.Prepare(buf, _written[b]);
                RunJob(w, _clear, _writtenCount[b]);
            }

            _writtenAll[b] = false;
            _writtenCount[b] = 0;
            int[] list = _written[b] ??= new int[_capacity];
            int n = Gather(w, pw, rv, list);
            _writtenCount[b] = n;
            _job.Prepare(w, buf, list, snapTick);
            RunJob(w, _job, n);
        }

        Slots[b] = high;
    }

    /// <summary>Загрузка: буфер «прошлого» тика = текущему (как у машин).</summary>
    public void CopyToPrevious(long snapTick, int high)
    {
        int b = (int)(snapTick & 1);
        Array.Copy(Buf[b], Buf[b ^ 1], high);
        Slots[b ^ 1] = high;
        _writtenAll[b ^ 1] = true;
        _writtenCount[b ^ 1] = 0;
    }

    private static void RunJob<T>(World w, T job, int count)
        where T : class, IRangeJob
    {
        if (count <= 0)
        {
            return;
        }

        if (w.Pool != null)
        {
            w.Pool.Run(job, count);
        }
        else
        {
            job.RunRange(0, 0, count);
        }
    }

    /// <summary>Номера в очередях переходов и остановок для вида «всё» (обход активных переходов и остановок с очередью).</summary>
    private static void FillRanks(PedWorld pw)
    {
        CrossingState x = pw.X;
        for (int word = 0; word < x.XActive.Length; word++)
        {
            ulong bits = x.XActive[word];
            while (bits != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                RankQueue(pw, x.QHead[(word << 6) + bit]);
            }
        }

        for (int s = 0; s < pw.StopQCount.Length; s++)
        {
            if (pw.StopQCount[s] > 0)
            {
                RankQueue(pw, pw.StopQHead[s]);
            }
        }
    }

    private static void RankQueue(PedWorld pw, int head)
    {
        int rank = 0;
        for (int p = head; p >= 0 && rank <= pw.P.Capacity; p = pw.P.Next[p])
        {
            pw.P.QueueRank[p] = rank++;
        }
    }

    // ------------------------------------------------------------------ вид

    private int Gather(World w, PedWorld pw, RenderViewState rv, int[] list)
    {
        EnsureIndex(pw.Net);
        int n = 0;
        if (_cellsX > 0 && !(rv.X1 < _originX || rv.Z1 < _originZ || rv.X0 > _originX + (_cellsX * _cellM) || rv.Z0 > _originZ + (_cellsZ * _cellM)))
        {
            int cx0 = CellOf(rv.X0, _originX, _cellsX);
            int cx1 = CellOf(rv.X1, _originX, _cellsX);
            int cz0 = CellOf(rv.Z0, _originZ, _cellsZ);
            int cz1 = CellOf(rv.Z1, _originZ, _cellsZ);
            int stamp = ++_stamp;
            PedStore ps = pw.P;
            for (int cz = cz0; cz <= cz1; cz++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int c = (cz * _cellsX) + cx;
                    for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                    {
                        int e = _cellEdges[k];
                        if (_edgeStamp[e] == stamp)
                        {
                            continue;
                        }

                        _edgeStamp[e] = stamp;
                        if (_bx1[e] < rv.X0 || _bx0[e] > rv.X1 || _bz1[e] < rv.Z0 || _bz0[e] > rv.Z1)
                        {
                            continue;
                        }

                        for (int p = ps.EdgeHead[e]; p >= 0; p = ps.EdgeNext[p])
                        {
                            list[n++] = p;
                        }

                        if (pw.Net.Edges[e].Kind >= PedEdgeKind.Crossing)
                        {
                            int x = pw.Net.XOfEdge(e);
                            if ((uint)x < (uint)pw.X.Capacity && pw.X.QCount[x] > 0)
                            {
                                int rank = 0;
                                for (int p = pw.X.QHead[x]; p >= 0 && rank < ps.Capacity; p = ps.Next[p])
                                {
                                    ps.QueueRank[p] = rank++;
                                    list[n++] = p;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Очереди остановок (P4): пишутся, если ребро точки доступа (цель ноги головы очереди) пересекает вид.
        for (int s = 0; s < pw.StopQCount.Length; s++)
        {
            if (pw.StopQCount[s] <= 0)
            {
                continue;
            }

            int ge = pw.P.GoalEdge[pw.StopQHead[s]];
            if ((uint)ge >= (uint)pw.Net.EdgeCount || _bx1[ge] < rv.X0 || _bx0[ge] > rv.X1 || _bz1[ge] < rv.Z0 || _bz0[ge] > rv.Z1)
            {
                continue;
            }

            int rank = 0;
            for (int p = pw.StopQHead[s]; p >= 0 && rank < pw.P.Capacity; p = pw.P.Next[p])
            {
                pw.P.QueueRank[p] = rank++;
                list[n++] = p;
            }
        }

        return n;
    }

    private int CellOf(float c, float origin, int cells)
    {
        float f = MathF.Floor((c - origin) / _cellM);
        return !(f > 0f) ? 0 : (f >= cells ? cells - 1 : (int)f);
    }

    private void EnsureIndex(PedNetwork net)
    {
        if (net.BuildStamp == _builtStamp)
        {
            return;
        }

        _builtStamp = net.BuildStamp;
        int edges = net.EdgeCount;
        if (_bx0.Length < net.Edges.Length)
        {
            int cap = net.Edges.Length;
            _bx0 = new float[cap];
            _bz0 = new float[cap];
            _bx1 = new float[cap];
            _bz1 = new float[cap];
            _edgeStamp = new int[cap];
        }

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int e = 0; e < edges; e++)
        {
            ref readonly PedEdgeData ed = ref net.Edges[e];
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int p = ed.FirstPoint; p < ed.FirstPoint + ed.PointCount; p++)
            {
                Vec2 q = net.Points[p];
                x0 = MathF.Min(x0, q.X);
                x1 = MathF.Max(x1, q.X);
                z0 = MathF.Min(z0, q.Z);
                z1 = MathF.Max(z1, q.Z);
            }

            _bx0[e] = x0 - Pad;
            _bz0[e] = z0 - Pad;
            _bx1[e] = x1 + Pad;
            _bz1[e] = z1 + Pad;
            minX = MathF.Min(minX, _bx0[e]);
            minZ = MathF.Min(minZ, _bz0[e]);
            maxX = MathF.Max(maxX, _bx1[e]);
            maxZ = MathF.Max(maxZ, _bz1[e]);
        }

        if (edges == 0)
        {
            _cellsX = _cellsZ = 0;
            return;
        }

        float cellM = BaseCellM;
        int nx, nz;
        while (true)
        {
            nx = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling((maxX - minX) / cellM)));
            nz = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling((maxZ - minZ) / cellM)));
            if ((long)nx * nz <= MaxCells)
            {
                break;
            }

            cellM *= 2f;
        }

        _cellM = cellM;
        _originX = minX;
        _originZ = minZ;
        _cellsX = nx;
        _cellsZ = nz;
        int cells = nx * nz;
        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
        }
        else
        {
            Array.Clear(_cellStart, 0, cells + 1);
        }

        long total = 0;
        for (int e = 0; e < edges; e++)
        {
            CellRange(e, out int a0, out int b0, out int a1, out int b1);
            for (int cz = b0; cz <= b1; cz++)
            {
                for (int cx = a0; cx <= a1; cx++)
                {
                    _cellStart[(cz * nx) + cx + 1]++;
                    total++;
                }
            }
        }

        for (int c = 0; c < cells; c++)
        {
            _cellStart[c + 1] += _cellStart[c];
        }

        if (_cellEdges.Length < total)
        {
            _cellEdges = new int[total];
        }

        for (int e = 0; e < edges; e++)
        {
            CellRange(e, out int a0, out int b0, out int a1, out int b1);
            for (int cz = b0; cz <= b1; cz++)
            {
                for (int cx = a0; cx <= a1; cx++)
                {
                    _cellEdges[_cellStart[(cz * nx) + cx]++] = e;
                }
            }
        }

        for (int c = cells; c > 0; c--)
        {
            _cellStart[c] = _cellStart[c - 1];
        }

        _cellStart[0] = 0;
    }

    private void CellRange(int edge, out int cx0, out int cz0, out int cx1, out int cz1)
    {
        cx0 = CellOf(_bx0[edge], _originX, _cellsX);
        cx1 = CellOf(_bx1[edge], _originX, _cellsX);
        cz0 = CellOf(_bz0[edge], _originZ, _cellsZ);
        cz1 = CellOf(_bz1[edge], _originZ, _cellsZ);
    }

    // ------------------------------------------------------------------ позиция одного пешехода

    /// <summary>Заполняет запись слота <paramref name="p"/>; false (запись нулевая) - пешехода нет в виде/снапшоте (мёртв, <c>PendingPath</c>, <c>OnBus</c>).</summary>
    internal static void Slot(World w, PedWorld pw, int p, long snapTick, ref PedestrianRenderData r)
    {
        PedStore ps = pw.P;
        if (!ps.Pool.IsAlive(p))
        {
            if (r.Flags != 0)
            {
                r = default;
            }

            return;
        }

        PedNetwork net = pw.Net;
        var st = (PedState)ps.State[p];
        float x, z, heading;
        int hint = ps.RenderHint[p];
        switch (st)
        {
            case PedState.Walking:
            case PedState.Crossing:
                {
                    int step = ps.Edge[p];
                    int e = step & PedStore.StepMask;
                    bool bwd = (step & PedStore.StepBwd) != 0;
                    int len = net.Edges[e].LenMm;
                    long d = ps.S0Mm[p] + ((long)ps.VMmT[p] * (snapTick - ps.T0[p]));
                    int end = pw.EndMm(p, step);
                    int dist = (int)Math.Clamp(d, 0L, Math.Max(0, end));
                    net.Evaluate(e, bwd ? len - dist : dist, bwd, ref hint, out x, out z, out heading);
                    break;
                }

            case PedState.AtCrossing:
                {
                    int step = ps.Edge[p];
                    int e = step & PedStore.StepMask;
                    bool bwd = (step & PedStore.StepBwd) != 0;
                    net.Evaluate(e, bwd ? net.Edges[e].LenMm : 0, bwd, ref hint, out x, out z, out heading);
                    Queued(ps.QueueRank[p], heading, ref x, ref z);
                    break;
                }

            case PedState.AtStop:
                {
                    // Точка доступа остановки - цель ноги (GoalEdge, GoalSMm) на тротуаре.
                    int ge = ps.GoalEdge[p];
                    if ((uint)ge >= (uint)net.EdgeCount)
                    {
                        goto default;
                    }

                    net.Evaluate(ge, ps.GoalSMm[p], false, ref hint, out x, out z, out heading);
                    Queued(ps.QueueRank[p], heading, ref x, ref z);
                    break;
                }

            default:
                if (r.Flags != 0)
                {
                    r = default;
                }

                return;
        }

        ps.RenderHint[p] = hint;
        r.X = x;
        r.Y = 0f;
        r.Z = z;
        r.Heading = heading;
        r.Index = p;
        r.Generation = (ushort)ps.Pool.GenerationOf(p);
        r.State = (byte)st;
        r.Flags = (byte)(PedRenderFlags.Active | (ps.Leg[p] != (byte)PedLeg.Direct ? PedRenderFlags.Passenger : 0));
    }

    /// <summary>Место в очереди у бордюра: 3 в ряд, ряд назад на 0.6 м (pedestrians.md §4.2).</summary>
    private static void Queued(int rank, float heading, ref float x, ref float z)
    {
        int row = rank / 3;
        int col = (rank % 3) - 1;
        float dx = MathF.Cos(heading);
        float dz = MathF.Sin(heading);
        x += (-dx * RankStepM * row) + (-dz * RankStepM * col);
        z += (-dz * RankStepM * row) + (dx * RankStepM * col);
    }
}

/// <summary>Задание фазы 10 для <see cref="WorkerPool"/>: пишет только <c>buf[slot]</c> и <c>RenderHint[slot]</c> своих слотов.</summary>
internal sealed class PedSnapshotJob : IRangeJob
{
    private World _w = null!;
    private PedWorld _pw = null!;
    private PedestrianRenderData[] _buf = Array.Empty<PedestrianRenderData>();
    private int[]? _list;
    private long _snapTick;

    public void Prepare(World w, PedestrianRenderData[] buf, int[]? list, long snapTick)
    {
        _w = w;
        _pw = w.Peds!;
        _buf = buf;
        _list = list;
        _snapTick = snapTick;
    }

    public void RunRange(int worker, int begin, int end)
    {
        int[]? list = _list;
        for (int k = begin; k < end; k++)
        {
            int p = list == null ? k : list[k];
            PedSnapshot.Slot(_w, _pw, p, _snapTick, ref _buf[p]);
        }
    }
}

/// <summary>Обнуление ранее записанных слотов буфера пешеходов (диапазон или список).</summary>
internal sealed class PedClearJob : IRangeJob
{
    private PedestrianRenderData[] _buf = Array.Empty<PedestrianRenderData>();
    private int[]? _list;

    public void Prepare(PedestrianRenderData[] buf, int[]? list)
    {
        _buf = buf;
        _list = list;
    }

    public void RunRange(int worker, int begin, int end)
    {
        int[]? list = _list;
        for (int k = begin; k < end; k++)
        {
            int p = list == null ? k : list[k];
            if (_buf[p].Flags != 0)
            {
                _buf[p] = default;
            }
        }
    }
}
