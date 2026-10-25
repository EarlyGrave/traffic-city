using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Intersections;

/// <summary>Явные сети с крестовыми узлами для тестов светофоров.</summary>
internal static class SignalFixture
{
    /// <summary>
    /// Кресты 4-way (центры в (1000·i, 0), рукава E, S, W, N по 100 м) с <paramref name="lanes"/> полос в каждую сторону.
    /// Если <paramref name="fiveWayFirst"/>, у первого креста добавлен диагональный рукав NE (итого 5 рукавов, 3 оси).
    /// Control центров = None (светофоры заводятся командой/напрямую).
    /// </summary>
    public static ScenarioDesc Crosses(int count, int lanes = 1, int maxSignals = 4, bool fiveWayFirst = false)
    {
        ScenarioDesc d = TestScenarios.Line();
        SimCapacities c = d.Capacities;
        c.MaxNodes = 64;
        c.MaxSegments = 64;
        c.MaxLanes = 2048;
        c.MaxLanePoints = 16384;
        c.MaxSignalizedNodes = maxSignals;
        c.EventCapacity = 256;
        c.CommandQueueCapacity = 32;
        d.Capacities = c;

        var nodes = new List<NodeDesc>();
        var segs = new List<SegmentDesc>();
        (float, float)[] ends = { (100, 0), (0, 100), (-100, 0), (0, -100), (70, -70) };
        for (int i = 0; i < count; i++)
        {
            float cx = 1000f * i;
            int center = nodes.Count;
            nodes.Add(new NodeDesc { X = cx, Z = 0, Control = NodeControl.None });
            int n = (fiveWayFirst && i == 0) ? 5 : 4;
            for (int k = 0; k < n; k++)
            {
                int e = nodes.Count;
                nodes.Add(new NodeDesc { X = cx + ends[k].Item1, Z = ends[k].Item2, Control = NodeControl.None });
                segs.Add(new SegmentDesc { A = center, B = e, LanesAB = (byte)lanes, LanesBA = (byte)lanes, SpeedLimit = 13.9f, Kind = RoadKind.Street });
            }
        }

        d.Network = new NetworkDesc { Nodes = nodes.ToArray(), Segments = segs.ToArray() };
        d.Rules.DefaultYellowTicks = 60;
        d.Rules.DefaultAllRedTicks = 20;
        return d;
    }

    /// <summary>Индекс центра i-го креста в <see cref="Crosses"/>.</summary>
    public static int Center(int i, bool fiveWayFirst = false) => (i * 5) + ((fiveWayFirst && i > 0) ? 1 : 0);

    public static SetSignalPlanCmd Plan(int g0, int g1, int yellow, int allRed, int offset = 0) => new()
    {
        Mode = SignalMode.Fixed,
        PhaseCount = 2,
        Green0 = (ushort)g0,
        Green1 = (ushort)g1,
        YellowTicks = (ushort)yellow,
        AllRedTicks = (ushort)allRed,
        OffsetTicks = (ushort)offset,
    };

    /// <summary>
    /// Ставит в очередь прежний план по умолчанию этапа 1 (<c>Fixed</c> 24 с / 24 с, жёлтый 3 с, all-red 1 с) для узла, получившего светофор командой
    /// <c>SetNodeControl</c>: с 2.5 (ADR-0018) план по умолчанию - <c>Adaptive</c>, а тесты ворот и коридора опираются на фиксированный цикл.
    /// Команда идёт сразу за <c>SetNodeControl</c> (тот же тик), поэтому состояние автомата то же, что у прежнего плана по умолчанию.
    /// </summary>
    public static void EnqueueFixedDefault(Simulation sim, int node, uint tag)
    {
        SetSignalPlanCmd p = Plan(480, 480, 60, 20);
        p.Node = node;
        Xunit.Assert.True(sim.Enqueue(Command.MakeSetSignalPlan(in p, tag)));
    }

    /// <summary>Ожидаемое (фаза, интервал, остаток) в позиции <paramref name="pos"/> цикла.</summary>
    public static (int Phase, SignalInterval Iv, int Left) Expected(int[] green, int yellow, int allRed, long pos)
    {
        int cycle = green.Length * (yellow + allRed);
        for (int k = 0; k < green.Length; k++)
        {
            cycle += green[k];
        }

        long p = pos % cycle;
        for (int k = 0; k < green.Length; k++)
        {
            int[] lens = { green[k], yellow, allRed };
            for (int iv = 0; iv < 3; iv++)
            {
                if (p < lens[iv])
                {
                    return (k, (SignalInterval)iv, (int)(lens[iv] - p));
                }

                p -= lens[iv];
            }
        }

        throw new InvalidOperationException();
    }

    /// <summary>true: connector идёт «прямо» вдоль оси X (вход и выход на одной Z).</summary>
    public static bool IsStraightX(NetworkStore net, in LaneData c)
    {
        Vec2 a = net.LanePoints[c.FirstPoint];
        Vec2 b = net.LanePoints[c.FirstPoint + c.PointCount - 1];
        return MathF.Abs(b.X - a.X) > 3f && MathF.Abs(b.Z - a.Z) < 0.5f;
    }

    public static bool IsStraightZ(NetworkStore net, in LaneData c)
    {
        Vec2 a = net.LanePoints[c.FirstPoint];
        Vec2 b = net.LanePoints[c.FirstPoint + c.PointCount - 1];
        return MathF.Abs(b.Z - a.Z) > 3f && MathF.Abs(b.X - a.X) < 0.5f;
    }
}
