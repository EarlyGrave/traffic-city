using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Автомат фаз светофоров (intersections.md 6.1), часть фазы 4 тика: продвигает таймеры и пишет
/// <c>World.SignalStates</c> и <c>World.LaneSignals</c> connector'ов регулируемых узлов. Без аллокаций.
/// Формулировка: при <c>TicksLeft == 0</c> интервал переключается (Green -> Yellow -> AllRed -> следующая фаза),
/// нулевые интервалы пропускаются; затем состояние публикуется (<c>TicksToChange = TicksLeft</c>) и
/// <c>TicksLeft--</c>. Так интервал длиной L виден ровно L тиков, а позиция в цикле на n-м вызове = (offset + n) mod Cycle.
/// В <c>PhaseList</c> пока не подключена (отдельная задача интеграции).
/// </summary>
internal static class SignalPhase
{
    public static void Run(World w)
    {
        SignalStore s = w.Signals;
        NetworkStore net = w.Net;
        for (int slot = 0; slot < s.Count; slot++)
        {
            SignalMode mode = s.Mode[slot];
            if (mode == SignalMode.Fixed)
            {
                if (s.TicksLeft[slot] == 0)
                {
                    Advance(s, slot);
                }

                w.SignalStates[slot] = new SignalState
                {
                    Node = s.Node[slot],
                    Phase = s.Phase[slot],
                    Interval = s.Interval[slot],
                    TicksToChange = s.TicksLeft[slot],
                };
                s.TicksLeft[slot]--;
            }
            else
            {
                w.SignalStates[slot] = new SignalState
                {
                    Node = s.Node[slot],
                    Phase = 0,
                    Interval = mode == SignalMode.Off ? SignalInterval.Off : SignalInterval.Yellow,
                    TicksToChange = 0,
                };
            }

            WriteLanes(w, net, s, slot, mode);
        }
    }

    private static void Advance(SignalStore s, int slot)
    {
        int pc = s.PhaseCount[slot];
        // Green_k >= 1 гарантирует остановку цикла не позже чем за 3 шага.
        while (s.TicksLeft[slot] == 0)
        {
            switch (s.Interval[slot])
            {
                case SignalInterval.Green:
                    s.Interval[slot] = SignalInterval.Yellow;
                    s.TicksLeft[slot] = s.YellowTicks[slot];
                    break;
                case SignalInterval.Yellow:
                    s.Interval[slot] = SignalInterval.AllRed;
                    s.TicksLeft[slot] = s.AllRedTicks[slot];
                    break;
                default:
                    int next = s.Phase[slot] + 1;
                    s.Phase[slot] = (byte)(next >= pc ? 0 : next);
                    s.Interval[slot] = SignalInterval.Green;
                    s.TicksLeft[slot] = s.GreenTicks[(slot * SignalStore.MaxPhases) + s.Phase[slot]];
                    break;
            }
        }
    }

    private static void WriteLanes(World w, NetworkStore net, SignalStore s, int slot, SignalMode mode)
    {
        ref readonly NodeData nd = ref net.Nodes[s.Node[slot]];
        byte[] lanes = w.LaneSignals;
        int first = nd.FirstConnector;
        int count = nd.ConnectorCount;
        if (mode == SignalMode.Off)
        {
            for (int c = 0; c < count; c++)
            {
                lanes[first + c] = (byte)LaneSignal.None;
            }

            return;
        }

        if (mode == SignalMode.FlashYellow)
        {
            for (int c = 0; c < count; c++)
            {
                lanes[first + c] = (byte)LaneSignal.Yellow;
            }

            return;
        }

        SignalInterval iv = s.Interval[slot];
        byte open = iv == SignalInterval.Green ? (byte)LaneSignal.Green : iv == SignalInterval.Yellow ? (byte)LaneSignal.Yellow : (byte)LaneSignal.Red;
        ulong mask = s.PhaseMask[(slot * SignalStore.MaxPhases) + s.Phase[slot]];
        for (int c = 0; c < count; c++)
        {
            lanes[first + c] = c < SignalMasks.MaxMaskedConnectors && ((mask >> c) & 1UL) != 0 ? open : (byte)LaneSignal.Red;
        }
    }
}
