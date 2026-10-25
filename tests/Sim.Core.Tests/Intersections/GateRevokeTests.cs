using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Intersections;

/// <summary>Снятие допуска на красном (intersections.md §5.4 п. 6, ADR-0019 R1).</summary>
public class GateRevokeTests
{
    private readonly ITestOutputHelper _out;

    public GateRevokeTests(ITestOutputHelper output) => _out = output;

    private static Simulation SignalCross()
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(1), Arm.S(1), Arm.W(1), Arm.N(1));
        Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        sim.Step(1);
        Assert.Equal(NodeControl.Signal, sim.World.Net.Nodes[0].Control);
        return sim;
    }

    /// <summary>Шагает до тика, когда сигнал connector'а равен <paramref name="want"/>.</summary>
    private static void StepUntil(Simulation sim, int connector, LaneSignal want)
    {
        for (int t = 0; t < 4000 && sim.World.LaneSignals[connector] != (byte)want; t++)
        {
            sim.Step(1);
        }

        Assert.Equal((byte)want, sim.World.LaneSignals[connector]);
    }

    /// <summary>Ручной допуск (как фаза 4): флаг, connector, билет узла.</summary>
    private static void Grant(World w, int vehicle, int connector)
    {
        w.Vehicles.GateState[vehicle] = GateRules.Granted;
        w.Vehicles.GrantConnector[vehicle] = connector;
        w.Vehicles.Ticket[vehicle] = w.Net.NodeTicket[0]++;
    }

    private static bool Granted(World w, int vehicle) => GateRules.IsGranted(w.Vehicles, vehicle);

    /// <summary>
    /// Допущенная, которая ещё комфортно останавливается, теряет допуск вместе со всеми допущенными за ней; та, что остановиться не успевает, -
    /// нет, и допущенные остаются префиксом полосы. Машина на connector'е не трогается. 
    /// </summary>
    [Fact]
    public void Red_RevokesStoppableAndFollowers_KeepsPrefix_SkipsConnectorVehicles()
    {
        using Simulation sim = SignalCross();
        World w = sim.World;
        int lane = GateFixture.InLane(w, 0);
        int conn = GateFixture.ConnectorTo(w, lane, 2);
        Assert.True(conn >= 0);
        float len = w.Net.LaneLength[lane];

        StepUntil(sim, conn, LaneSignal.Red);

        // Головной допущен у самой линии на скорости, при которой остановиться нельзя; за ним две допущенные далеко.
        int head = GateFixture.Place(sim, lane, len - 3f, 13f, conn);
        int mid = GateFixture.Place(sim, lane, 150f, 5f, conn);
        int tail = GateFixture.Place(sim, lane, 120f, 5f, conn);
        Assert.True(w.Occupancy.LaneHead[lane] == head);
        Grant(w, head, conn);
        Grant(w, mid, conn);
        Grant(w, tail, conn);
        sim.Step(1);
        Assert.True(Granted(w, head), "не успевающая остановиться сохраняет допуск");
        Assert.False(Granted(w, mid));
        Assert.False(Granted(w, tail));
        Assert.Equal(-1, w.Vehicles.GrantConnector[mid]);
        Assert.Equal(2, sim.Counters.GrantsRevoked);
        _out.WriteLine($"revoked={sim.Counters.GrantsRevoked}");
    }

    /// <summary>На зелёном допущенные далеко от линии не отзываются.</summary>
    [Fact]
    public void Green_RevokesNothing()
    {
        using Simulation sim = SignalCross();
        World w = sim.World;
        int lane = GateFixture.InLane(w, 0);
        int conn = GateFixture.ConnectorTo(w, lane, 2);
        StepUntil(sim, conn, LaneSignal.Green);
        int g0 = GateFixture.Place(sim, lane, 100f, 5f, conn);
        Grant(w, g0, conn);
        sim.Step(1);
        Assert.True(Granted(w, g0));
        Assert.Equal(0, sim.Counters.GrantsRevoked);
    }

    /// <summary>Допущенный, стоящий на connector'е, сигнал которого стал красным, не теряет допуск (он уже в узле).</summary>
    [Fact]
    public void Red_DoesNotTouchVehicleOnConnector()
    {
        using Simulation sim = SignalCross();
        World w = sim.World;
        int lane = GateFixture.InLane(w, 0);
        int conn = GateFixture.ConnectorTo(w, lane, 2);
        StepUntil(sim, conn, LaneSignal.Red);
        int onConn = GateFixture.Place(sim, conn, 3f, 2f);
        Grant(w, onConn, conn);
        sim.Step(1);
        Assert.Equal(conn, w.Vehicles.Lane[onConn]);
        Assert.True(Granted(w, onConn));
        Assert.Equal(0, sim.Counters.GrantsRevoked);
    }

    /// <summary>
    /// Синтетика «поперечная колонна ползёт на красном»: колонна с допуском едет к линии с 100 м на скорости 3 м/с (допущена на своём зелёном/жёлтом),
    /// у линии поперечной оси стоит голова, ждущая своего зелёного. Допуск колонны отзывается на красном, голова получает зелёный сразу после
    /// его начала (без отзыва колонна держала бы коробку ещё ≈ 33 с).
    /// </summary>
    [Fact]
    public void CrawlingColumnOnRed_IsRevoked_OncomingHeadGetsGreen()
    {
        using Simulation sim = SignalCross();
        World w = sim.World;
        int laneA = GateFixture.InLane(w, 0);     // с востока на запад
        int connA = GateFixture.ConnectorTo(w, laneA, 2);
        int laneB = GateFixture.InLane(w, 1);     // с юга на север - пересекает ось A
        int connB = GateFixture.ConnectorTo(w, laneB, 3);
        Assert.True(connA >= 0 && connB >= 0);
        float lenB = w.Net.LaneLength[laneB];

        int head = GateFixture.Place(sim, laneB, lenB - 1.2f, 0f, connB);
        StepUntil(sim, connA, LaneSignal.Yellow);
        Assert.Equal((byte)LaneSignal.Red, w.LaneSignals[connB]);

        var column = new int[3];
        for (int k = 0; k < column.Length; k++)
        {
            column[k] = GateFixture.Place(sim, laneA, 100f - (k * 10f), 3f, connA);
            w.Vehicles.DesiredSpeed[column[k]] = 3f;
            Grant(w, column[k], connA);
        }

        long revoked0 = sim.Counters.GrantsRevoked;
        int grantedAt = -1;
        int greenAt = -1;
        for (int t = 0; t < 600 && grantedAt < 0; t++)
        {
            sim.Step(1);
            if (greenAt < 0 && w.LaneSignals[connB] == (byte)LaneSignal.Green)
            {
                greenAt = t;
            }

            if (Granted(w, head) || w.Vehicles.Lane[head] != laneB)
            {
                grantedAt = t;
            }
        }

        _out.WriteLine($"greenAt={greenAt} headGrantedAt={grantedAt} revoked={sim.Counters.GrantsRevoked - revoked0}");
        Assert.Equal(3, sim.Counters.GrantsRevoked - revoked0);
        Assert.True(greenAt >= 0 && grantedAt >= greenAt, "голова получила допуск не раньше своего зелёного");
        Assert.True(grantedAt - greenAt <= 40, $"голова ждала {grantedAt - greenAt} тиков после начала зелёного");
        for (int k = 0; k < column.Length; k++)
        {
            Assert.False(Granted(w, column[k]) && w.Vehicles.Lane[column[k]] == laneA, "колонна не должна остаться допущенной на красном");
        }
    }
}
