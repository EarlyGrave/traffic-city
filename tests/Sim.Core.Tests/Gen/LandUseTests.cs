using System;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Tests.City;
using Sim.Core.Util;

namespace Sim.Core.Tests.Gen;

/// <summary>VS8 (visual-style.md §8, ADR-0027): землепользование генератора - парки, пруды, береговая полоса; отказ участкам в парке и воде.</summary>
public sealed class LandUseTests
{
    private static readonly LandUseDesc _on = new()
    {
        Enabled = true,
        ParkShare = 0.12f,
        PondShare = 0.25f,
        Coast = LandUseCoast.South,
        CoastWidthM = 400f,
    };

    private static GenRun Run(ulong seed, bool landUse)
    {
        return GenFixture.Run("s1k-res", r =>
        {
            r.Seed = seed;
            r.City.SnapNodes = true;
            if (landUse)
            {
                r.City.LandUse = _on;
            }
        });
    }

    private static ulong Fnv(LandUsePoly[] polys)
    {
        var h = Fnv1a64.Create();
        h.Add(polys.Length);
        foreach (LandUsePoly p in polys)
        {
            h.Add(p.P0.X);
            h.Add(p.P0.Z);
            h.Add(p.P1.X);
            h.Add(p.P1.Z);
            h.Add(p.P2.X);
            h.Add(p.P2.Z);
            h.Add(p.P3.X);
            h.Add(p.P3.Z);
            h.Add(p.Kind);
            h.Add(p.Flags);
        }

        return h.Value;
    }


    private static LandUsePoly[] Generate(ulong seed)
    {
        GenRun run = Run(seed, landUse: false);
        run.City.LandUse = _on;
        run.Net = new NetworkStore(run.Caps);
        run.Builder = new NetworkBuilder(run.Net, run.City.LaneWidth, run.City.ConnectorSamples, NetworkOptions.CornerRadiusFromDesc(run.City.CornerRadius));
        Pcg32 rng = Pcg32.ForStream(seed, RngStream.CityGen);
        CreateStatus st = CityGenerator.Generate(run.City, run.Rules, run.Caps, run.Builder, ref rng, out CityGenResult res, seed);
        Assert.Equal(CreateStatus.Ok, st);
        Assert.NotNull(res.LandUse);
        return res.LandUse!;
    }

    [Fact]
    public void Layout_Is36Bytes()
    {
        Assert.Equal(36, System.Runtime.InteropServices.Marshal.SizeOf<LandUsePoly>());
    }

    [Fact]
    public void Disabled_ProducesNothing_AndDoesNotChangeNetwork()
    {
        GenRun off = Run(1, landUse: false);
        Assert.Null(off.City0.LandUse);
        GenRun on = Run(1, landUse: true);
        Assert.Equal(off.Net.Hash(), on.Net.Hash());   // землепользование не трогает ни сеть, ни общий PRNG генератора
        Assert.Equal(GenFixture.ResultHash(off), GenFixture.ResultHash(on));
    }

    [Theory]
    [InlineData(1UL, 0xBD49B4FF4D0C7495UL)]
    [InlineData(2UL, 0xEBBFB346E4F4CBF8UL)]
    [InlineData(3UL, 0x1861AB3D8A155FF5UL)]
    public void Golden_Fnv_PerSeed(ulong seed, ulong expected)
    {
        LandUsePoly[] polys = Generate(seed);
        ulong h = Fnv(polys);
        Assert.Equal(Fnv(Generate(seed)), h);   // детерминизм
        Assert.True(h == expected, $"seed {seed}: fnv 0x{h:X16}UL, polys {polys.Length}");
    }

    [Fact]
    public void Polys_ParksInteriorOnly_PondsOnParks_CoastIsWaterOutsideMap()
    {
        GenRun run = Run(2, landUse: false);
        LandUsePoly[] polys = Generate(2);
        float halfW = run.City.SizeX * 0.5f;
        float halfH = run.City.SizeZ * 0.5f;
        int parks = 0;
        int coasts = 0;
        foreach (LandUsePoly p in polys)
        {
            if (p.Kind == (byte)LandUseKind.Park)
            {
                parks++;
                Assert.Equal(0, p.Flags & (byte)LandUseMarks.Coast);
                Assert.True(MathF.Abs(p.P0.X) < halfW && MathF.Abs(p.P2.X) < halfW && MathF.Abs(p.P0.Z) < halfH && MathF.Abs(p.P2.Z) < halfH, "park touches the map perimeter");
                foreach (Vec2 v in new[] { p.P0, p.P1, p.P2, p.P3 })
                {
                    Assert.Equal(0f, v.X % 8f);   // snap_nodes: углы на сетке 8 м (road-profiles.md §5)
                    Assert.Equal(0f, v.Z % 8f);
                }
            }
            else
            {
                coasts++;
                Assert.Equal((byte)LandUseKind.Water, p.Kind);
                Assert.Equal((byte)LandUseMarks.Coast, p.Flags);
                Assert.True(p.P0.Z >= halfH && p.P3.Z > halfH, "south coast lies beyond +Z edge");
                Assert.Equal(0f, (p.P3.Z - p.P0.Z) % 8f);
            }
        }

        Assert.Equal(1, coasts);
        Assert.True(parks > 0);
    }

    [Fact]
    public void Contains_QuadAndTriangle()
    {
        var q = new LandUsePoly { P0 = new Vec2(0, 0), P1 = new Vec2(10, 0), P2 = new Vec2(10, 10), P3 = new Vec2(0, 10) };
        Assert.True(q.Contains(5, 5));
        Assert.True(q.Contains(0, 0));
        Assert.False(q.Contains(11, 5));
        Assert.False(q.Contains(5, -0.1f));
        var t = new LandUsePoly { P0 = new Vec2(0, 0), P1 = new Vec2(10, 0), P2 = new Vec2(10, 10), P3 = new Vec2(10, 10) };
        Assert.True(t.Contains(8, 2));
        Assert.False(t.Contains(2, 8));
    }

    [Fact]
    public void Lots_NeverInParkOrWater_AndParksDropLots()
    {
        ScenarioDesc plain = CityFixtures.S1kRes();
        ScenarioDesc withLu = CityFixtures.S1kRes();
        withLu.City.LandUse = _on;
        using Simulation a = Simulation.Create(in plain);
        using Simulation b = Simulation.Create(in withLu);

        Assert.Equal(0, a.LandUse.Length);
        Assert.Equal(0, a.LandUseVersion);
        Assert.True(b.LandUse.Length > 1);
        Assert.Equal(1, b.LandUseVersion);

        var lotsA = new LotRenderData[a.LotCount];
        var lotsB = new LotRenderData[b.LotCount];
        a.GetLots(lotsA);
        b.GetLots(lotsB);
        Assert.True(lotsB.Length < lotsA.Length, $"lots with landuse {lotsB.Length} vs without {lotsA.Length}");
        Assert.True(lotsB.Length > 0);

        LandUsePoly[] polys = b.LandUse.ToArray();
        foreach (LotRenderData lot in lotsB)
        {
            foreach (LandUsePoly p in polys)
            {
                Assert.False(p.Contains(lot.X, lot.Z), $"lot ({lot.X}, {lot.Z}) inside landuse kind {p.Kind}");
            }
        }
    }

    [Fact]
    public void Create_IsDeterministic_SameStateHash()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        d.City.LandUse = _on;
        using Simulation a = Simulation.Create(in d);
        using Simulation b = Simulation.Create(in d);
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.True(a.LandUse.SequenceEqual(b.LandUse));
    }
}
