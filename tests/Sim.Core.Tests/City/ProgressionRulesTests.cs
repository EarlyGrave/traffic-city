using System;
using System.Collections.Generic;
using System.Globalization;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Tools;
using Sim.Core.Util;
using Sim.Scenarios;
using Sim.Scenarios.Tools;

namespace Sim.Core.Tests.City;

/// <summary>
/// Вехи и замки (PG3, progression.md §3.3, §4.3): закрытая команда - <c>Locked</c> и в превью (Value = веха), и в применении; undo сноса закрытого профиля проходит; веха берётся ровно
/// один раз (признаки, грант Subsidy, бонус кредита, область); ворота без выполнения - одно <c>MilestoneGated</c>; реплей журнала - тот же хэш.
/// </summary>
public sealed class ProgressionRulesTests
{
    private const int MicroDay = 28_800;   // час = 1 200 тиков
    private const int Hour = MicroDay / 24;

    private static Simulation Make(in ScenarioDesc d, int threads = 1) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });

    private static ScenarioDesc Econ(string section) => ProgressionFixtures.Econ(MicroDay, section).Desc;

    private static int LaneSegment(Simulation sim)
    {
        NetworkStore n = sim.World.Net;
        for (int s = 0; s < n.SegmentCount; s++)
        {
            if (n.SegmentAlive(s) && n.Segments[s].LanesAB > 0)
            {
                return s;
            }
        }

        throw new InvalidOperationException("нет сегмента");
    }

    private static Command BusLane(Simulation sim, int seg, uint tag) =>
        Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Dir = 0, LaneNo = 0, Mask = LaneFlags.BusOnly, Flags = LaneFlags.BusOnly }, tag);

    private static RejectReason Preview(Simulation sim, in Command cmd, out PreviewIssue first)
    {
        var issues = new PreviewIssue[8];
        RejectReason v = sim.Preview(in cmd, out PreviewResult r, issues);
        first = r.IssuesWritten > 0 ? issues[0] : default;
        return v;
    }

    private static int CountEvents(Simulation sim, int ticks, int chunk, SimEventKind kind, List<SimEvent>? seen = null)
    {
        int n = 0;
        for (int done = 0; done < ticks; done += chunk)
        {
            sim.Step(Math.Min(chunk, ticks - done));
            foreach (SimEvent e in sim.Events)
            {
                if (e.Kind == kind)
                {
                    n++;
                    seen?.Add(e);
                }
            }
        }

        return n;
    }

    [Fact]
    public void Locked_PreviewEqualsApply_ValueIsMilestone_AndCounted()
    {
        using Simulation sim = Make(Econ(ProgressionFixtures.Section));
        sim.Step(10);
        int seg = LaneSegment(sim);
        Command bus = BusLane(sim, seg, 1);
        Assert.Equal(RejectReason.Locked, Preview(sim, in bus, out PreviewIssue iss));
        Assert.Equal((IssueCode)(int)RejectReason.Locked, iss.Code);
        Assert.Equal(1, iss.Value);   // bus_lanes открывает m2 (индекс 1)
        Assert.Equal(RejectReason.Locked, ToolsFixture.Run(sim, bus));
        Assert.Equal(1, sim.Counters.LockedRejects);

        // Не закрытые биты той же команды - открыты.
        Command keep = Command.MakeSetLaneFlags(new SetLaneFlagsCmd { Seg = ToolsFixture.Segment(sim, seg), Mask = LaneFlags.NoLaneChangeOut, Flags = 0 }, 2);
        Assert.Equal(RejectReason.None, Preview(sim, in keep, out _));

        // Закрытый профиль arterial_6 - веха m3 (индекс 2), в превью и в применении.
        RoadProfileStore ps = sim.World.Net.Profiles!;
        int p6 = -1;
        for (int p = 0; p < ps.Count; p++)
        {
            if (!sim.IsProfileUnlocked(p))
            {
                p6 = sim.World.Progression!.MilestoneForProfile(p) == 2 ? p : p6;
            }
        }

        Assert.True(p6 >= 0);
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 a = sim.World.Net.Nodes[west].Position;
        BuildRoadCmd road = ToolsFixture.Road(a, new Vec2(a.X - 200f, a.Z));
        road.Profile = (ushort)(p6 + 1);
        road.LanesAB = (byte)ps.LanesAB(p6, false);
        road.LanesBA = (byte)ps.LanesBA(p6, false);
        road.Kind = ps.Kind[p6];
        Command build = Command.MakeBuildRoad(in road, 3);
        Assert.Equal(RejectReason.Locked, Preview(sim, in build, out iss));
        Assert.Equal(2, iss.Value);
        Assert.Equal(RejectReason.Locked, ToolsFixture.Run(sim, build));
        Assert.Equal(2, sim.Counters.LockedRejects);

        // Зонирование закрыто (PaintZone вне валидатора инструментов): превью и применение.
        var paint = Command.MakePaintZone(new PaintZoneCmd { X0 = a.X, Z0 = a.Z - 50f, X1 = a.X + 100f, Z1 = a.Z + 50f, ZoneKind = 1 }, 4);
        Assert.Equal(RejectReason.Locked, Preview(sim, in paint, out iss));
        Assert.Equal(2, iss.Value);   // zoning открывает m3
        Assert.Equal(RejectReason.Locked, ToolsFixture.Run(sim, paint));
        Assert.Equal(3, sim.Counters.LockedRejects);
    }

    [Fact]
    public void NoSection_NothingLocked()
    {
        using Simulation sim = Make(Econ("""{ "enabled": false }"""));
        sim.Step(10);
        Assert.False(sim.HasProgression);
        Command bus = BusLane(sim, LaneSegment(sim), 1);
        Assert.Equal(RejectReason.None, Preview(sim, in bus, out _));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, bus));
        Assert.Equal(0, sim.Counters.LockedRejects);
    }

    [Fact]
    public void Undo_OfLockedProfileDemolish_Passes_ForgedBuildIsLocked()
    {
        // Профиль сегмента генератора закрываем секцией: снос открыт, undo (бит Inverse) обязан вернуть сегмент.
        string? id;
        int seg;
        using (Simulation probe = Make(Econ("""{ "enabled": false }""")))
        {
            seg = -1;
            NetworkStore n0 = probe.World.Net;
            for (int s = 0; s < n0.SegmentCount && seg < 0; s++)
            {
                if (n0.SegmentAlive(s) && n0.Segments[s].Profile >= 0 && !ToolsFixture.HasStop(n0, s))
                {
                    seg = s;
                }
            }

            Assert.True(seg >= 0, "в сети нет сегментов с профилем");
            id = probe.World.Net.Profiles!.Ids[n0.Segments[seg].Profile];
        }

        string section = "{ \"enabled\": true, \"locked_profiles\": [\"" + id + "\"], \"milestones\": [] }";
        using Simulation sim = Make(Econ(section));
        int prof = sim.World.Net.Segments[seg].Profile;
        Assert.False(sim.IsProfileUnlocked(prof));
        var stack = new UndoStack();
        UndoResult rem = stack.Execute(sim, new[] { Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = ToolsFixture.Segment(sim, seg) }, 7) });
        Assert.True(rem.Ok, $"снос: {rem.FirstReason}");
        sim.Step(20);
        UndoResult undo = stack.Undo(sim);
        Assert.True(undo.Ok, $"undo: {undo.FirstReason} {undo.FirstKind}");
        Assert.Equal(0, sim.Counters.LockedRejects);
    }

    private static string GrantSection(int pop, string gates = "") => string.Format(CultureInfo.InvariantCulture, """
        {{
          "enabled": true,
          "locked_features": ["bus_lanes"],
          "min_legs": 10,
          "areas": [ {{ "min_x": -100000, "min_z": -100000, "max_x": 100000, "max_z": 100000 }}, {{ "min_x": -200000, "min_z": -200000, "max_x": 200000, "max_z": 200000 }} ],
          "milestones": [
            {{ "id": "m1", "population": {0}, {1} "unlock": ["bus_lanes"], "credit_days": 7, "area": 1, "grant": 5000 }},
            {{ "id": "m2", "population": 100000000 }}
          ]
        }}
        """, pop, gates);

    [Fact]
    public void Milestone_ReachedExactlyOnce_UnlocksGrantsCreditAndArea()
    {
        using Simulation sim = Make(Econ(GrantSection(1)));
        long subsidy0 = sim.World.City!.Ledger.FlowTotal[(int)FlowKind.Subsidy];
        int seg = LaneSegment(sim);
        sim.Step(Hour - 10);
        Assert.Equal(RejectReason.Locked, Preview(sim, BusLane(sim, seg, 1), out _));
        var seen = new List<SimEvent>();
        int reached = CountEvents(sim, 3 * Hour, 100, SimEventKind.MilestoneReached, seen);
        Assert.Equal(1, reached);
        Assert.Equal(0, seen[0].A);
        Assert.True(seen[0].B >= 1);   // население на момент вехи
        Assert.Equal(Hour - 1, seen[0].Tick);   // последний тик первого игрового часа

        Assert.True(sim.GetProgression(out ProgressionInfo pi));
        Assert.Equal(1, pi.Reached);
        Assert.Equal(7, pi.CreditDaysBonus);
        Assert.Equal(1, pi.AreaStage);
        Assert.True(sim.IsFeatureUnlocked(Feature.BusLanes));
        Assert.True(sim.GetMilestone(0, out MilestoneInfo mi));
        Assert.Equal(Hour - 1, mi.ReachedTick);
        Assert.Equal(5000, sim.World.City.Ledger.FlowTotal[(int)FlowKind.Subsidy] - subsidy0);
        Assert.True(sim.GetCityBudget(out CityBudgetInfo bi));
        Assert.Equal((sim.World.City.Cfg.Economy.CreditDays + 7L) * sim.World.City.Budget!.IncomeEwma, bi.CreditLimit);

        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, BusLane(sim, seg, 2)));
        Assert.Equal(0, CountEvents(sim, 2 * Hour, 200, SimEventKind.MilestoneReached));
    }

    [Fact]
    public void Milestone_GatesFail_GatedOnce_NotReached()
    {
        using Simulation sim = Make(Econ(GrantSection(1, """ "gates": [ { "kpi": "bus_share_pct", "min": 99 } ], """)));
        var seen = new List<SimEvent>();
        Assert.Equal(1, CountEvents(sim, 4 * Hour, 200, SimEventKind.MilestoneGated, seen));
        Assert.Equal(0, seen[0].A);
        Assert.Equal(1, seen[0].B);   // G0 не выполнены
        Assert.True(sim.GetProgression(out ProgressionInfo pi));
        Assert.Equal(0, pi.Reached);
        Assert.False(sim.IsFeatureUnlocked(Feature.BusLanes));
    }

    [Fact]
    public void Area_OutOfBounds_UntilMilestoneOpensIt()
    {
        // Область 0 - bbox сети, область 1 - всё: дорога наружу от крайнего узла вне области 0 до вехи.
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        using (Simulation probe = Make(Econ("""{ "enabled": false }""")))
        {
            NetworkStore n0 = probe.World.Net;
            for (int i = 0; i < n0.NodeCount; i++)
            {
                if (n0.NodeAlive(i))
                {
                    Vec2 p = n0.Nodes[i].Position;
                    minX = Math.Min(minX, p.X);
                    minZ = Math.Min(minZ, p.Z);
                    maxX = Math.Max(maxX, p.X);
                    maxZ = Math.Max(maxZ, p.Z);
                }
            }
        }

        string section = string.Format(CultureInfo.InvariantCulture, """
            {{ "enabled": true,
               "areas": [ {{ "min_x": {0}, "min_z": {1}, "max_x": {2}, "max_z": {3} }}, {{ "min_x": -100000, "min_z": -100000, "max_x": 100000, "max_z": 100000 }} ],
               "milestones": [ {{ "id": "m1", "population": 1, "area": 1 }} ] }}
            """, minX - 1f, minZ - 1f, maxX + 1f, maxZ + 1f);
        using Simulation sim = Make(Econ(section));
        int west = ToolsFixture.WestmostNode(sim);
        Vec2 a = sim.World.Net.Nodes[west].Position;
        Command out1 = Command.MakeBuildRoad(ToolsFixture.Road(a, new Vec2(a.X - 300f, a.Z)), 1);
        Assert.Equal(RejectReason.OutOfBounds, Preview(sim, in out1, out _));
        Assert.Equal(RejectReason.OutOfBounds, ToolsFixture.Run(sim, out1));
        sim.Step(Hour);
        Assert.True(sim.GetProgression(out ProgressionInfo pi));
        Assert.Equal(1, pi.AreaStage);
        Assert.NotEqual(RejectReason.OutOfBounds, Preview(sim, in out1, out _));
    }

    [Fact]
    public void JournalReplay_SameHash_AcrossThreads()
    {
        // Журнал: закрытая команда до вехи, та же после; повтор в свежем мире (1/2/3 потока) - тот же хэш и те же отказы.
        ulong expected = 0;
        for (int threads = 1; threads <= 3; threads++)
        {
            using Simulation sim = Make(Econ(GrantSection(1)), threads);
            int seg = LaneSegment(sim);
            sim.Step(200);
            Assert.Equal(RejectReason.Locked, ToolsFixture.Run(sim, BusLane(sim, seg, 1)));
            sim.Step(Hour);
            Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, BusLane(sim, seg, 2)));
            sim.Step(500);
            ulong h = sim.ComputeStateHash();
            Assert.Equal(1, sim.Counters.LockedRejects);
            if (threads == 1)
            {
                expected = h;
            }
            else
            {
                Assert.Equal(expected, h);
            }
        }
    }
}
