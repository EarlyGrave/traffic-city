using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// RP6 + VS3/VS4 (road-profiles.md §7): сегменты и углы перекрёстков с профилем строятся по элементам сечения (<see cref="RoadProfileLayouts"/>).
/// LOD0 - полосы по элементам: асфальт (группы полос одной ширины с разметкой: осевая, пунктир, стоп-линии на концах), бордюр, газон, разделитель,
/// резерв и тротуар - цвета из <see cref="Palette"/>; LOD1 - асфальт одной лентой и тротуары; дальняя лента - только асфальт (владелец отверг
/// «карту» цветов вдали). Площадка перекрёстка стыкуется с торцами (кромки проезжей части), углы между рукавами добавляют кольца бордюра, газона
/// и тротуара по ширинам соседних профилей. Ширин в коде нет: всё из каталога.
/// </summary>
public sealed partial class RoadChunks
{
    private const float YReserve = 0.01f;
    private const float YVerge = 0.03f;
    private const float YCurb = 0.04f;
    private const float YWalk = 0.045f; // ниже тротуаров пешеходной сети (0.06): их heatmap остаётся сверху
    private const float MarkLift = 0.015f;
    private const float StopLineInset = 1.0f;
    private const float StopLineWidth = 0.5f;
    private const float StopLineSideGap = 0.15f;
    private const float MinLeg = 2.5f;
    private const float EmptyLayer = 1e-3f;

    private enum Detail
    {
        Full,
        Walk,
        Asphalt,
    }

    private readonly List<Vec2> _cornerPts = new();
    private Vec2[] _axis = new Vec2[256];
    private Vec2[] _nrm = new Vec2[256];
    private float[] _arc = new float[256];

    private void EnsureAxis(int n)
    {
        if (_axis.Length < n)
        {
            int len = Math.Max(n, _axis.Length * 2);
            _axis = new Vec2[len];
            _nrm = new Vec2[len];
            _arc = new float[len];
        }
    }

    private static void ElemStyle(RoadElemKind kind, out Color col, out float y)
    {
        switch (kind)
        {
            case RoadElemKind.Sidewalk:
                col = Palette.Sidewalk;
                y = YWalk;
                break;
            case RoadElemKind.Verge:
                col = Palette.Lawn;
                y = YVerge;
                break;
            case RoadElemKind.Curb:
                col = Palette.Curb;
                y = YCurb;
                break;
            case RoadElemKind.Median:
                col = Palette.Median;
                y = YVerge;
                break;
            default:
                col = Palette.Reserve;
                y = YReserve;
                break;
        }
    }

    /// <summary>Лента сегмента с профилем: ось = базовая полоса, сдвинутая на смещение её центра; ленты элементов - от оси на <c>x0..x1</c> по нормали.</summary>
    private void AddProfileRoad(NetworkView net, int seg, in SegGeom g, MeshBuilder b, float tol, float y, Color col, Detail detail, bool markings, Vec2[] ends)
    {
        RoadProfileLayouts.Layout lay = g.Lay!;
        SegmentData sd = net.Segments[seg];
        LaneData bl = net.Lanes[g.BaseAB ? sd.FirstLaneAB : sd.FirstLaneBA];
        ReadOnlySpan<Vec2> pts = net.LanePoints.Slice(bl.FirstPoint, bl.PointCount);
        int m;
        if (tol > 0f)
        {
            ChunkGrid.Simplify(pts, tol, _keep);
            m = _keep.Count;
            EnsureScratch(m);
            for (int k = 0; k < m; k++)
            {
                _scratch[k] = pts[_keep[k]];
            }
        }
        else
        {
            m = pts.Length;
            EnsureScratch(m);
            pts.CopyTo(_scratch);
        }

        if (!g.BaseAB)
        {
            Array.Reverse(_scratch, 0, m);
        }

        EnsureAxis(m);
        var span = new ReadOnlySpan<Vec2>(_scratch, 0, m);
        float cBase = g.BaseAB ? lay.CBaseAB : lay.CBaseBA;
        float acc = 0f;
        for (int k = 0; k < m; k++)
        {
            MeshBuilder.EdgeNormal(span, k, out float ox, out float oz);
            _nrm[k] = new Vec2(ox, oz);
            _axis[k] = new Vec2(span[k].X - (ox * cBase), span[k].Z - (oz * cBase));
            if (k > 0)
            {
                float dx = _axis[k].X - _axis[k - 1].X;
                float dz = _axis[k].Z - _axis[k - 1].Z;
                acc += MathF.Sqrt((dx * dx) + (dz * dz));
            }

            _arc[k] = acc;
        }

        // торцы для площадки перекрёстка: кромки проезжей части (+N, -N), на концах ленты
        ends[(seg * 4) + 0] = Offset(0, lay.CurbR);
        ends[(seg * 4) + 1] = Offset(0, -lay.CurbL);
        ends[(seg * 4) + 2] = Offset(m - 1, lay.CurbR);
        ends[(seg * 4) + 3] = Offset(m - 1, -lay.CurbL);

        if (detail != Detail.Full)
        {
            AddStrip(b, m, -lay.CurbL, lay.CurbR, y, col, seg, 0f, 0f, 0f, 0f);
        }

        ReadOnlySpan<RoadProfileLayouts.Elem> el = lay.Elems;
        int i = 0;
        while (i < el.Length)
        {
            RoadProfileLayouts.Elem e = el[i];
            if (e.Kind == RoadElemKind.Lane)
            {
                int j = i;
                int nt = 0;
                int nb = 0;
                while (j < el.Length && el[j].Kind == RoadElemKind.Lane && MathF.Abs(el[j].Width - e.Width) < 0.002f && (j == i || MathF.Abs(el[j].X0 - el[j - 1].X1) < 0.002f))
                {
                    nt++;
                    if (el[j].Backward)
                    {
                        nb++;
                    }

                    j++;
                }

                if (detail == Detail.Full)
                {
                    bool lines = markings && nt >= 2 && nt <= 15;
                    float code = lines ? nt + (16 * (nb > 0 && nb < nt ? nb : 0)) : 0f;
                    AddStrip(b, m, e.X0, el[j - 1].X1, y, col, seg, code, lines ? 1f : 0f, 0f, nt);
                }

                i = j;
                continue;
            }

            if (detail == Detail.Full || (detail == Detail.Walk && e.Kind == RoadElemKind.Sidewalk))
            {
                ElemStyle(e.Kind, out Color ec, out float ey);
                AddStrip(b, m, e.X0, e.X1, ey, ec, -1f, 0f, 0f, 0f, 0f);
            }

            i++;
        }

        if (detail == Detail.Full && markings)
        {
            if (lay.BwdLeft < lay.BwdRight)
            {
                AddStopLine(b, m, true, lay.BwdLeft, lay.BwdRight, y);
            }

            if (lay.FwdLeft < lay.FwdRight)
            {
                AddStopLine(b, m, false, lay.FwdLeft, lay.FwdRight, y);
            }
        }
    }

    private Vec2 Offset(int k, float x) => new(_axis[k].X + (_nrm[k].X * x), _axis[k].Z + (_nrm[k].Z * x));

    private void AddStrip(MeshBuilder b, int m, float x0, float x1, float y, Color col, float seg, float code, float dash, float vL, float vR)
    {
        if (x1 - x0 < EmptyLayer)
        {
            return;
        }

        int first = b.VertexCount;
        for (int k = 0; k < m; k++)
        {
            Vec2 r = Offset(k, x1);
            Vec2 l = Offset(k, x0);
            b.Add(new Vector3(r.X, y, r.Z), col, seg, code, dash, 0f, new Vector2(_arc[k], vR));
            b.Add(new Vector3(l.X, y, l.Z), col, seg, code, dash, 0f, new Vector2(_arc[k], vL));
        }

        for (int k = 0; k + 1 < m; k++)
        {
            int a = first + (k * 2);
            b.Quad(a, a + 1, a + 2, a + 3);
        }
    }

    /// <summary>Стоп-линия у конца ленты поперёк полос приходящего направления (на конце A - встречные B→A, на конце B - попутные A→B); код <c>CUSTOM0.y = -2</c> в шейдере.</summary>
    private void AddStopLine(MeshBuilder b, int m, bool atA, float x0, float x1, float y)
    {
        int k = atA ? 0 : m - 1;
        int k2 = atA ? 1 : m - 2;
        float dx = _axis[k2].X - _axis[k].X;
        float dz = _axis[k2].Z - _axis[k].Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < MinLeg)
        {
            return;
        }

        dx /= len; // внутрь ленты
        dz /= len;
        float cx = _axis[k].X + (dx * StopLineInset);
        float cz = _axis[k].Z + (dz * StopLineInset);
        float nx = _nrm[k].X;
        float nz = _nrm[k].Z;
        float hw = StopLineWidth * 0.5f;
        float xa = x0 + StopLineSideGap;
        float xb = x1 - StopLineSideGap;
        float yy = y + MarkLift;
        Color c = Palette.AsphaltVertex;
        int v0 = b.Add(new Vector3(cx + (nx * xa) - (dx * hw), yy, cz + (nz * xa) - (dz * hw)), c, -1f, -2f);
        int v1 = b.Add(new Vector3(cx + (nx * xb) - (dx * hw), yy, cz + (nz * xb) - (dz * hw)), c, -1f, -2f);
        int v2 = b.Add(new Vector3(cx + (nx * xa) + (dx * hw), yy, cz + (nz * xa) + (dz * hw)), c, -1f, -2f);
        int v3 = b.Add(new Vector3(cx + (nx * xb) + (dx * hw), yy, cz + (nz * xb) + (dz * hw)), c, -1f, -2f);
        b.Quad(v0, v1, v2, v3);
    }

    /// <summary>
    /// Кольца бордюра, газона и тротуара в углу между рукавами <paramref name="a"/> (вход, правая кромка к узлу) и <paramref name="nx"/> (выход):
    /// от угловой кривой площадки (внешняя кромка правого поворота) наружу; ширины слоёв линейно меняются от сечения одного рукава к сечению другого.
    /// Тупик (рукав сам с собой) и угол без профильных рукавов не рисуются.
    /// </summary>
    private void AddCornerStrips(in NodeData nd, in Arm a, in Arm nx, SegGeom[] geo, Vec2[] ends, MeshBuilder b, Detail detail)
    {
        if ((a.Seg == nx.Seg && a.AtA == nx.AtA) || detail == Detail.Asphalt)
        {
            return;
        }

        SegGeom ga = geo[a.Seg];
        SegGeom gn = geo[nx.Seg];
        if (!ga.Profiled && !gn.Profiled)
        {
            return;
        }

        float a1 = 0f;
        float a2 = 0f;
        float a3 = 0f;
        float n1 = 0f;
        float n2 = 0f;
        float n3 = 0f;
        if (ga.Profiled)
        {
            ga.Lay!.SideLayers(a.AtA, out a1, out a2, out a3);
            n1 = a1;
            n2 = a2;
            n3 = a3;
        }

        if (gn.Profiled)
        {
            gn.Lay!.SideLayers(!nx.AtA, out n1, out n2, out n3);
            if (!ga.Profiled)
            {
                a1 = n1;
                a2 = n2;
                a3 = n3;
            }
        }

        _cornerPts.Clear();
        _cornerPts.Add(a.RIn);
        foreach (Vec2 p in _corner)
        {
            _cornerPts.Add(p);
        }

        _cornerPts.Add(nx.ROut);
        int n = _cornerPts.Count;
        EnsureAxis(n);
        Vec2 c = nd.Position;
        Vec2 outA = Lateral(ends, a.Seg, a.AtA, a.AtA ? -1f : 1f);
        Vec2 outN = Lateral(ends, nx.Seg, nx.AtA, nx.AtA ? 1f : -1f);
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            Vec2 u;
            if (i == 0)
            {
                u = outA;
            }
            else if (i == n - 1)
            {
                u = outN;
            }
            else
            {
                float tx = _cornerPts[i + 1].X - _cornerPts[i - 1].X;
                float tz = _cornerPts[i + 1].Z - _cornerPts[i - 1].Z;
                float tl = MathF.Sqrt((tx * tx) + (tz * tz));
                float ux = tl > 1e-5f ? -tz / tl : 0f;
                float uz = tl > 1e-5f ? tx / tl : 0f;
                if ((ux * (_cornerPts[i].X - c.X)) + (uz * (_cornerPts[i].Z - c.Z)) < 0f)
                {
                    ux = -ux;
                    uz = -uz;
                }

                u = new Vec2(ux, uz);
            }

            _nrm[i] = u;
            if (i > 0)
            {
                float dx = _cornerPts[i].X - _cornerPts[i - 1].X;
                float dz = _cornerPts[i].Z - _cornerPts[i - 1].Z;
                total += MathF.Sqrt((dx * dx) + (dz * dz));
            }

            _arc[i] = total;
        }

        // слои: [0, d1] бордюр, [d1, d2] газон, [d2, d3] тротуар
        for (int layer = 0; layer < 3; layer++)
        {
            if (detail == Detail.Walk && layer != 2)
            {
                continue;
            }

            float aIn = layer == 0 ? 0f : layer == 1 ? a1 : a2;
            float aOut = layer == 0 ? a1 : layer == 1 ? a2 : a3;
            float nIn = layer == 0 ? 0f : layer == 1 ? n1 : n2;
            float nOut = layer == 0 ? n1 : layer == 1 ? n2 : n3;
            if (MathF.Max(aOut - aIn, nOut - nIn) < EmptyLayer)
            {
                continue;
            }

            Color col = layer == 0 ? Palette.Curb : layer == 1 ? Palette.Lawn : Palette.Sidewalk;
            float y = layer == 0 ? YCurb : layer == 1 ? YVerge : YWalk;
            int first = b.VertexCount;
            for (int i = 0; i < n; i++)
            {
                float t = total > 1e-4f ? _arc[i] / total : 0f;
                float d0 = aIn + ((nIn - aIn) * t);
                float d1 = aOut + ((nOut - aOut) * t);
                Vec2 p = _cornerPts[i];
                Vec2 u = _nrm[i];
                b.Add(new Vector3(p.X + (u.X * d1), y, p.Z + (u.Z * d1)), col, -1f);
                b.Add(new Vector3(p.X + (u.X * d0), y, p.Z + (u.Z * d0)), col, -1f);
            }

            for (int i = 0; i + 1 < n; i++)
            {
                int q = first + (i * 2);
                b.Quad(q, q + 1, q + 2, q + 3);
            }
        }
    }

    /// <summary>Единичный вектор поперёк рукава (от оси) на торце: <paramref name="sign"/> +1 - в сторону +N ленты, -1 - в сторону -N.</summary>
    private static Vec2 Lateral(Vec2[] ends, int seg, bool atA, float sign)
    {
        Vec2 plus = ends[(seg * 4) + (atA ? 0 : 2)];
        Vec2 minus = ends[(seg * 4) + (atA ? 1 : 3)];
        float dx = plus.X - minus.X;
        float dz = plus.Z - minus.Z;
        float l = MathF.Sqrt((dx * dx) + (dz * dz));
        return l < 1e-5f ? new Vec2(0f, 0f) : new Vec2(sign * dx / l, sign * dz / l);
    }
}
