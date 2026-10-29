using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

// Разрез и слияние сегментов (T5, player-tools.md §4.4): узел ровно в заданной точке (без прилипания к соседним).
internal sealed partial class NetworkBuilder
{
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
