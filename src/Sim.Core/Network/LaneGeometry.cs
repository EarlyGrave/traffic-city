using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>Вычисление позиции/направления по полосе (core-api §6.2). Без аллокаций.</summary>
internal static class LaneGeometry
{
    /// <summary>
    /// Позиция и единичное направление на расстоянии <paramref name="s"/> (зажимается в [0, Length]).
    /// <paramref name="pointHint"/> — индекс отрезка полилинии внутри полосы (0..PointCount-2), хранится у машины:
    /// поиск идёт от подсказки вперёд/назад, амортизированно O(1).
    /// </summary>
    public static void Evaluate(
        ReadOnlySpan<LaneData> lanes,
        ReadOnlySpan<Vec2> points,
        ReadOnlySpan<float> pointS,
        int lane,
        float s,
        ref int pointHint,
        out Vec2 pos,
        out Vec2 dir)
    {
        ref readonly LaneData l = ref lanes[lane];
        int first = l.FirstPoint;
        int lastPiece = l.PointCount - 2;
        float total = pointS[first + lastPiece + 1];
        if (s < 0f)
        {
            s = 0f;
        }
        else if (s > total)
        {
            s = total;
        }

        int h = pointHint;
        if (h < 0)
        {
            h = 0;
        }
        else if (h > lastPiece)
        {
            h = lastPiece;
        }

        while (h < lastPiece && s >= pointS[first + h + 1])
        {
            h++;
        }

        while (h > 0 && s < pointS[first + h])
        {
            h--;
        }

        pointHint = h;
        Vec2 a = points[first + h];
        Vec2 b = points[first + h + 1];
        float sa = pointS[first + h];
        float span = pointS[first + h + 1] - sa;
        float t = span > 1e-6f ? (s - sa) / span : 0f;
        Vec2 d = b - a;
        pos = a + (d * t);
        float len = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        dir = len > 1e-9f ? d * (1f / len) : new Vec2(1f, 0f);
    }

    public static void Evaluate(NetworkStore store, int lane, float s, ref int pointHint, out Vec2 pos, out Vec2 dir) =>
        Evaluate(store.Lanes, store.LanePoints, store.LanePointS, lane, s, ref pointHint, out pos, out dir);

    /// <summary>Удобная форма: позиция и курс (atan2(dz, dx), радианы). Для рендера/тестов, не для хэша состояния.</summary>
    public static (Vec2 Pos, float Heading) Evaluate(in NetworkView view, int lane, float s)
    {
        int hint = 0;
        Evaluate(view.Lanes, view.LanePoints, view.LanePointS, lane, s, ref hint, out Vec2 pos, out Vec2 dir);
        return (pos, MathF.Atan2(dir.Z, dir.X));
    }

    public static (Vec2 Pos, float Heading) Evaluate(NetworkStore store, int lane, float s) => Evaluate(store.GetView(), lane, s);
}
