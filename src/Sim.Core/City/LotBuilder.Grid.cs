using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Участки на мировой сетке 8 м (ADR-0030, road-profiles.md §4.2, RP5): режим профилей. Целочисленная маска «дорога» (16 точек на клетку, <c>int64</c>, без <c>sqrt</c>),
/// цепочка клеток фасада с шагом 4 м, прямоугольники <c>F x D</c> клеток, ось глубины <c>+-x/+-z</c>. Участок хранит клетки (<c>CellX/CellZ/CellsF/CellsD/Orient</c>) как состояние:
/// правка дорог его не двигает, клетки под дорогой переводят его в <c>Lost</c> (<see cref="OnRoadsChanged"/>). Производные (центр, курс, точки доступа) считает <see cref="Derive"/>.
/// Сетка занятости: бит 1 - участок, бит 2 - дорога по маске; в legacy используется только бит 1.
/// </summary>
internal sealed partial class LotBuilder
{
    /// <summary>Порог «клетка свободна» (<c>zoning.cell_min_share_pct</c>, решение владельца 2026-10-27): меньше этой доли свободной площади - клетка считается дорогой. Константа ядра: параметр сейва потребует world_version.</summary>
    public const int CellMinSharePct = 75;

    private const int ChainStepMm = 4000;        // шаг цепочки фасада по оси сегмента
    private const int ChainGapMm = 4000;         // точка цепочки - на EdgeMm + 4 м от оси
    private const int MaxFrontCells = 8;
    private const int MaxDepthCells = 6;
    private const int RowSpreadMax = 1;          // разброс рядов фасада в участке (ступенька «уступ»)
    private const int PointsPerCell = 16;        // 4 x 4
    private const byte BitLot = 1;
    private const byte BitRoad = 2;
    private const int SubPieceMm = 16_000;

    private static readonly int[] _dirX = { 1, -1, 0, 0 };
    private static readonly int[] _dirZ = { 0, 0, 1, -1 };

    private readonly bool _grid;
    private readonly long _modMm;
    private readonly long _gridOx;               // начало мировой сетки, мм
    private readonly long _gridOz;
    private readonly int _gx0;                   // индекс клетки мира в начале массива занятости
    private readonly int _gz0;
    private readonly int _frontMax;
    private readonly int _depthMax;
    private readonly ushort[]? _pts;             // [_nx * _nz] биты занятых точек клетки

    // цепочка текущего участка (scratch)
    private bool _open;
    private int _axis;
    private int _count;
    private int _f0;
    private int _lastF;
    private int _fDir;
    private long _sFirst;
    private long _sLast;
    private readonly int[] _rows = new int[MaxFrontCells];

    /// <summary>true - участки на мировой сетке (задан каталог профилей).</summary>
    public bool GridMode => _grid;

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        return ((a % b) != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    private static long RoundMm(float m) => DetMath.ToLongSat(MathF.Round(m * 1000f));

    private static int AxisOf(float nx, float nz)
    {
        if (MathF.Abs(nx) > MathF.Abs(nz))
        {
            return nx >= 0f ? 0 : 1;
        }

        return nz >= 0f ? 2 : 3;
    }

    private static int EdgeMmOf(World w, in SegmentData sd)
    {
        if (w.Net.Profiles != null && sd.Profile >= 0)
        {
            return w.Net.Profiles.EdgeMm[sd.Profile];
        }

        return DetMath.ToIntSat(RoundMm(FrontOffsetM(w, in sd)));
    }

    private int Idx(int cx, int cz)
    {
        long ix = (long)cx - _gx0;
        long iz = (long)cz - _gz0;
        if (ix < 0 || iz < 0 || ix >= _nx || iz >= _nz)
        {
            return -1;
        }

        return (int)((iz * _nx) + ix);
    }

    /// <summary>Клетка вне массива считается занятой: участки на краю города не нарезаются.</summary>
    private bool IsRoadCell(int cx, int cz)
    {
        int i = Idx(cx, cz);
        return i < 0 || (_occ[i] & BitRoad) != 0;
    }

    private bool IsFreeCell(int cx, int cz)
    {
        int i = Idx(cx, cz);
        return i >= 0 && _occ[i] == 0;
    }

    // ---- маска дороги ----

    private void BuildRoadMask(World w)
    {
        ushort[] pts = _pts!;
        Array.Clear(pts);
        NetworkStore net = w.Net;
        for (int seg = 0; seg < net.SegmentCount; seg++)
        {
            if (!net.SegmentAlive(seg))
            {
                continue;
            }

            ref readonly SegmentData sd = ref net.Segments[seg];
            long e = EdgeMmOf(w, in sd);
            for (int i = 0; i + 1 < sd.PointCount; i++)
            {
                Vec2 a = net.SegmentPoints[sd.FirstPoint + i];
                Vec2 b = net.SegmentPoints[sd.FirstPoint + i + 1];
                MarkPiece(pts, RoundMm(a.X), RoundMm(a.Z), RoundMm(b.X), RoundMm(b.Z), e);
            }
        }

        for (int n = 0; n < net.NodeCount; n++)
        {
            if (!net.NodeAlive(n))
            {
                continue;
            }

            ref readonly NodeData nd = ref net.Nodes[n];
            long r = RoundMm(nd.Radius);
            for (int k = 0; k < nd.ArmCount; k++)
            {
                int seg = net.NodeArms[nd.FirstArm + k];
                r = Math.Max(r, EdgeMmOf(w, in net.Segments[seg]));
            }

            MarkDisk(pts, RoundMm(nd.Position.X), RoundMm(nd.Position.Z), r);
        }

        int minFree = (CellMinSharePct * PointsPerCell) + 99;
        minFree /= 100;   // free * 100 >= pct * 16  <=>  free >= ceil(pct * 16 / 100)
        for (int i = 0; i < pts.Length; i++)
        {
            int free = PointsPerCell - System.Numerics.BitOperations.PopCount(pts[i]);
            if (free < minFree)
            {
                _occ[i] |= BitRoad;
            }
        }
    }

    private void CellRangeMm(long x0, long z0, long x1, long z1, out int cx0, out int cz0, out int cx1, out int cz1)
    {
        cx0 = (int)Math.Max(_gx0, FloorDiv(x0 - _gridOx, _modMm));
        cz0 = (int)Math.Max(_gz0, FloorDiv(z0 - _gridOz, _modMm));
        cx1 = (int)Math.Min(_gx0 + _nx - 1, FloorDiv(x1 - _gridOx, _modMm));
        cz1 = (int)Math.Min(_gz0 + _nz - 1, FloorDiv(z1 - _gridOz, _modMm));
    }

    private (long X, long Z) SamplePoint(int cx, int cz, int k)
    {
        int i = k & 3;
        int j = k >> 2;
        return (_gridOx + (cx * _modMm) + ((((2 * i) + 1) * _modMm) / 8), _gridOz + (cz * _modMm) + ((((2 * j) + 1) * _modMm) / 8));
    }

    private void MarkDisk(ushort[] pts, long x, long z, long r)
    {
        CellRangeMm(x - r, z - r, x + r, z + r, out int cx0, out int cz0, out int cx1, out int cz1);
        long r2 = r * r;
        for (int cz = cz0; cz <= cz1; cz++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int idx = Idx(cx, cz);
                for (int k = 0; k < PointsPerCell; k++)
                {
                    (long px, long pz) = SamplePoint(cx, cz, k);
                    long dx = px - x;
                    long dz = pz - z;
                    if ((dx * dx) + (dz * dz) <= r2)
                    {
                        pts[idx] |= (ushort)(1 << k);
                    }
                }
            }
        }
    }

    /// <summary>Отрезок A-B режется на куски по 16 м: для каждого перебираются только близкие клетки, расстояние считается до всего отрезка.</summary>
    private void MarkPiece(ushort[] pts, long ax, long az, long bx, long bz, long e)
    {
        long dx = bx - ax;
        long dz = bz - az;
        long len2 = (dx * dx) + (dz * dz);
        long lenApprox = Math.Abs(dx) + Math.Abs(dz);
        long n = Math.Max(1, (lenApprox + SubPieceMm - 1) / SubPieceMm);
        for (long k = 0; k < n; k++)
        {
            long sx0 = ax + (dx * k / n);
            long sz0 = az + (dz * k / n);
            long sx1 = ax + (dx * (k + 1) / n);
            long sz1 = az + (dz * (k + 1) / n);
            CellRangeMm(Math.Min(sx0, sx1) - e, Math.Min(sz0, sz1) - e, Math.Max(sx0, sx1) + e, Math.Max(sz0, sz1) + e, out int cx0, out int cz0, out int cx1, out int cz1);
            for (int cz = cz0; cz <= cz1; cz++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int idx = Idx(cx, cz);
                    for (int p = 0; p < PointsPerCell; p++)
                    {
                        if ((pts[idx] & (1 << p)) != 0)
                        {
                            continue;
                        }

                        (long px, long pz) = SamplePoint(cx, cz, p);
                        if (WithinEdge(px - ax, pz - az, dx, dz, len2, e))
                        {
                            pts[idx] |= (ushort)(1 << p);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Расстояние от точки (относительно A) до отрезка (A -> A + d) не больше <paramref name="e"/>; целые, без sqrt; перпендикуляр сравнивается в 128 битах.</summary>
    private static bool WithinEdge(long rx, long rz, long dx, long dz, long len2, long e)
    {
        long e2 = e * e;
        if (len2 == 0)
        {
            return (rx * rx) + (rz * rz) <= e2;
        }

        long dot = (rx * dx) + (rz * dz);
        if (dot <= 0)
        {
            return (rx * rx) + (rz * rz) <= e2;
        }

        if (dot >= len2)
        {
            long qx = rx - dx;
            long qz = rz - dz;
            return (qx * qx) + (qz * qz) <= e2;
        }

        long cross = (dx * rz) - (dz * rx);
        return (Int128)cross * cross <= (Int128)e2 * len2;
    }

    // ---- геометрия клеток участка ----

    /// <summary>Границы клеток участка (включительно) по мировой сетке.</summary>
    internal static void CellBounds(LotStore l, int lot, out int x0, out int z0, out int x1, out int z1)
    {
        int o = l.Orient[lot];
        int dx = _dirX[o];
        int dz = _dirZ[o];
        int lx = dz;      // «влево» от оси глубины: (dz, -dx)
        int lz = -dx;
        int f = l.CellsF[lot] - 1;
        int d = l.CellsD[lot] - 1;
        int ax = l.CellX[lot];
        int az = l.CellZ[lot];
        int bx = ax + (dx * d) - (lx * f);
        int bz = az + (dz * d) - (lz * f);
        x0 = Math.Min(ax, bx);
        x1 = Math.Max(ax, bx);
        z0 = Math.Min(az, bz);
        z1 = Math.Max(az, bz);
    }

    /// <summary>Центр прямоугольника участка, м, и единичная ось глубины (от дороги наружу).</summary>
    private void GridCenter(LotStore l, int lot, out float cx, out float cz, out int dirX, out int dirZ)
    {
        CellBounds(l, lot, out int x0, out int z0, out int x1, out int z1);
        cx = (_gridOx + ((x0 + x1 + 1) * _modMm / 2)) * 0.001f;
        cz = (_gridOz + ((z0 + z1 + 1) * _modMm / 2)) * 0.001f;
        dirX = _dirX[l.Orient[lot]];
        dirZ = _dirZ[l.Orient[lot]];
    }

    private void MarkLotCells(LotStore l, int lot)
    {
        CellBounds(l, lot, out int x0, out int z0, out int x1, out int z1);
        for (int cz = z0; cz <= z1; cz++)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                int i = Idx(cx, cz);
                if (i >= 0)
                {
                    _occ[i] |= BitLot;
                }
            }
        }
    }

    private bool AnyRoadCell(LotStore l, int lot)
    {
        CellBounds(l, lot, out int x0, out int z0, out int x1, out int z1);
        for (int cz = z0; cz <= z1; cz++)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                if (IsRoadCell(cx, cz))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ---- нарезка ----

    private bool AppendSegmentGrid(World w, CityWorld c, int seg, bool derive)
    {
        int[]? labels = null;
        ref readonly SegmentData sd = ref w.Net.Segments[seg];
        long lengthMm = RoundMm(sd.Length);
        long clear = c.Cfg.Zoning.NodeClearMm;
        long edge = EdgeMmOf(w, in sd);
        for (int side = 0; side < 2; side++)
        {
            _open = false;
            for (long s = clear; s <= lengthMm - clear; s += ChainStepMm)
            {
                PointAt(w.Net, in sd, s * 0.001f, out float px, out float pz, out float tx, out float tz);
                float nx = side == 0 ? -tz : tz;
                float nz = side == 0 ? tx : -tx;
                long off = edge + ChainGapMm;
                long qx = RoundMm(px) + RoundMm(nx * off * 0.001f);
                long qz = RoundMm(pz) + RoundMm(nz * off * 0.001f);
                int cx = (int)FloorDiv(qx - _gridOx, _modMm);
                int cz = (int)FloorDiv(qz - _gridOz, _modMm);
                int ptAxis = AxisOf(nx, nz);

                if (_open)
                {
                    if (!Place(ref cx, ref cz, _axis))
                    {
                        if (!EmitLot(w, c, seg, side, lengthMm, derive, ref labels))
                        {
                            return false;
                        }

                        continue;
                    }

                    if (TryExtend(cx, cz, s))
                    {
                        continue;
                    }

                    if (!EmitLot(w, c, seg, side, lengthMm, derive, ref labels))
                    {
                        return false;
                    }

                    // клетка пересчитывается с осью нового участка
                    cx = (int)FloorDiv(qx - _gridOx, _modMm);
                    cz = (int)FloorDiv(qz - _gridOz, _modMm);
                }

                if (Place(ref cx, ref cz, ptAxis))
                {
                    StartLot(cx, cz, ptAxis, s);
                }
            }

            if (_open && !EmitLot(w, c, seg, side, lengthMm, derive, ref labels))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Клетка точки цепочки: если это дорога - одна клетка наружу по оси; снова дорога - точки нет.</summary>
    private bool Place(ref int cx, ref int cz, int axis)
    {
        if (!IsRoadCell(cx, cz))
        {
            return true;
        }

        cx += _dirX[axis];
        cz += _dirZ[axis];
        return !IsRoadCell(cx, cz);
    }

    private static int RowOf(int axis, int cx, int cz) => axis < 2 ? cx : cz;

    private static int FrontOf(int axis, int cx, int cz) => axis < 2 ? cz : cx;

    private void StartLot(int cx, int cz, int axis, long s)
    {
        _open = true;
        _axis = axis;
        _count = 1;
        _f0 = FrontOf(axis, cx, cz);
        _lastF = _f0;
        _fDir = 0;
        _rows[0] = RowOf(axis, cx, cz);
        _sFirst = s;
        _sLast = s;
    }

    private int Outward(int row) => (_axis & 1) == 0 ? row : -row;

    /// <summary>Клетка цепочки в тот же участок: тот же фасадный индекс (ряд берётся дальний) или соседний в одном направлении; разброс рядов не больше <see cref="RowSpreadMax"/>, длина не больше <c>F</c>.</summary>
    private bool TryExtend(int cx, int cz, long s)
    {
        int f = FrontOf(_axis, cx, cz);
        int r = RowOf(_axis, cx, cz);
        int df = f - _lastF;
        bool same = df == 0;
        if (!same && (Math.Abs(df) != 1 || (_fDir != 0 && df != _fDir) || _count >= _frontMax))
        {
            return false;
        }

        int newOut = Outward(r);
        int lastOut = Outward(_rows[_count - 1]);
        int mergedLast = same ? Math.Max(lastOut, newOut) : lastOut;
        int lo = mergedLast;
        int hi = mergedLast;
        for (int i = 0; i < _count - 1; i++)
        {
            int o = Outward(_rows[i]);
            lo = Math.Min(lo, o);
            hi = Math.Max(hi, o);
        }

        if (!same)
        {
            lo = Math.Min(lo, newOut);
            hi = Math.Max(hi, newOut);
        }

        if (hi - lo > RowSpreadMax)
        {
            return false;
        }

        if (same)
        {
            _rows[_count - 1] = (_axis & 1) == 0 ? mergedLast : -mergedLast;
        }
        else
        {
            _rows[_count++] = r;
            _fDir = df;
            _lastF = f;
        }

        _sLast = s;
        return true;
    }

    private int FitDepth(int axis, int row, int fa, int fb)
    {
        int sign = (axis & 1) == 0 ? 1 : -1;
        int k = 0;
        for (; k < _depthMax; k++)
        {
            int rr = row + (sign * k);
            for (int f = fa; f <= fb; f++)
            {
                int cx = axis < 2 ? rr : f;
                int cz = axis < 2 ? f : rr;
                if (!IsFreeCell(cx, cz))
                {
                    return k;
                }
            }
        }

        return k;
    }

    private bool EmitLot(World w, CityWorld c, int seg, int side, long lengthMm, bool derive, ref int[]? labels)
    {
        _open = false;
        int axis = _axis;
        int cnt = _count;
        int fa = Math.Min(_f0, _lastF);
        int fb = Math.Max(_f0, _lastF);
        int bestOut = Outward(_rows[0]);
        for (int i = 1; i < cnt; i++)
        {
            bestOut = Math.Max(bestOut, Outward(_rows[i]));
        }

        int frontRow = (axis & 1) == 0 ? bestOut : -bestOut;
        int total = fb - fa + 1;

        // полный фасад, затем срезы с хвоста и с начала: лучшая первая находка, глубина не меньше 2 клеток
        int pa = 0;
        int pb = 0;
        int depth = 0;
        for (int trim = 0; trim <= total - 2 && depth < 2; trim++)
        {
            for (int ts = 0; ts <= trim; ts++)
            {
                int a = fa + ts;
                int b = fb - (trim - ts);
                int dd = FitDepth(axis, frontRow, a, b);
                if (dd >= 2)
                {
                    pa = a;
                    pb = b;
                    depth = dd;
                    break;
                }
            }
        }

        if (depth < 2)
        {
            return true;
        }

        int fcount = pb - pa + 1;
        int dx = _dirX[axis];
        int dz = _dirZ[axis];
        int lx = dz;
        int lz = -dx;
        int fl = (axis < 2 ? lz : lx) > 0 ? pb : pa;   // фасадный индекс левой клетки переднего ряда
        int cellX = axis < 2 ? frontRow : fl;
        int cellZ = axis < 2 ? fl : frontRow;

        // центр переднего ряда -> проекция на ось сегмента
        long frontMidF = ((long)(pa + pb + 1)) * _modMm / 2;
        long frontMidR = ((2L * frontRow) + 1) * _modMm / 2;
        long fx = _gridOx + (axis < 2 ? frontMidR : frontMidF);
        long fz = _gridOz + (axis < 2 ? frontMidF : frontMidR);
        long sc = ProjectMm(w.Net, in w.Net.Segments[seg], fx * 0.001f, fz * 0.001f, lengthMm);
        long extent = ((_sLast - _sFirst) + ChainStepMm) * fcount / Math.Max(1, total);
        long h = Math.Min(extent / 2, Math.Min(sc, lengthMm - sc));
        if (h < 500)
        {
            return true;
        }

        // участок в парке/воде не нарезается (как в legacy: по центру)
        long x0 = _gridOx + (((long)Math.Min(cellX, cellX + (dx * (depth - 1)) - (lx * (fcount - 1)))) * _modMm);
        long z0 = _gridOz + (((long)Math.Min(cellZ, cellZ + (dz * (depth - 1)) - (lz * (fcount - 1)))) * _modMm);
        float widthM = (axis < 2 ? depth : fcount) * _modMm * 0.001f;
        float heightM = (axis < 2 ? fcount : depth) * _modMm * 0.001f;
        if (InLandUse(w, (x0 * 0.001f) + (widthM * 0.5f), (z0 * 0.001f) + (heightM * 0.5f)))
        {
            return true;
        }

        LotStore l = c.Lots;
        if (!l.TryAdd(seg, w.Net.Segments[seg].Generation, (byte)side, (int)(sc - h), (int)(sc + h), out int lot))
        {
            return false;
        }

        l.CellX[lot] = cellX;
        l.CellZ[lot] = cellZ;
        l.CellsF[lot] = (byte)fcount;
        l.CellsD[lot] = (byte)depth;
        l.Orient[lot] = (byte)axis;
        MarkLotCells(l, lot);
        if (derive)
        {
            labels ??= RoadLabels(w.Net);
            Derive(w, c, lot, labels);
        }

        return true;
    }

    /// <summary>Проекция точки на полилинию оси сегмента, мм вдоль оси (первое наименьшее расстояние; чистые float-операции IEEE).</summary>
    private static long ProjectMm(NetworkStore net, in SegmentData sd, float x, float z, long lengthMm)
    {
        int fp = sd.FirstPoint;
        float acc = 0f;
        float best = float.MaxValue;
        float bestS = 0f;
        for (int i = 0; i + 1 < sd.PointCount; i++)
        {
            Vec2 a = net.SegmentPoints[fp + i];
            Vec2 b = net.SegmentPoints[fp + i + 1];
            float dx = b.X - a.X;
            float dz = b.Z - a.Z;
            float len2 = (dx * dx) + (dz * dz);
            float len = MathF.Sqrt(len2);
            float t = len2 > 1e-12f ? Math.Clamp((((x - a.X) * dx) + ((z - a.Z) * dz)) / len2, 0f, 1f) : 0f;
            float qx = a.X + (dx * t) - x;
            float qz = a.Z + (dz * t) - z;
            float d2 = (qx * qx) + (qz * qz);
            if (d2 < best)
            {
                best = d2;
                bestS = acc + (len * t);
            }

            acc += len;
        }

        return Math.Clamp(RoundMm(bestS), 0, lengthMm);
    }

    /// <summary>
    /// Пересчёт после правки дорог (RP7 вызывает после <c>BuildRoad</c>/<c>UpgradeRoad</c>; <see cref="OnRoadBuilt"/> - сам): маска дороги и занятость заново, живые участки на сетке,
    /// у которых хотя бы одна клетка стала дорогой, получают <c>Lost</c> (здание в <c>Abandoned</c>). Возвращает число потерянных участков. В legacy - 0.
    /// </summary>
    public int OnRoadsChanged(World w, CityWorld c)
    {
        if (!_grid)
        {
            return 0;
        }

        RebuildOccupancy(w, c);
        LotStore l = c.Lots;
        int lost = 0;
        for (int lot = 0; lot < l.Count; lot++)
        {
            if (l.Orient[lot] == LotOrient.Legacy || IsDead(w, c, lot) || !AnyRoadCell(l, lot))
            {
                continue;
            }

            int bi = l.Building[lot];
            if (bi >= 0)
            {
                Growth.Abandon(w, c, bi);
            }

            l.Flags[lot] |= LotFlags.Lost;
            MakeCanonicalDerived(l, lot);
            Parking.ParkingBuild.OnLotLost(w, c, lot);
            lost++;
        }

        if (lost > 0)
        {
            l.Version++;
            RebuildOccupancy(w, c);
        }

        return lost;
    }
}
