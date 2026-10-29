using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

// Разрез и слияние сегментов (T5, player-tools.md §4.4): узел ровно в заданной точке (без прилипания к соседним).
internal sealed partial class NetworkBuilder
{
    /// <summary>
    /// Квадратичная Безье A - C - B (player-tools.md §2): <c>n = clamp(ceil((|AC| + |CB|) / 8 м), 2, 32)</c> точек, равномерно по параметру, без функций libm кроме sqrt и округления вверх;
    /// концы - ровно A и B. Отступление от спеки (там - по хорде): у крутой петли хорда коротка, и редкие точки скрыли бы перегиб от проверки радиуса. Возвращает число точек в <paramref name="dst"/>.
    /// </summary>
    public static int SampleQuadratic(Vec2 a, Vec2 c, Vec2 b, System.Span<Vec2> dst)
    {
        float ctrl = Dist(a, c) + Dist(c, b);
        int n = System.Math.Clamp(DetMath.ToIntSat(System.MathF.Ceiling(ctrl / 8f)), 2, MaxCurvePoints);
        dst[0] = a;
        for (int i = 1; i < n - 1; i++)
        {
            float t = i / (float)(n - 1);
            float u = 1f - t;
            dst[i] = new Vec2((u * u * a.X) + (2f * u * t * c.X) + (t * t * b.X), (u * u * a.Z) + (2f * u * t * c.Z) + (t * t * b.Z));
        }

        dst[n - 1] = b;
        return n;
    }

    /// <summary>Новый узел в точке <paramref name="position"/> без поиска соседа в <c>SnapRadius</c> (валидатор проверил, что рядом узлов нет). Не-Ok - пул или сетка прилипания полны.</summary>
    internal NetworkStatus AddNodeExact(Vec2 position, out int node)
    {
        node = -1;
        int idx = _s.NodePool.PeekIndex();
        if (idx < 0 || !IsFinite(position))
        {
            return NetworkStatus.NodesFull;
        }

        _s.Nodes[idx] = new NodeData { Position = position };
        if (!LinkNode(idx))
        {
            _s.Nodes[idx] = NetworkStore.DeadNode(_s.NodePool.GenerationOf(idx));
            return NetworkStatus.NodesFull;
        }

        _s.NodePool.TryAllocate(out idx, out int gen);
        _s.IncHead[idx] = -1;
        _s.Nodes[idx] = new NodeData
        {
            Position = position,
            Control = NodeControl.None,
            FirstArm = -1,
            Signal = -1,
            Zone = -1,
            Radius = _o.NodeSetback,
            Generation = gen,
        };
        _s.Version++;
        node = idx;
        return NetworkStatus.Ok;
    }
}
