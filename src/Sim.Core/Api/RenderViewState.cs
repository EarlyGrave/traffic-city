using System;
using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Api;

/// <summary>
/// Вид для снапшота (<c>SetRenderView</c>, ADR-0019 п. 1, sim-lod.md 13.7 A1): презентация, в хэш не входит, на состояние не влияет.
/// Хранит прямоугольник вида, равномерную сетку полос (CSR, не зависит от <c>Lod.Mode</c>) и по каждому из двух буферов снапшота список слотов,
/// записанных в нём в прошлый раз (для обнуления). Все массивы выделяются лениво при первом непустом виде и растут только вместе с сетью
/// (<c>BuildRoad</c>); в стационарном состоянии <c>Step</c> ничего не аллоцирует.
/// <para>
/// Параллельность (ADR-0017): список записанных слотов собирает <b>один</b> поток (главный) между двумя параллельными заданиями - «обнулить слоты
/// прошлого списка» и «записать слоты нового списка». Каждое задание пишет только <c>buf[slot]</c> (и <c>PointHint[slot]</c>) своих элементов списка;
/// слот попадает в список ровно один раз (по основной полосе <c>Lane[i]</c>, ghost пропускается), поэтому записи воркеров не пересекаются; общих
/// счётчиков и списков воркеры не трогают.
/// </para>
/// </summary>
internal sealed class RenderViewState
{
    /// <summary>Сторона ячейки сетки вида, м (удваивается, пока ячеек больше <see cref="MaxCells"/>).</summary>
    public const float BaseCellM = 200f;
    public const int MaxCells = 1 << 18;

    /// <summary>Запас вокруг ограничивающего прямоугольника полосы: боковое смещение машины и округления.</summary>
    private const float Pad = 4f;

    /// <summary>true = вид «всё» (по умолчанию): фаза 10 пишет все слоты [0, HighWater).</summary>
    public bool All = true;

    public float X0, Z0, X1, Z1;

    /// <summary>Растёт при каждом <see cref="Set"/>/<see cref="SetAll"/> (клиент сверяет, какой вид отражён в кадре); на состояние не влияет.</summary>
    public int Version;

    // ---- сетка полос (производное сети, вне хэша) ----
    private int _lanesIndexed = -1;
    private int _indexVersion = -1;   // версия сети индекса: после правок (дыры) число полос не показывает изменения
    private float _cellM, _originX, _originZ;
    private int _cellsX, _cellsZ;
    private int[] _cellStart = Array.Empty<int>();   // [cells + 1] CSR
    private int[] _cellLanes = Array.Empty<int>();
    private float[] _bx0 = Array.Empty<float>(), _bz0 = Array.Empty<float>(), _bx1 = Array.Empty<float>(), _bz1 = Array.Empty<float>();
    private int[] _laneStamp = Array.Empty<int>();
    private int _stamp;

    // ---- слоты, записанные в буфер b (0/1) в прошлый раз ----
    private readonly int[]?[] _written = new int[2][];
    private readonly int[] _writtenCount = new int[2];
    private readonly bool[] _writtenAll = new bool[2]; // true: буфер мог быть записан по всему префиксу [0, SnapshotSlots[b]) (вид «всё»)

    /// <summary>Задаёт вид. Нечисловые координаты дают вид «всё»; границы упорядочиваются.</summary>
    public void Set(float x0, float z0, float x1, float z1)
    {
        Version++;
        if (!float.IsFinite(x0) || !float.IsFinite(z0) || !float.IsFinite(x1) || !float.IsFinite(z1))
        {
            All = true;
            return;
        }

        All = false;
        X0 = MathF.Min(x0, x1);
        X1 = MathF.Max(x0, x1);
        Z0 = MathF.Min(z0, z1);
        Z1 = MathF.Max(z0, z1);
    }

    public void SetAll()
    {
        Version++;
        All = true;
    }

    /// <summary>Вид «всё» записал буфер <paramref name="b"/> целиком (префикс до <c>SnapshotSlots</c>): обнулять придётся диапазоном.</summary>
    public void MarkWrittenAll(int b)
    {
        _writtenAll[b] = true;
        _writtenCount[b] = 0;
    }

    /// <summary>Фаза 10 для вида, не равного «всё»: обнуляет слоты прошлой записи буфера, собирает и пишет слоты вида.</summary>
    public void Run(World w, VehicleRenderData[] buf, int b, int high)
    {
        // 1. Обнуление прошлой записи (параллельно, диапазон слотов или список).
        SnapshotClearJob clear = w.SnapshotClearJob;
        if (_writtenAll[b])
        {
            clear.Prepare(buf, null);
            RunJob(w, clear, w.SnapshotSlots[b]);
        }
        else if (_writtenCount[b] > 0)
        {
            clear.Prepare(buf, _written[b]);
            RunJob(w, clear, _writtenCount[b]);
        }

        _writtenAll[b] = false;
        _writtenCount[b] = 0;

        // 2. Сбор слотов вида (главный поток; серийно, но O(машин вида)).
        int[] list = _written[b] ??= new int[w.Vehicles.Capacity];
        int n = Gather(w, list);
        _writtenCount[b] = n;

        // 3. Запись (параллельно, по списку).
        SnapshotJob job = w.SnapshotJob;
        job.Prepare(w, buf, list);
        RunJob(w, job, n);
        w.Work.SnapshotSlots += n;
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

    /// <summary>Слоты машин (по основной полосе) полос, чей ограничивающий прямоугольник пересекает вид. Возвращает число слотов.</summary>
    private int Gather(World w, int[] list)
    {
        EnsureIndex(w.Net);
        if (_cellsX == 0 || X1 < _originX || Z1 < _originZ
            || X0 > _originX + (_cellsX * _cellM) || Z0 > _originZ + (_cellsZ * _cellM))
        {
            return 0;
        }

        int cx0 = CellOf(X0, _originX, _cellsX);
        int cx1 = CellOf(X1, _originX, _cellsX);
        int cz0 = CellOf(Z0, _originZ, _cellsZ);
        int cz1 = CellOf(Z1, _originZ, _cellsZ);
        int stamp = ++_stamp;
        VehicleStore v = w.Vehicles;
        int[] head = w.Occupancy.LaneHead;
        int n = 0;
        for (int cz = cz0; cz <= cz1; cz++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int c = (cz * _cellsX) + cx;
                for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    int lane = _cellLanes[k];
                    if (_laneStamp[lane] == stamp)
                    {
                        continue;
                    }

                    _laneStamp[lane] = stamp;
                    if (_bx1[lane] < X0 || _bx0[lane] > X1 || _bz1[lane] < Z0 || _bz0[lane] > Z1)
                    {
                        continue;
                    }

                    w.Queue?.ViewLane(lane, w.Tick + 1);   // Q-meso: синтез позиций очереди полосы на начало следующего тика

                    // От головы к хвосту; ghost-член чужой полосы пропускаем (его пишет основная полоса), цепочку ведём по роли в этой полосе.
                    for (int i = head[lane]; i != -1;)
                    {
                        if (v.Lane[i] == lane)
                        {
                            list[n++] = i;
                            i = v.Behind[i];
                        }
                        else
                        {
                            i = v.GhostBehind[i];
                        }
                    }
                }
            }
        }

        return n;
    }

    private int CellOf(float c, float origin, int cells)
    {
        float f = MathF.Floor((c - origin) / _cellM);
        return !(f > 0f) ? 0 : (f >= cells ? cells - 1 : (int)f);
    }

    private void EnsureIndex(NetworkStore net)
    {
        if (net.LaneCount == _lanesIndexed && (net.ToolsTouched == 0 || net.Version == _indexVersion))
        {
            return;
        }

        _indexVersion = net.Version;

        int lanes = net.LaneCount;
        if (_bx0.Length < lanes)
        {
            int cap = net.Lanes.Length;
            _bx0 = new float[cap];
            _bz0 = new float[cap];
            _bx1 = new float[cap];
            _bz1 = new float[cap];
            _laneStamp = new int[cap];
        }

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int l = 0; l < lanes; l++)
        {
            ref readonly LaneData ld = ref net.Lanes[l];
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int p = ld.FirstPoint; p < ld.FirstPoint + ld.PointCount; p++)
            {
                Vec2 q = net.LanePoints[p];
                x0 = MathF.Min(x0, q.X);
                x1 = MathF.Max(x1, q.X);
                z0 = MathF.Min(z0, q.Z);
                z1 = MathF.Max(z1, q.Z);
            }

            _bx0[l] = x0 - Pad;
            _bz0[l] = z0 - Pad;
            _bx1[l] = x1 + Pad;
            _bz1[l] = z1 + Pad;
            minX = MathF.Min(minX, _bx0[l]);
            minZ = MathF.Min(minZ, _bz0[l]);
            maxX = MathF.Max(maxX, _bx1[l]);
            maxZ = MathF.Max(maxZ, _bz1[l]);
        }

        _lanesIndexed = lanes;
        if (lanes == 0)
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

        // Два прохода: подсчёт вхождений полос в ячейки, затем заполнение.
        long total = 0;
        for (int l = 0; l < lanes; l++)
        {
            CellRange(l, out int a0, out int b0, out int a1, out int b1);
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

        if (_cellLanes.Length < total)
        {
            _cellLanes = new int[total];
        }

        // Курсоры - в самом массиве начал (после прохода Start[c] = конец ячейки c), затем сдвиг назад.
        for (int l = 0; l < lanes; l++)
        {
            CellRange(l, out int a0, out int b0, out int a1, out int b1);
            for (int cz = b0; cz <= b1; cz++)
            {
                for (int cx = a0; cx <= a1; cx++)
                {
                    _cellLanes[_cellStart[(cz * nx) + cx]++] = l;
                }
            }
        }

        for (int c = cells; c > 0; c--)
        {
            _cellStart[c] = _cellStart[c - 1];
        }

        _cellStart[0] = 0;
    }

    private void CellRange(int lane, out int cx0, out int cz0, out int cx1, out int cz1)
    {
        cx0 = CellOf(_bx0[lane], _originX, _cellsX);
        cx1 = CellOf(_bx1[lane], _originX, _cellsX);
        cz0 = CellOf(_bz0[lane], _originZ, _cellsZ);
        cz1 = CellOf(_bz1[lane], _originZ, _cellsZ);
    }
}
