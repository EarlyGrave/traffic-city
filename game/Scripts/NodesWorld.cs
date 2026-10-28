using System;
using Sim.Core.Api;
using Sim.Host;
using TrafficCity.Tools;

namespace TrafficCity;

/// <summary>Мир инструмента «Узлы» (<see cref="INodesWorld"/>): копия сети из последнего кадра хоста (главный поток, без блокировок).</summary>
public sealed class NodesWorld : INodesWorld
{
    private readonly Func<NetworkSnapshot?> _net;

    public NodesWorld(Func<NetworkSnapshot?> net)
    {
        _net = net;
    }

    public bool TryGetNet(out NetworkView net)
    {
        NetworkSnapshot? s = _net();
        if (s == null)
        {
            net = default;
            return false;
        }

        net = s.View;
        return true;
    }
}
