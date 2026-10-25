using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Tests.Intersections;

namespace Sim.Core.Tests.LodTests;

/// <summary>
/// Насыщенный поток одной полосы на зелёном (sim-lod.md §7): один светофорный крест 1+1, на западном въезде постоянная очередь
/// (источник чаще пропускной способности), свободный выезд. Результат - допусков в минуту зелёного на полосу.
/// </summary>
internal static class SaturationProbe
{
    public static (double GrantsPerGreenMin, int Exits) Run(in LodDesc lod, int ticks = 14000, int warmup = 4000, int lanes = 1)
    {
        ScenarioDesc d = GateFixture.Node(NodeControl.RightHandRule, Arm.E(lanes), Arm.S(lanes), Arm.W(lanes), Arm.N(lanes));
        d.Lod = lod;
        using Simulation sim = Simulation.Create(in d);
        Assert.True(sim.Enqueue(Command.MakeSetNodeControl(0, NodeControl.Signal, 1)));
        SignalFixture.EnqueueFixedDefault(sim, 0, 2);   // 2.5: замер на прежнем Fixed 24/24/3/1 (план по умолчанию теперь Adaptive)
        sim.Step(1);
        World w = sim.World;
        var drv = new FlowDriver(sim);
        int inLane = GateFixture.InLane(w, 2);
        for (int l = 0; l < lanes; l++)
        {
            drv.AddSource(inLane + l, 24, toArm: 0);
        }
        for (int a = 0; a < 4; a++)
        {
            for (int l = 0; l < lanes; l++)
            {
                drv.AddSink(GateFixture.OutLane(w, a, l));
            }
        }

        int conn = GateFixture.ConnectorTo(w, inLane, 0);
        long grants = 0;
        long green = 0;
        drv.OnTick = f =>
        {
            if (w.Tick < warmup)
            {
                return;
            }

            for (int l = 0; l < lanes; l++)
            {
                for (int x = w.Occupancy.LaneHead[inLane + l]; x != -1; x = w.Vehicles.Behind[x])
                {
                    if (w.Vehicles.Lane[x] == inLane + l && GateRules.IsGranted(w.Vehicles, x) && (f.PrevGate[x] & GateRules.Granted) == 0 && f.PrevLane[x] == inLane + l)
                    {
                        grants++;
                    }
                }
            }

            if (w.LaneSignals[conn] == (byte)LaneSignal.Green)
            {
                green++;
            }
        };

        drv.Run(ticks);
        return (green == 0 ? 0 : grants * 1200.0 / green / lanes, drv.Exits);
    }
}
