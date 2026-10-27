using System;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tests.Gen;

/// <summary>RP9 (road-profiles.md §5): генератор на мировой сетке 8 м - block_m точно, снап после джиттера, топология не меняется.</summary>
public class GridSnapTests
{
    private const float Module = 8f;

    private static (CreateStatus Status, CityGenResult Res, NetworkStore Net) Gen(GenRun run)
    {
        var net = new NetworkStore(run.Caps);
        var b = new NetworkBuilder(net, run.City.LaneWidth, run.City.ConnectorSamples, NetworkOptions.CornerRadiusFromDesc(run.City.CornerRadius));
        Pcg32 rng = Pcg32.ForStream(run.Seed, RngStream.CityGen);
        CreateStatus st = CityGenerator.Generate(run.City, run.Rules, run.Caps, b, ref rng, out CityGenResult res);
        return (st, res, net);
    }

    private static void AssertSameTopology(CityGenResult a, CityGenResult b, string name)
    {
        Assert.True(a.NodeCount == b.NodeCount, $"{name}: nodes {a.NodeCount} vs {b.NodeCount}");
        Assert.True(a.SegmentCount == b.SegmentCount, $"{name}: segments {a.SegmentCount} vs {b.SegmentCount}");
        Assert.True(a.RemovedStreets == b.RemovedStreets, $"{name}: removed {a.RemovedStreets} vs {b.RemovedStreets}");
        Assert.True(a.RepairedEdges == b.RepairedEdges, $"{name}: repaired {a.RepairedEdges} vs {b.RepairedEdges}");
        Assert.True(a.ShiftedNodes == b.ShiftedNodes, $"{name}: shifted {a.ShiftedNodes} vs {b.ShiftedNodes}");
        Assert.True(a.MergedNodes == b.MergedNodes, $"{name}: merged {a.MergedNodes} vs {b.MergedNodes}");
        Assert.True(a.IrregularSkipped == b.IrregularSkipped, $"{name}: skipped {a.IrregularSkipped} vs {b.IrregularSkipped}");
        Assert.True(a.SignalizedNodes == b.SignalizedNodes, $"{name}: signals {a.SignalizedNodes} vs {b.SignalizedNodes}");
        Assert.True(a.Nx == b.Nx && a.Nz == b.Nz, $"{name}: grid {a.Nx}x{a.Nz} vs {b.Nx}x{b.Nz}");
    }

    /// <summary>Сценарии лестницы (3040 / 152) со снапом дают ту же топологию, что прежние 3000 / 150 без снапа: узлы, сегменты, удалённые улицы, нерегулярные узлы.</summary>
    [Theory]
    [InlineData("s1k")]
    [InlineData("s10k")]
    [InlineData("s100k")]
    [InlineData("s1m")]
    [InlineData("s1k-res")]
    [InlineData("s10k-res")]
    [InlineData("s100k-res")]
    public void Topology_SameAsBeforeOnLadder(string scenario)
    {
        GenRun prev = GenFixture.Load(scenario);
        Assert.Equal(3040f, prev.City.SizeX);
        Assert.Equal(152f, prev.City.BlockSize);
        prev.City.SizeX = 3000f;   // прежнее состояние: 3000 / 150, без снапа
        prev.City.SizeZ = 3000f;
        prev.City.BlockSize = 150f;
        prev.City.SnapNodes = false;
        (CreateStatus st0, CityGenResult r0, _) = Gen(prev);
        Assert.Equal(CreateStatus.Ok, st0);

        GenRun cur = GenFixture.Load(scenario);
        cur.City.SnapNodes = true;
        (CreateStatus st1, CityGenResult r1, NetworkStore net) = Gen(cur);
        Assert.Equal(CreateStatus.Ok, st1);
        AssertSameTopology(r0, r1, scenario);
        Assert.Equal(152f, r1.BlockX);
        Assert.Equal(152f, r1.BlockZ);

        // число демографических зон прежнее: ceil(width / zone_m) (zone_m 508 вместо 500, иначе 3040 / 500 дало бы 7 вместо 6 зон на сторону)
        Assert.Equal(MathF.Ceiling(r0.Width / 500f), MathF.Ceiling(r1.Width / cur.Demand.ZoneSize));

        // без снапа на новых размерах (3040 / 152) - та же топология и шаг w / nx = 152
        GenRun noSnap = GenFixture.Load(scenario);
        (CreateStatus st2, CityGenResult r2, _) = Gen(noSnap);
        Assert.Equal(CreateStatus.Ok, st2);
        AssertSameTopology(r0, r2, scenario + " (no snap)");

        // все узлы на сетке 8 м
        for (int i = 0; i < net.NodeCount; i++)
        {
            Vec2 p = net.Nodes[i].Position;
            Assert.True(p.X % Module == 0f && p.Z % Module == 0f, $"{scenario}: node {i} at ({p.X}, {p.Z}) is off the 8 m grid");
        }
    }

    [Fact]
    public void Snap_Jitter_StaysOnGridWithSamePrngDraws()
    {
        GenRun a = GenFixture.Load("s1k");
        a.City.NodeJitter = 20f;
        a.City.SnapNodes = true;
        (CreateStatus sa, CityGenResult ra, NetworkStore na) = Gen(a);
        Assert.Equal(CreateStatus.Ok, sa);
        for (int i = 0; i < na.NodeCount; i++)
        {
            Vec2 p = na.Nodes[i].Position;
            Assert.True(p.X % Module == 0f && p.Z % Module == 0f, $"node {i} at ({p.X}, {p.Z}) is off the 8 m grid");
        }

        // тот же сид без снапа: число draw прежнее, поэтому состояние PRNG после генератора совпадает
        GenRun b = GenFixture.Load("s1k");
        b.City.NodeJitter = 20f;
        b.City.SnapNodes = false;
        Pcg32 rngA = Pcg32.ForStream(a.Seed, RngStream.CityGen);
        Pcg32 rngB = Pcg32.ForStream(b.Seed, RngStream.CityGen);
        var nA = new NetworkStore(a.Caps);
        var nB = new NetworkStore(b.Caps);
        CityGenerator.Generate(a.City, a.Rules, a.Caps, new NetworkBuilder(nA, a.City.LaneWidth, a.City.ConnectorSamples, NetworkOptions.CornerRadiusFromDesc(a.City.CornerRadius)), ref rngA, out _);
        CityGenerator.Generate(b.City, b.Rules, b.Caps, new NetworkBuilder(nB, b.City.LaneWidth, b.City.ConnectorSamples, NetworkOptions.CornerRadiusFromDesc(b.City.CornerRadius)), ref rngB, out _);
        Assert.Equal(rngB.State, rngA.State);
        Assert.NotEqual(0, ra.NodeCount);
    }

    [Fact]
    public void Validator_BlockMustBeMultipleOfModule_OnlyWithSnap()
    {
        GenRun bad = GenFixture.Load("s1k");
        bad.City.BlockSize = 150f;
        bad.City.SizeX = bad.City.SizeZ = 3000f;
        bad.City.SnapNodes = true;
        Assert.Equal(CreateStatus.InvalidNetwork, Gen(bad).Status);

        bad.City.SnapNodes = false;   // прежнее поведение: любой block_m
        Assert.Equal(CreateStatus.Ok, Gen(bad).Status);
    }

    [Theory]
    [InlineData(8f, true)]
    [InlineData(152f, true)]
    [InlineData(150f, false)]
    [InlineData(100.5f, false)]
    public void IsMultipleOfModule(float block, bool expected) =>
        Assert.Equal(expected, CityGenerator.IsMultipleOfModule(block, Module));

    [Theory]
    [InlineData(3f, 0f, 0f)]
    [InlineData(4f, 8f, 0f)]    // .5 - от нуля
    [InlineData(-4f, -8f, 0f)]
    [InlineData(-3.9f, 0f, 0f)]
    [InlineData(75f, 72f, 0f)]
    [InlineData(77f, 80f, 0f)]
    [InlineData(37f, 37f, -1f)]   // module <= 0 - без снапа
    public void SnapToModule_RoundsToNearestMultiple(float v, float expected, float moduleSel) =>
        Assert.Equal(expected, CityGenerator.SnapToModule(v, moduleSel < 0f ? 0f : Module));
}
