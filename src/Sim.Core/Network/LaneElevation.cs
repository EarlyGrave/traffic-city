using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Продольный профиль дорог на рельефе (terrain.md §4.2, TR3): производные <see cref="NetworkStore.LanePointY"/>, <see cref="NetworkStore.SegmentPointY"/>,
/// <see cref="NetworkStore.LaneGrade"/> из высот узлов. Только при рельефе; вне хэша и сейва, пересчитываются целиком после правки сети и после <c>Load</c>.
/// Площадка узла плоская на <see cref="NodeData.Elevation"/>; полоса сегмента - линейно от узла начала к узлу конца по <c>LanePointS / Length</c>.
/// </summary>
internal static class LaneElevation
{
    /// <summary>Выделить массивы (один раз, при включённом рельефе).</summary>
    public static void Ensure(NetworkStore n)
    {
        if (n.LanePointY == null)
        {
            n.LanePointY = new float[n.LanePoints.Length];
            n.SegmentPointY = new float[n.SegmentPoints.Length];
            n.LaneGrade = new float[n.Lanes.Length];
        }
    }

    /// <summary>Полный пересчёт по всем живым полосам и сегментам. Порядок и арифметика фиксированы; 0 Б.</summary>
    public static void Rebuild(NetworkStore n)
    {
        float[] ly = n.LanePointY!;
        float[] grade = n.LaneGrade!;
        float[] sy = n.SegmentPointY!;
        for (int l = 0; l < n.LaneCount; l++)
        {
            if (!n.LaneAlive(l))
            {
                grade[l] = 0f;
                continue;
            }

            ref readonly LaneData ld = ref n.Lanes[l];
            int first = ld.FirstPoint;
            int count = ld.PointCount;
            if (ld.Kind == LaneKind.Connector || ld.Segment < 0)
            {
                float y = ld.Node >= 0 ? n.Nodes[ld.Node].Elevation : 0f;
                for (int k = 0; k < count; k++)
                {
                    ly[first + k] = y;
                }

                grade[l] = 0f;
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[ld.Segment];
            float ya = n.Nodes[ld.Direction == 0 ? sd.NodeA : sd.NodeB].Elevation;
            float yb = n.Nodes[ld.Direction == 0 ? sd.NodeB : sd.NodeA].Elevation;
            float len = ld.Length;
            for (int k = 0; k < count; k++)
            {
                ly[first + k] = len > 0f ? ya + ((yb - ya) * n.LanePointS[first + k] / len) : ya;
            }

            grade[l] = sd.Length > 0f ? (yb - ya) / sd.Length : 0f;
        }

        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s))
            {
                continue;
            }

            ref readonly SegmentData sd = ref n.Segments[s];
            float ya = n.Nodes[sd.NodeA].Elevation;
            float yb = n.Nodes[sd.NodeB].Elevation;
            int first = sd.FirstPoint;
            float total = 0f;
            for (int k = 1; k < sd.PointCount; k++)
            {
                total += Dist(n.SegmentPoints[first + k - 1], n.SegmentPoints[first + k]);
            }

            float acc = 0f;
            for (int k = 0; k < sd.PointCount; k++)
            {
                if (k > 0)
                {
                    acc += Dist(n.SegmentPoints[first + k - 1], n.SegmentPoints[first + k]);
                }

                sy[first + k] = total > 0f ? ya + ((yb - ya) * acc / total) : ya;
            }
        }
    }

    /// <summary>Высота полосы <paramref name="lane"/> на отрезке <paramref name="piece"/> в точке <paramref name="s"/> (тот же отрезок, что у X/Z; §6.2).</summary>
    public static float YOn(NetworkStore n, int lane, int piece, float s)
    {
        float[] ly = n.LanePointY!;
        ref readonly LaneData ld = ref n.Lanes[lane];
        int k = ld.FirstPoint + Math.Clamp(piece, 0, Math.Max(0, ld.PointCount - 2));
        if (ld.PointCount < 2)
        {
            return ly[ld.FirstPoint];
        }

        float s0 = n.LanePointS[k];
        float s1 = n.LanePointS[k + 1];
        float f = s1 > s0 ? Math.Clamp((s - s0) / (s1 - s0), 0f, 1f) : 0f;
        return ly[k] + ((ly[k + 1] - ly[k]) * f);
    }

    private static float Dist(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
