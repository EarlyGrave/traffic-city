using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Нарезка участков (zoning-demand.md §2.1): детерминированный проход по сегментам по возрастанию индекса и сторонам 0, 1. От обоих концов отступается
/// <c>node_clear_m</c>, остаток режется на <c>floor(L / lot_frontage_m)</c> участков равной длины (целые мм), участок отбрасывается, если его прямоугольник
/// <c>frontage x lot_depth_m</c> (от кромки тротуара) задевает уже принятый: сетка занятости 8 м, участок занимает ячейки, чьи центры лежат внутри прямоугольника.
/// Сетка занятости - scratch, выделенный один раз; её границы задаёт <see cref="CityGrid"/> плюс поля, поэтому после <c>Load</c> она та же.
/// <see cref="AppendSegment"/> дописывает участки нового сегмента в конец (старые не двигаются). Float здесь - только геометрия (производное).
/// </summary>
internal sealed class LotBuilder
{
    public const float OccCellM = 8f;
    public const float OccMarginM = 150f;
    private const float SidewalkDefaultM = 3f;

    private readonly byte[] _occ;
    private readonly float _ox;
    private readonly float _oz;
    private readonly int _nx;
    private readonly int _nz;

    private readonly float _depthHalfM;

    public LotBuilder(in CityGrid grid, int lotDepthMm)
    {
        _depthHalfM = lotDepthMm * 0.0005f;
        _ox = grid.OriginX - OccMarginM;
        _oz = grid.OriginZ - OccMarginM;
        float w = (grid.CellsX * grid.CellM) + (2f * OccMarginM);
        float h = (grid.CellsZ * grid.CellM) + (2f * OccMarginM);
        _nx = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling(w / OccCellM)));
        _nz = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling(h / OccCellM)));
        _occ = new byte[checked(_nx * _nz)];
    }

    /// <summary>Нарезает все сегменты сети и считает производные; false - ёмкость <c>max_lots</c> исчерпана (нарезано сколько влезло).</summary>
    public bool BuildAll(World w)
    {
        CityWorld c = w.City!;
        bool ok = true;
        for (int seg = 0; seg < w.Net.SegmentCount && ok; seg++)
        {
            if (w.Net.SegmentAlive(seg))   // дыры сети (4.3, T2): мёртвый слот сегмента не нарезается
            {
                ok = AppendSegment(w, c, seg, derive: false);
            }
        }

        RestoreDerived(w);
        return ok;
    }

    /// <summary>Дописывает участки сегмента <paramref name="seg"/> (обе стороны); false - ёмкость исчерпана. Дорожные компоненты участков пересчитывает вызывающий (<see cref="RebuildComponents"/>).</summary>
    public bool AppendSegment(World w, CityWorld c, int seg, bool derive = true)
    {
        int[]? labels = null;
        ref readonly SegmentData sd = ref w.Net.Segments[seg];
        long lengthMm = DetMath.ToLongSat(MathF.Round(sd.Length * 1000f));
        long clear = c.Cfg.Zoning.NodeClearMm;
        long frontage = c.Cfg.Zoning.LotFrontageMm;
        long avail = lengthMm - (2 * clear);
        long n = avail / frontage;
        if (n < 1)
        {
            return true;
        }

        for (int side = 0; side < 2; side++)
        {
            for (long k = 0; k < n; k++)
            {
                int s0 = (int)(clear + (k * avail / n));
                int s1 = (int)(clear + ((k + 1) * avail / n));
                if (!Fits(w, c, seg, side, s0, s1, out float cx, out float cz, out float hx, out float hz, out float nxv, out float nzv))
                {
                    continue;
                }

                if (!c.Lots.TryAdd(seg, sd.Generation, (byte)side, s0, s1, out int lot))
                {
                    return false;
                }

                Mark(cx, cz, hx, hz, nxv, nzv, s1 - s0);
                if (derive)
                {
                    labels ??= RoadLabels(w.Net);
                    Derive(w, c, lot, labels);
                }
            }
        }

        return true;
    }

    /// <summary>Производные участков и сетка занятости после загрузки сейва или смены сети (тем же кодом, что при <c>Create</c>).</summary>
    public void RestoreDerived(World w)
    {
        CityWorld c = w.City!;
        RebuildOccupancy(w, c);
        int[] comp = RoadLabels(w.Net);
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            Derive(w, c, lot, comp);
        }
    }

    /// <summary>
    /// Участок без сегмента: флаг <c>Lost</c> или сегмент мёртв/сменил поколение. Геометрии у него нет (сейв её не хранит), поэтому производные такого участка всегда канонические
    /// и одинаковы в живом мире и после <c>Load</c>.
    /// </summary>
    internal static bool IsDead(World w, CityWorld c, int lot)
    {
        int seg = c.Lots.Seg[lot];
        return (c.Lots.Flags[lot] & LotFlags.Lost) != 0 || (uint)seg >= (uint)w.Net.SegmentCount || !w.Net.SegmentAlive(seg) || w.Net.Segments[seg].Generation != c.Lots.SegGen[lot];
    }

    /// <summary>Сетка занятости - чистая функция живых участков (мёртвые её не занимают): так живой мир и загруженный нарезают новые сегменты одинаково.</summary>
    private void RebuildOccupancy(World w, CityWorld c)
    {
        Array.Clear(_occ);
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            if (!IsDead(w, c, lot))
            {
                Frame(w, c, c.Lots.Seg[lot], c.Lots.Side[lot], c.Lots.S0Mm[lot], c.Lots.S1Mm[lot], out float cx, out float cz, out float tx, out float tz, out float nx, out float nz);
                Mark(cx, cz, tx, tz, nx, nz, c.Lots.S1Mm[lot] - c.Lots.S0Mm[lot]);
            }
        }
    }

    /// <summary>
    /// Хук <c>BuildRoad</c> (zoning-demand.md §2.1, Z6): дописывает участки нового сегмента в конец (по возрастанию отрезка, стороны 0, 1) и пересчитывает производные всех участков
    /// (компоненты могли слиться, индексы полос и рёбер тротуара - сдвинуться). Вызывается после пересборки сети пешеходов. Ёмкость <c>max_lots</c> исчерпана - дописано сколько влезло.
    /// </summary>
    public void OnRoadBuilt(World w, int seg)
    {
        CityWorld c = w.City!;
        RebuildOccupancy(w, c);
        AppendSegment(w, c, seg, derive: false);
        int[] comp = RoadLabels(w.Net);
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            Derive(w, c, lot, comp);
        }
    }

    /// <summary>
    /// Хук финала <c>RemoveSegment</c> (Z6): живые участки сегмента получают <c>Lost</c>, их здания уходят в <c>Abandoned</c> (§8.4); возвращает число потерянных участков.
    /// Вызывается до того, как слот сегмента освобождён и переиспользован. Производные остальных участков пересчитывает <see cref="RestoreDerived"/> после пересборки сети.
    /// </summary>
    public static int OnSegmentRemoved(World w, CityWorld c, int seg, int gen)
    {
        LotStore l = c.Lots;
        int lost = 0;
        for (int lot = 0; lot < l.Count; lot++)
        {
            if (l.Seg[lot] != seg || l.SegGen[lot] != gen || (l.Flags[lot] & LotFlags.Lost) != 0)
            {
                continue;
            }

            int bi = l.Building[lot];
            if (bi >= 0)
            {
                Growth.Abandon(w, c, bi);   // до смены производных: учёт рынка идёт по прежней ячейке участка
            }

            l.Flags[lot] |= LotFlags.Lost;
            MakeCanonicalDerived(l, lot);
            lost++;
        }

        if (lost > 0)
        {
            l.Version++;
        }

        return lost;
    }

    private static void MakeCanonicalDerived(LotStore l, int lot)
    {
        l.X[lot] = 0f;
        l.Z[lot] = 0f;
        l.Heading[lot] = 0f;
        l.Cell[lot] = 0;
        l.CarLane[lot] = -1;
        l.CarS[lot] = 0f;
        l.PedEdge[lot] = -1;
        l.PedSMm[lot] = 0;
        l.RoadComp[lot] = -1;
        l.PedComp[lot] = -1;
    }

    /// <summary>Пересчитывает <c>RoadComp</c> всех участков (после <c>BuildRoad</c> компоненты могут слиться).</summary>
    public static void RebuildComponents(World w)
    {
        CityWorld c = w.City!;
        int[] comp = RoadLabels(w.Net);
        for (int lot = 0; lot < c.Lots.Count; lot++)
        {
            int seg = c.Lots.Seg[lot];
            c.Lots.RoadComp[lot] = seg < w.Net.SegmentCount ? comp[w.Net.Segments[seg].NodeA] : -1;
        }

        c.Lots.Version++;
    }

    /// <summary>Метки дорожных компонент узлов (слабая связность по сегментам); метка - наименьший индекс узла компоненты.</summary>
    public static int[] RoadLabels(NetworkStore net)
    {
        int n = net.NodeCount;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        for (int s = 0; s < net.SegmentCount; s++)
        {
            if (!net.SegmentAlive(s))
            {
                continue;
            }

            int a = Find(parent, net.Segments[s].NodeA);
            int b = Find(parent, net.Segments[s].NodeB);
            if (a != b)
            {
                if (a < b)
                {
                    parent[b] = a;
                }
                else
                {
                    parent[a] = b;
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            parent[i] = Find(parent, i);
        }

        return parent;
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }

        return x;
    }

    // ---- геометрия ----

    /// <summary>Точка оси сегмента на расстоянии <paramref name="s"/> от начала и единичная касательная.</summary>
    internal static void PointAt(NetworkStore net, in SegmentData sd, float s, out float x, out float z, out float tx, out float tz)
    {
        int fp = sd.FirstPoint;
        int last = sd.PointCount - 1;
        float acc = 0f;
        x = net.SegmentPoints[fp].X;
        z = net.SegmentPoints[fp].Z;
        tx = 1f;
        tz = 0f;
        for (int i = 0; i < last; i++)
        {
            Vec2 a = net.SegmentPoints[fp + i];
            Vec2 b = net.SegmentPoints[fp + i + 1];
            float dx = b.X - a.X;
            float dz = b.Z - a.Z;
            float len = MathF.Sqrt((dx * dx) + (dz * dz));
            if (len > 1e-6f)
            {
                tx = dx / len;
                tz = dz / len;
            }

            if (s <= acc + len || i == last - 1)
            {
                float u = len > 1e-6f ? Math.Clamp((s - acc) / len, 0f, 1f) : 0f;
                x = a.X + (dx * u);
                z = a.Z + (dz * u);
                return;
            }

            acc += len;
        }
    }

    private static float FrontOffsetM(World w, in SegmentData sd)
    {
        if (w.Net.Profiles != null && sd.Profile >= 0)
        {
            return w.Net.Profiles.EdgeMm[sd.Profile] * 0.001f;   // режим профилей (ADR-0030): фасад участка - край профиля (проезжая часть, газон, тротуар); сетка 8 м - RP5
        }

        float sidewalk = w.PedCfg.Enabled ? w.PedCfg.SidewalkWidthMm / 1000f : SidewalkDefaultM;
        return (Math.Max(sd.LanesAB, sd.LanesBA) * w.Builder.Options.LaneWidth) + sidewalk;
    }

    private static void Frame(World w, CityWorld c, int seg, int side, int s0Mm, int s1Mm, out float cx, out float cz, out float tx, out float tz, out float nx, out float nz)
    {
        ref readonly SegmentData sd = ref w.Net.Segments[seg];
        float sc = (s0Mm + s1Mm) * 0.0005f;
        PointAt(w.Net, in sd, sc, out float px, out float pz, out tx, out tz);
        if (side == 0)
        {
            nx = -tz;
            nz = tx;
        }
        else
        {
            nx = tz;
            nz = -tx;
        }

        float off = FrontOffsetM(w, in sd) + (c.Cfg.Zoning.LotDepthMm * 0.0005f);
        cx = px + (nx * off);
        cz = pz + (nz * off);
    }

    private bool Fits(World w, CityWorld c, int seg, int side, int s0, int s1, out float cx, out float cz, out float tx, out float tz, out float nx, out float nz)
    {
        Frame(w, c, seg, side, s0, s1, out cx, out cz, out tx, out tz, out nx, out nz);
        float halfL = (s1 - s0) * 0.0005f;
        float halfD = c.Cfg.Zoning.LotDepthMm * 0.0005f;
        return !InLandUse(w, cx, cz) && Scan(cx, cz, tx, tz, nx, nz, halfL, halfD, mark: false);
    }

    /// <summary>Центр участка внутри парка или воды (city.landuse, visual-style.md §8): такой участок не нарезается. Без землепользования - всегда false.</summary>
    private static bool InLandUse(World w, float x, float z)
    {
        LandUsePoly[] polys = w.LandUse;
        for (int i = 0; i < polys.Length; i++)
        {
            if (polys[i].Contains(x, z))
            {
                return true;
            }
        }

        return false;
    }

    private void Mark(float cx, float cz, float tx, float tz, float nx, float nz, int lengthMm)
    {
        Scan(cx, cz, tx, tz, nx, nz, lengthMm * 0.0005f, _depthHalfM, mark: true);
    }

    /// <summary>true - ни одна ячейка прямоугольника не занята (при <paramref name="mark"/> ячейки занимаются).</summary>
    private bool Scan(float cx, float cz, float tx, float tz, float nx, float nz, float halfL, float halfD, bool mark)
    {
        float ex = (MathF.Abs(tx) * halfL) + (MathF.Abs(nx) * halfD);
        float ez = (MathF.Abs(tz) * halfL) + (MathF.Abs(nz) * halfD);
        int ix0 = Math.Max(0, DetMath.ToIntSat(MathF.Floor((cx - ex - _ox) / OccCellM)));
        int ix1 = Math.Min(_nx - 1, DetMath.ToIntSat(MathF.Floor((cx + ex - _ox) / OccCellM)));
        int iz0 = Math.Max(0, DetMath.ToIntSat(MathF.Floor((cz - ez - _oz) / OccCellM)));
        int iz1 = Math.Min(_nz - 1, DetMath.ToIntSat(MathF.Floor((cz + ez - _oz) / OccCellM)));
        for (int iz = iz0; iz <= iz1; iz++)
        {
            for (int ix = ix0; ix <= ix1; ix++)
            {
                float px = _ox + ((ix + 0.5f) * OccCellM) - cx;
                float pz = _oz + ((iz + 0.5f) * OccCellM) - cz;
                float u = (px * tx) + (pz * tz);
                float v = (px * nx) + (pz * nz);
                if (MathF.Abs(u) > halfL || MathF.Abs(v) > halfD)
                {
                    continue;
                }

                int idx = (iz * _nx) + ix;
                if (mark)
                {
                    _occ[idx] = 1;
                }
                else if (_occ[idx] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void Derive(World w, CityWorld c, int lot, int[] labels)
    {
        LotStore l = c.Lots;
        int seg = l.Seg[lot];
        if (IsDead(w, c, lot))
        {
            MakeCanonicalDerived(l, lot);
            l.Version++;
            return;
        }

        ref readonly SegmentData sd = ref w.Net.Segments[seg];
        int side = l.Side[lot];
        Frame(w, c, seg, side, l.S0Mm[lot], l.S1Mm[lot], out float cx, out float cz, out _, out _, out float nx, out float nz);
        l.X[lot] = cx;
        l.Z[lot] = cz;
        l.Heading[lot] = MathF.Atan2(-nz, -nx);
        l.Cell[lot] = c.Grid.CellOf(cx, cz);
        float sc = (l.S0Mm[lot] + l.S1Mm[lot]) * 0.0005f;
        if (side == 0 ? sd.LanesAB > 0 : sd.LanesBA > 0)
        {
            int lane = side == 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            float len = w.Net.LaneLength[lane];
            l.CarLane[lot] = lane;
            l.CarS[lot] = Math.Clamp(side == 0 ? sc : sd.Length - sc, 0f, len);
        }
        else
        {
            l.CarLane[lot] = -1;
            l.CarS[lot] = 0f;
        }

        l.PedEdge[lot] = -1;
        l.PedSMm[lot] = 0;
        l.PedComp[lot] = -1;
        if (w.Net.PedNet != null && w.Net.PedNet.PointOnSide(seg, side, sc, out int edge, out int smm))
        {
            l.PedEdge[lot] = edge;
            l.PedSMm[lot] = smm;
            l.PedComp[lot] = w.Net.PedNet.PedComp[w.Net.PedNet.Edges[edge].A];
        }

        l.RoadComp[lot] = labels[sd.NodeA];
        l.Version++;
    }
}
