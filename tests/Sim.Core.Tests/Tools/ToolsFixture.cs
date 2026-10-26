using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Save;
using Sim.Core.Util;

namespace Sim.Core.Tests.Tools;

/// <summary>Общие помощники тестов инструментов игрока (4.3, T3/T4): малый мир сохранения с оверлеем, поиск узлов, постановка команд и сверки.</summary>
internal static class ToolsFixture
{
    /// <summary>Описание малого мира (город 1.2 x 1.2 км, ~100 машин, оверлей, светофоры, автобусы); <paramref name="peds"/> - с пешеходами.</summary>
    public static ScenarioDesc SmallDesc(bool buses = true, bool peds = false)
    {
        ScenarioDesc d = WorldSaveFixtures.Small(buses);
        if (peds)
        {
            d.Peds = new PedDesc { Enabled = true, Count = 40, MidblockAtStops = true, ArterialCrossings = ArterialCrossings.SignalOnly };
            SimCapacities c = d.Capacities;
            c.MaxPeds = 80;
            c.MaxResidents = 80;
            c.MaxPedPathSteps = 64;
            d.Capacities = c;
        }

        return d;
    }

    /// <summary>Малый мир, прогретый на <paramref name="warmTicks"/>.</summary>
    public static Simulation Small(int warmTicks = 400, int threads = 1, bool buses = true, bool peds = false)
    {
        Simulation sim = Simulation.Create(SmallDesc(buses, peds), new SimRuntimeOptions { WorkerThreads = threads });
        sim.Step(warmTicks);
        return sim;
    }

    /// <summary>Применить одну команду за один <c>Step(1)</c>; возвращает причину отказа (<c>None</c> - применена).</summary>
    public static RejectReason Run(Simulation sim, in Command cmd)
    {
        Assert.True(sim.Enqueue(cmd));
        sim.Step(1);
        RejectReason r = RejectReason.None;
        bool seen = false;
        foreach (SimEvent e in sim.Events)
        {
            if (e.Tag == cmd.Tag && e.Kind == SimEventKind.CommandRejected)
            {
                r = (RejectReason)e.A;
                seen = true;
            }
            else if (e.Tag == cmd.Tag && e.Kind == SimEventKind.CommandApplied)
            {
                seen = true;
            }
        }

        Assert.True(seen, "команда не дала ни Applied, ни Rejected");
        return r;
    }

    /// <summary>Повторять команду (по одному тику), пока её не отклоняет <c>Busy</c>: перестройка узла ждёт, пока уйдут машины с исчезающего connector'а.</summary>
    public static RejectReason RunUntilNotBusy(Simulation sim, Func<Command> make, int attempts = 400)
    {
        RejectReason r = RejectReason.Busy;
        for (int i = 0; i < attempts && r == RejectReason.Busy; i++)
        {
            r = Run(sim, make());
        }

        return r;
    }

    public static EntityRef Node(Simulation sim, int node) => new(node, sim.World.Net.Nodes[node].Generation);

    public static EntityRef Segment(Simulation sim, int seg) => new(seg, sim.World.Net.Segments[seg].Generation);

    /// <summary>Первый построенный узел с <paramref name="arms"/> рукавами и режимом <paramref name="control"/> (или -1).</summary>
    public static int FindNode(Simulation sim, int arms, NodeControl control, int skip = 0)
    {
        NetworkStore n = sim.World.Net;
        for (int i = 0; i < n.NodeCount; i++)
        {
            if (n.NodeAlive(i) && n.Nodes[i].FirstArm >= 0 && n.Nodes[i].ArmCount == arms && n.Nodes[i].Control == control && skip-- <= 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Номер рукава (слот в <c>NodeArms</c>) сегмента <paramref name="seg"/> у узла.</summary>
    public static int ArmOf(Simulation sim, int node, int seg)
    {
        NetworkStore n = sim.World.Net;
        for (int a = 0; a < n.Nodes[node].ArmCount; a++)
        {
            if (n.NodeArms[n.Nodes[node].FirstArm + a] == seg)
            {
                return a;
            }
        }

        return -1;
    }

    /// <summary>Рукава (from, to) connector'а <paramref name="p"/> узла.</summary>
    public static (int From, int To) ConnectorArms(Simulation sim, int node, int p)
    {
        NetworkStore n = sim.World.Net;
        ref readonly LaneData cl = ref n.Lanes[n.Nodes[node].FirstConnector + p];
        return (ArmOf(sim, node, n.Lanes[cl.FromLane].Segment), ArmOf(sim, node, n.Lanes[cl.ToLane].Segment));
    }

    /// <summary>Сколько connector'ов узла идут с рукава <paramref name="from"/> на рукав <paramref name="to"/>.</summary>
    public static int CountConnectors(Simulation sim, int node, int from, int to)
    {
        NetworkStore n = sim.World.Net;
        int c = 0;
        for (int p = 0; p < n.Nodes[node].ConnectorCount; p++)
        {
            (int f, int t) = ConnectorArms(sim, node, p);
            if (f == from && t == to)
            {
                c++;
            }
        }

        return c;
    }

    /// <summary>Сейв -> загрузка -> сейв побайтно равны, хэш после загрузки тот же (TL4); возвращает загруженный мир.</summary>
    public static Simulation RoundTrip(Simulation sim, int threads = 1)
    {
        byte[] a = WorldSaveFixtures.SaveBytes(sim);
        Simulation loaded = WorldSaveFixtures.Load(a, threads, strict: false);
        if (loaded.ComputeStateHash() != sim.ComputeStateHash())
        {
            byte[] c = WorldSaveFixtures.SaveBytes(loaded);
            int at = -1;
            for (int i = 0; i < Math.Min(a.Length, c.Length) && at < 0; i++)
            {
                if (a[i] != c[i] && !WorldSaveFixtures.Locate(a, i).StartsWith("header", StringComparison.Ordinal))
                {
                    at = i;
                }
            }

            Assert.Fail($"хэш после загрузки не равен: первый различающийся байт {at}: {(at >= 0 ? WorldSaveFixtures.Locate(a, at) : "размеры " + a.Length + "/" + c.Length)}");
        }

        byte[] b = WorldSaveFixtures.SaveBytes(loaded);
        Assert.Equal(a.Length, b.Length);
        Assert.True(a.AsSpan().SequenceEqual(b), "Save -> Load -> Save не побайтно");
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        return loaded;
    }

    /// <summary>Узел у края сети (наименьший X среди построенных) и его позиция: от него можно вести дорогу наружу.</summary>
    public static int WestmostNode(Simulation sim)
    {
        NetworkStore n = sim.World.Net;
        int best = -1;
        for (int i = 0; i < n.NodeCount; i++)
        {
            if (n.NodeAlive(i) && n.Nodes[i].FirstArm >= 0 && (best < 0 || n.Nodes[i].Position.X < n.Nodes[best].Position.X))
            {
                best = i;
            }
        }

        return best;
    }

    public static BuildRoadCmd Road(Vec2 a, Vec2 b, int lanes = 1) => new()
    {
        Ax = a.X,
        Az = a.Z,
        Bx = b.X,
        Bz = b.Z,
        SpeedLimit = 13.9f,
        LanesAB = (byte)lanes,
        LanesBA = (byte)lanes,
        Kind = RoadKind.Street,
    };

    // Самый загруженный сегмент без остановок: на нём машин больше всего (снос заведомо ждёт).
    public static int BusiestSegment(Simulation sim)
    {
        World w = sim.World;
        NetworkStore n = w.Net;
        int best = -1;
        int bestCount = -1;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (!n.SegmentAlive(s) || HasStop(n, s))
            {
                continue;
            }

            int c = ToolOpsPhase.CountOn(w, s);
            if (c > bestCount)
            {
                bestCount = c;
                best = s;
            }
        }

        return best;
    }

    public static bool HasStop(NetworkStore n, int seg)
    {
        for (int i = 0; i < n.BusStopCount; i++)
        {
            if (n.BusStopAlive(i) && n.Lanes[n.BusStops[i].Lane].Segment == seg)
            {
                return true;
            }
        }

        return false;
    }
}
