using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

// Запросы поверх построенной сети для агентов (P2): точки на тротуарах, индекс перехода, позиция на ребре.
internal sealed partial class PedNetwork
{
    /// <summary>Кумулятивная длина полилинии ребра в точке (м, от первой точки ребра); производное, заполняет <c>Rebuild</c>. Вне хэша сети.</summary>
    public readonly float[] PointCum;

    /// <summary>Счётчик пересборок (меняется в <c>Rebuild</c>): индексы вида в <c>RenderViewState</c>-стиле перестраиваются по нему.</summary>
    public int BuildStamp;

    /// <summary>Число индексов переходов в состоянии агентов: слоты рукавов в <c>NodeArms</c> + по одному на остановку (переход на перегоне <c>m</c> = <see cref="MidXBase"/> + m).</summary>
    public int XCapacity => ArmCap + MidCap;

    /// <summary>База индексов переходов на перегоне = число занятых слотов рукавов.</summary>
    public int MidXBase => _armUsed;

    /// <summary>Индекс перехода по ребру-переходу (<see cref="PedEdgeKind.Crossing"/> или <see cref="PedEdgeKind.MidCrossing"/>).</summary>
    public int XOfEdge(int edge)
    {
        ref readonly PedEdgeData e = ref Edges[edge];
        return e.Kind == PedEdgeKind.Crossing ? e.Slot : _armUsed + e.Slot;
    }

    /// <summary>Ребро перехода по индексу <paramref name="x"/> или -1 (перехода нет).</summary>
    public int EdgeOfX(int x)
    {
        if ((uint)x < (uint)_armUsed)
        {
            return XEdge[x];
        }

        int m = x - _armUsed;
        return (uint)m < (uint)MidCount ? MidEdge[m] : -1;
    }

    private void BuildCumulative()
    {
        for (int e = 0; e < EdgeCount; e++)
        {
            int first = Edges[e].FirstPoint;
            int count = Edges[e].PointCount;
            PointCum[first] = 0f;
            for (int k = 1; k < count; k++)
            {
                Vec2 d = Points[first + k] - Points[first + k - 1];
                PointCum[first + k] = PointCum[first + k - 1] + MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
            }
        }
    }

    /// <summary>
    /// Точка на тротуаре стороны <paramref name="side"/> сегмента <paramref name="seg"/> на расстоянии <paramref name="s"/> по оси:
    /// ребро и смещение от его <c>A</c> вдоль полилинии, мм (линейно по <c>S0..S1</c>). false - у стороны нет рёбер.
    /// </summary>
    public bool PointOnSide(int seg, int side, float s, out int edge, out int sMm)
    {
        edge = -1;
        sMm = 0;
        SideEdges(seg, side, out int first, out int count);
        if (count <= 0)
        {
            return false;
        }

        int e = first + count - 1;
        for (int k = first; k < first + count; k++)
        {
            if (Edges[k].S1 >= s)
            {
                e = k;
                break;
            }
        }

        ref readonly PedEdgeData ed = ref Edges[e];
        float span = ed.S1 - ed.S0;
        float t = span > 1e-3f ? (s - ed.S0) / span : 0.5f;
        t = t < 0f ? 0f : (t > 1f ? 1f : t);
        edge = e;
        sMm = (int)(ed.LenMm * t);
        return true;
    }

    /// <summary>Обратное к <see cref="PointOnSide"/>: положение точки <c>(edge, sMm)</c> тротуара по оси сегмента, м.</summary>
    public float AxisOf(int edge, int sMm)
    {
        ref readonly PedEdgeData ed = ref Edges[edge];
        float t = ed.LenMm > 0 ? (float)sMm / ed.LenMm : 0f;
        return ed.S0 + ((ed.S1 - ed.S0) * t);
    }

    /// <summary>Позиция в мире точки ребра на расстоянии <paramref name="distFromAMm"/> от <c>A</c> и курс по ходу (<paramref name="backward"/> - от B к A). <paramref name="hint"/> - кусок полилинии (кэш вызывающего).</summary>
    public void Evaluate(int edge, int distFromAMm, bool backward, ref int hint, out float x, out float z, out float heading)
    {
        ref readonly PedEdgeData ed = ref Edges[edge];
        int first = ed.FirstPoint;
        int last = first + ed.PointCount - 1;
        float total = PointCum[last];
        float t = ed.LenMm > 0 ? (float)distFromAMm / ed.LenMm : 0f;
        t = t < 0f ? 0f : (t > 1f ? 1f : t);
        float target = t * total;
        int k = hint;
        if (k < first || k >= last)
        {
            k = first;
        }

        while (k > first && PointCum[k] > target)
        {
            k--;
        }

        while (k < last - 1 && PointCum[k + 1] < target)
        {
            k++;
        }

        hint = k;
        Vec2 a = Points[k];
        Vec2 b = Points[k + 1];
        float seg = PointCum[k + 1] - PointCum[k];
        float u = seg > 1e-6f ? (target - PointCum[k]) / seg : 0f;
        u = u < 0f ? 0f : (u > 1f ? 1f : u);
        x = a.X + ((b.X - a.X) * u);
        z = a.Z + ((b.Z - a.Z) * u);
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        heading = backward ? MathF.Atan2(-dz, -dx) : MathF.Atan2(dz, dx);
    }
}
