using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.City;

namespace Sim.Core.Tests.Tools;

/// <summary>Обратная запись и превью <c>PaintZone</c> (UI-C1, E4; ui-spec.md §5.2, zoning-demand.md §14): undo возвращает вид, плотность и <c>Painted</c>; превью считает то же, что делает применение.</summary>
public sealed class PaintZoneToolsTests
{
    private static Simulation Make() => Simulation.Create(CityFixtures.S1kRes(), new SimRuntimeOptions { WorkerThreads = 1 });

    private static PaintZoneCmd Rect(LotStore l, int from, int to, byte zone, byte density = 0)
    {
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        for (int i = from; i < to; i++)
        {
            x0 = MathF.Min(x0, l.X[i]);
            x1 = MathF.Max(x1, l.X[i]);
            z0 = MathF.Min(z0, l.Z[i]);
            z1 = MathF.Max(z1, l.Z[i]);
        }

        return new PaintZoneCmd { X0 = x0 - 0.25f, Z0 = z0 - 0.25f, X1 = x1 + 0.25f, Z1 = z1 + 0.25f, ZoneKind = zone, Density = density };
    }

    private static (byte[] Zone, byte[] Density, byte[] Flags) Lots(Simulation sim)
    {
        LotStore l = sim.World.City!.Lots;
        return (l.Zone.AsSpan(0, l.Count).ToArray(), l.Density.AsSpan(0, l.Count).ToArray(), l.Flags.AsSpan(0, l.Count).ToArray());
    }

    private static int AbandonedCount(CityWorld c)
    {
        int n = 0;
        for (int i = 0; i < c.Lots.Count; i++)
        {
            int bi = c.Lots.Building[i];
            n += bi >= 0 && c.Buildings.IsAlive(bi) && c.Buildings.State[bi] == (byte)BuildingState.Abandoned ? 1 : 0;
        }

        return n;
    }

    [Fact]
    public void PaintZone_Undo_RestoresZoneDensityAndPaintedFlag_MixedPriorStates()
    {
        using Simulation sim = Make();
        LotStore l = sim.World.City!.Lots;
        Assert.True(l.Count > 12);

        // Разные прежние состояния: два участка уже нарисованы игроком (другой вид и плотность), остальные - как сгенерированы.
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakePaintZone(new PaintZoneCmd { X0 = l.X[2], Z0 = l.Z[2], X1 = l.X[2], Z1 = l.Z[2], ZoneKind = (byte)LotZone.Industrial, Density = 1 }, 1)));
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakePaintZone(new PaintZoneCmd { X0 = l.X[5], Z0 = l.Z[5], X1 = l.X[5], Z1 = l.Z[5], ZoneKind = (byte)LotZone.Residential }, 2)));
        var before = Lots(sim);

        var paint = Command.MakePaintZone(Rect(l, 0, 10, (byte)LotZone.Commercial, 1), 3);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, paint));
        var painted = Lots(sim);
        Assert.NotEqual(before.Zone, painted.Zone);

        InverseRecord rec = default;
        foreach (InverseRecord r in sim.Inverses)
        {
            if (r.Tag == 3)
            {
                rec = r;
            }
        }

        Assert.Equal(0, rec.Flags);
        Assert.True(rec.Count >= 1);
        Command[] inv = sim.InverseCommands.Slice(rec.First, rec.Count).ToArray();
        Assert.All(inv, c =>
        {
            Assert.Equal(CommandKind.PaintZone, c.Kind);
            Assert.Equal(CommandFlags.Inverse, c.Flags);
        });

        for (int i = 0; i < inv.Length; i++)
        {
            inv[i].Tag = (uint)(100 + i);
            Assert.True(sim.Enqueue(inv[i]));
        }

        sim.Step(1);
        var after = Lots(sim);
        Assert.Equal(before.Zone, after.Zone);
        Assert.Equal(before.Density, after.Density);
        Assert.Equal(before.Flags, after.Flags);
    }

    [Fact]
    public void PaintZone_Undo_OfUndo_IsRedo()
    {
        using Simulation sim = Make();
        LotStore l = sim.World.City!.Lots;
        var before = Lots(sim);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, Command.MakePaintZone(Rect(l, 3, 9, (byte)LotZone.Industrial), 1)));
        var painted = Lots(sim);
        Command[] inv = Collect(sim, 500);
        foreach (Command c in inv)
        {
            sim.Enqueue(c);
        }

        sim.Step(1);
        Assert.Equal(before.Flags, Lots(sim).Flags);
        Assert.Equal(before.Zone, Lots(sim).Zone);
        Command[] redo = Collect(sim, 1000);
        Assert.NotEmpty(redo);
        foreach (Command c in redo)
        {
            sim.Enqueue(c);
        }

        sim.Step(1);
        Assert.Equal(painted.Zone, Lots(sim).Zone);
        Assert.Equal(painted.Flags, Lots(sim).Flags);
    }

    // Обратные команды шага в порядке постановки undo/redo: записи в обратном порядке (player-tools.md §8.3), внутри записи - как отдало ядро.
    private static Command[] Collect(Simulation sim, uint tag)
    {
        var list = new System.Collections.Generic.List<Command>();
        for (int ri = sim.Inverses.Length - 1; ri >= 0; ri--)
        {
            InverseRecord r = sim.Inverses[ri];
            for (int k = 0; k < r.Count; k++)
            {
                Command c = sim.InverseCommands[r.First + k];
                c.Tag = tag + (uint)list.Count;
                list.Add(c);
            }
        }

        return list.ToArray();
    }

    [Fact]
    public void PaintZone_Undo_ReportsTruncated_WhenMoreThan64Exceptions()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.Town.Zoning.MaxPaintM2 = 1_000_000_000;
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        LotStore l = sim.World.City!.Lots;
        int n = Math.Min(l.Count, 150);
        for (int i = 0; i < n; i++)
        {
            sim.Enqueue(Command.MakePaintZone(new PaintZoneCmd { X0 = l.X[i], Z0 = l.Z[i], X1 = l.X[i], Z1 = l.Z[i], ZoneKind = (byte)(1 + (i % 3)), Density = (byte)(i & 1) }, (uint)(1 + i)));
        }

        sim.Step(1);
        var all = Command.MakePaintZone(Rect(l, 0, n, (byte)LotZone.Commercial), 999);
        var issues = new PreviewIssue[8];
        RejectReason v = sim.Preview(in all, out PreviewResult pr, issues);
        Assert.Equal(RejectReason.None, v);
        Assert.Contains(issues.AsSpan(0, pr.IssuesWritten).ToArray(), x => x.Code == IssueCode.UndoUnavailable);
        sim.Enqueue(all);
        sim.Step(1);
        Assert.Equal(InverseFlags.Truncated, sim.Inverses[sim.Inverses.Length - 1].Flags);
    }

    [Fact]
    public void PaintZone_Preview_CountsEqualWhatApplyDoes_AndDoesNotMutate()
    {
        using Simulation sim = Make();
        CityWorld c = sim.World.City!;
        LotStore l = c.Lots;
        int lotWithBuilding = -1;
        for (int i = 0; i < l.Count && lotWithBuilding < 0; i++)
        {
            lotWithBuilding = l.Building[i] >= 0 ? i : -1;
        }

        Assert.True(lotWithBuilding >= 0);
        PaintZoneCmd rect = Rect(l, Math.Max(0, lotWithBuilding - 4), lotWithBuilding + 5, (byte)((byte)c.Types.Rows[c.Buildings.Type[l.Building[lotWithBuilding]]].Kind % 3 + 1), 1);
        var cmd = Command.MakePaintZone(rect, 5);
        ulong hash = sim.ComputeStateHash();
        var issues = new PreviewIssue[8];
        RejectReason v = sim.Preview(in cmd, out PreviewResult pr, issues);
        Assert.Equal(hash, sim.ComputeStateHash());
        Assert.Equal(RejectReason.None, v);
        Assert.Equal(0, pr.Cost);
        Assert.True(pr.ZoneLots >= 1);
        Assert.True(pr.ZoneAbandon >= 1);
        Assert.Contains(issues.AsSpan(0, pr.IssuesWritten).ToArray(), x => x.Code == IssueCode.ZoneBuildingsAbandoned && x.Value == pr.ZoneAbandon);

        var marks = new PaintLotMark[512];
        int total = sim.PreviewPaintMarks(in rect, marks);
        Assert.Equal(pr.ZoneLots, total);
        int abandonMarks = 0;
        for (int i = 0; i < total; i++)
        {
            abandonMarks += marks[i].State == PaintLotMarkState.ZonedAbandon ? 1 : 0;
        }

        Assert.Equal(pr.ZoneAbandon, abandonMarks);

        var beforeLots = Lots(sim);
        int abandonedBefore = AbandonedCount(c);
        Assert.Equal(RejectReason.None, ToolsFixture.Run(sim, cmd));
        var afterLots = Lots(sim);
        int changed = 0;
        for (int i = 0; i < l.Count; i++)
        {
            changed += beforeLots.Zone[i] != afterLots.Zone[i] || beforeLots.Density[i] != afterLots.Density[i] || beforeLots.Flags[i] != afterLots.Flags[i] ? 1 : 0;
        }

        Assert.Equal(pr.ZoneLotsChanged, changed);
        Assert.Equal(pr.ZoneAbandon, AbandonedCount(c) - abandonedBefore);
    }

    [Fact]
    public void PaintZone_Preview_SameVerdictAsApply_ForRejections()
    {
        using Simulation sim = Make();
        LotStore l = sim.World.City!.Lots;
        var big = Command.MakePaintZone(new PaintZoneCmd { X0 = 0, Z0 = 0, X1 = 5000, Z1 = 5000, ZoneKind = 1 }, 1);
        var nan = Command.MakePaintZone(new PaintZoneCmd { X0 = float.NaN, ZoneKind = 1 }, 2);
        var badKind = Command.MakePaintZone(new PaintZoneCmd { X0 = 0, Z0 = 0, X1 = 10, Z1 = 10, ZoneKind = 9 }, 3);
        var empty = Command.MakePaintZone(new PaintZoneCmd { X0 = 5e5f, Z0 = 5e5f, X1 = 5e5f + 10, Z1 = 5e5f + 10, ZoneKind = 1 }, 4);
        foreach (Command cmd in new[] { big, nan, badKind, empty })
        {
            RejectReason pv = sim.Preview(in cmd, out PreviewResult pr, new PreviewIssue[4]);
            Assert.Equal(pv, ToolsFixture.Run(sim, cmd));
            Assert.Equal(pv, pr.Verdict);
        }

        Assert.Equal(RejectReason.ZoneRule, sim.Preview(in big, out _, new PreviewIssue[4]));
        Assert.True(l.Count > 0);
        sim.Preview(in empty, out PreviewResult none, new PreviewIssue[4]);
        Assert.Equal(0, none.ZoneLots);
        Assert.True(none.ZoneUnavailableCells > 0);
    }
}
