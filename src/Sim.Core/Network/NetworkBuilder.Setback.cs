using System;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Отступ конца сегмента от оси узла (ADR-0028, intersections.md §1): по рукавам. У узла степени ≥ 3 рукав отступает на
/// <c>max(NodeSetback, h + CornerRadius)</c>, где <c>h</c> - полуширина самой широкой поперечной дороги узла (рукав, который не продолжает данный «прямо»);
/// тупики и изгибы (степень 1-2) - ровно <c>NodeSetback</c>. Тогда стоп-линия рукава стоит у начала скругления бордюра радиуса <c>CornerRadius</c>, и правый поворот
/// из крайней полосы идёт по дуге радиуса ≈ <c>CornerRadius + LaneWidth/2</c>. Без тригонометрии (кросс-платформенный детерминизм, порт на Rust 1:1).
/// </summary>
internal sealed partial class NetworkBuilder
{
    /// <summary>Сколько сегментов получили сжатые надбавки отступов (ось короче суммы отступов + <c>MinLaneLength</c>). Диагностика построения, не состояние мира.</summary>
    internal int SetbackClampCount;

    private float HalfWidth(int laneTotal) => laneTotal * _o.LaneWidth * 0.5f;

    /// <summary>Полуширина проезжей части сегмента <c>h</c> (ADR-0028): режим профилей - <c>max(CurbL, CurbR)</c> его профиля (ADR-0030), иначе по числу полос.</summary>
    private float SegmentHalf(in SegmentData sd) =>
        _s.Profiles != null && sd.Profile >= 0 ? _s.Profiles.HalfRoadM(sd.Profile) : HalfWidth(sd.LanesAB + sd.LanesBA);

    /// <summary>Отступ рукава узла степени <paramref name="degree"/> при полуширине самой широкой поперечной дороги <paramref name="crossHalf"/> (0 - поперечных нет).</summary>
    internal static float ArmSetback(in NetworkOptions o, int degree, float crossHalf)
    {
        if (!(o.CornerRadius > 0f) || degree < 3 || !(crossHalf > 0f))
        {
            return o.NodeSetback;
        }

        float s = crossHalf + o.CornerRadius;
        return s > o.NodeSetback ? s : o.NodeSetback;
    }

    /// <summary>Рукав <paramref name="v"/> поперечен рукаву <paramref name="u"/> (направления от узла): не его продолжение «прямо» (угол до развёрнутого больше 30°, как <c>ClassifyTurn</c>).</summary>
    private static bool Crosses(Vec2 u, Vec2 v) => (u.X * v.X) + (u.Z * v.Z) > -StraightCos;

    private static Vec2 Dir(Vec2 from, Vec2 to)
    {
        float dx = to.X - from.X;
        float dz = to.Z - from.Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        return len > 0f ? new Vec2(dx / len, dz / len) : default;
    }

    /// <summary>
    /// Отступ нового конца у узла <paramref name="node"/> по уже существующим рукавам узла (инструменты игрока, <c>BuildRoad</c>): ось нового рукава идёт к точке
    /// <paramref name="toward"/>. Существующие рукава не перестраиваются (на них могут быть машины) - их отступы остаются прежними.
    /// </summary>
    internal float IncrementalSetback(int node, Vec2 toward, float ownHalf)
    {
        _ = ownHalf;
        if (!(_o.CornerRadius > 0f))
        {
            return _o.NodeSetback;
        }

        Vec2 u = Dir(_s.Nodes[node].Position, toward);
        int degree = 1;
        float cross = 0f;
        for (int code = _s.IncHead[node]; code >= 0; code = _s.IncNext[code])
        {
            degree++;
            ref readonly SegmentData sd = ref _s.Segments[code >> 1];
            int p0 = sd.FirstPoint;
            Vec2 v = (code & 1) == 0
                ? Dir(_s.SegmentPoints[p0], _s.SegmentPoints[p0 + 1])
                : Dir(_s.SegmentPoints[p0 + sd.PointCount - 1], _s.SegmentPoints[p0 + sd.PointCount - 2]);
            float h = SegmentHalf(in sd);
            if (Crosses(u, v) && h > cross)
            {
                cross = h;
            }
        }

        return ArmSetback(in _o, degree, cross);
    }

    /// <summary>
    /// Отступы концов прямых сегментов <c>a[e] → b[e]</c> (узлы уже в хранилище) по полному списку рукавов (генератор, явная сеть): <paramref name="setbackA"/>/<paramref name="setbackB"/>.
    /// Сегменты с неверными узлами получают <c>NodeSetback</c> и в рукава не идут (их отклонит <c>AddSegment</c>). Аллоцирует рабочие массивы (построение мира, не тик).
    /// </summary>
    internal void PlanSetbacks(ReadOnlySpan<int> a, ReadOnlySpan<int> b, ReadOnlySpan<int> laneTotal, Span<float> setbackA, Span<float> setbackB) =>
        PlanSetbacks(a, b, laneTotal, ReadOnlySpan<int>.Empty, setbackA, setbackB);

    /// <summary>То же; у сегментов с <paramref name="profile"/>[e] &gt;= 0 (режим профилей) <c>h</c> берётся из профиля, а не по числу полос.</summary>
    internal void PlanSetbacks(ReadOnlySpan<int> a, ReadOnlySpan<int> b, ReadOnlySpan<int> laneTotal, ReadOnlySpan<int> profile, Span<float> setbackA, Span<float> setbackB)
    {
        int m = a.Length;
        int nodes = _s.NodeCount;
        int[] head = new int[nodes];
        Array.Fill(head, -1);
        int[] next = new int[2 * m];
        int[] degree = new int[nodes];
        Vec2[] dir = new Vec2[2 * m];
        float[] half = new float[m];
        for (int e = 0; e < m; e++)
        {
            int na = a[e];
            int nb = b[e];
            half[e] = profile.Length > e && profile[e] >= 0 && _s.Profiles != null ? _s.Profiles.HalfRoadM(profile[e]) : HalfWidth(laneTotal[e]);
            next[2 * e] = -1;
            next[(2 * e) + 1] = -1;
            if (na < 0 || nb < 0 || na >= nodes || nb >= nodes || na == nb || !_s.NodeAlive(na) || !_s.NodeAlive(nb))
            {
                continue;
            }

            Vec2 pa = _s.Nodes[na].Position;
            Vec2 pb = _s.Nodes[nb].Position;
            dir[2 * e] = Dir(pa, pb);
            dir[(2 * e) + 1] = Dir(pb, pa);
            next[2 * e] = head[na];
            head[na] = 2 * e;
            next[(2 * e) + 1] = head[nb];
            head[nb] = (2 * e) + 1;
            degree[na]++;
            degree[nb]++;
        }

        for (int e = 0; e < m; e++)
        {
            setbackA[e] = _o.NodeSetback;
            setbackB[e] = _o.NodeSetback;
            int na = a[e];
            int nb = b[e];
            if (na < 0 || nb < 0 || na >= nodes || nb >= nodes || na == nb || !_s.NodeAlive(na) || !_s.NodeAlive(nb))
            {
                continue;
            }

            setbackA[e] = EndSetback(2 * e, na, head, next, degree, dir, half);
            setbackB[e] = EndSetback((2 * e) + 1, nb, head, next, degree, dir, half);
        }
    }

    private float EndSetback(int code, int node, int[] head, int[] next, int[] degree, Vec2[] dir, float[] half)
    {
        float cross = 0f;
        for (int c = head[node]; c >= 0; c = next[c])
        {
            if (c != code && Crosses(dir[code], dir[c]) && half[c >> 1] > cross)
            {
                cross = half[c >> 1];
            }
        }

        return ArmSetback(in _o, degree[node], cross);
    }

    // Надбавки сверх NodeSetback сжимаются пропорционально, если ось не вмещает оба отступа и MinLaneLength (длина оси >= 2 * NodeSetback + MinLaneLength проверена).
    private void ClampSetbacks(float total, ref float setbackA, ref float setbackB)
    {
        float b = _o.NodeSetback;
        if (setbackA < b)
        {
            setbackA = b;
        }

        if (setbackB < b)
        {
            setbackB = b;
        }

        float avail = total - _o.MinLaneLength;
        if (setbackA + setbackB > avail)
        {
            float ea = setbackA - b;
            float eb = setbackB - b;
            float k = (avail - (2f * b)) / (ea + eb);
            setbackA = b + (ea * k);
            setbackB = b + (eb * k);
            SetbackClampCount++;
        }
    }

    /// <summary>Длина оси сегмента от узла до узла: обрезанная ось плюс отступы обоих концов (расстояние от узла до конца оси; для прямого сегмента = расстояние между узлами).</summary>
    internal float FullAxisLength(int seg)
    {
        ref readonly SegmentData sd = ref _s.Segments[seg];
        Vec2 a = _s.Nodes[sd.NodeA].Position;
        Vec2 b = _s.Nodes[sd.NodeB].Position;
        return sd.Length + Dist(a, _s.SegmentPoints[sd.FirstPoint]) + Dist(b, _s.SegmentPoints[sd.FirstPoint + sd.PointCount - 1]);
    }
}
