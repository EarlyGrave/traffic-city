using System;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Tests.Gen;
using Sim.Core.Util;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Network;

/// <summary>ADR-0028: отступ рукава по ширине поперечной дороги + <c>corner_radius</c>; радиус правого поворота из крайней полосы артерии.</summary>
public class CornerRadiusTests
{
    // Голден-хэш NetworkStore.Hash() s1k до ADR-0028 (CityGeneratorTests.GoldenS1kNetworkHash до смены): выключенное скругление обязано его воспроизводить.
    private const ulong LegacyS1kNetworkHash = 0x579C25BA8FEB8BC8UL;

    private const float MinRightTurnRadius = 6f;

    private readonly ITestOutputHelper _out;

    public CornerRadiusTests(ITestOutputHelper output) => _out = output;


    [Fact]
    public void Disabled_ReproducesLegacyNetwork()
    {
        GenRun r = GenFixture.Run("s1k", run => run.City.CornerRadius = -1f);
        Assert.Equal(CreateStatus.Ok, r.CityStatus);
        Assert.Equal(LegacyS1kNetworkHash, r.Net.Hash());
    }

    [Fact]
    public void ArterialCurbRightTurn_RadiusAtLeastThreshold()
    {
        GenRun legacy = GenFixture.Run("s1k", run => run.City.CornerRadius = -1f);
        GenRun now = GenFixture.Run("s1k");
        TurnStats aaBefore = RightTurns(legacy, intoArterial: true, perpendicular: true);
        TurnStats aaAfter = RightTurns(now, intoArterial: true, perpendicular: true);
        TurnStats asBefore = RightTurns(legacy, intoArterial: false, perpendicular: true);
        TurnStats asAfter = RightTurns(now, intoArterial: false, perpendicular: true);
        TurnStats allBefore = RightTurns(legacy, intoArterial: false, perpendicular: false);
        TurnStats allAfter = RightTurns(now, intoArterial: false, perpendicular: false);
        _out.WriteLine(Line("артерия -> артерия (90°)", aaBefore, aaAfter));
        _out.WriteLine(Line("артерия -> улица/артерия (90°)", asBefore, asAfter));
        _out.WriteLine(Line("все правые из крайней полосы артерии, вкл. косые узлы", allBefore, allAfter));

        Assert.True(aaBefore.Count > 0 && aaAfter.Count == aaBefore.Count && asAfter.Count == asBefore.Count);
        Assert.True(aaBefore.MinChordRadius < MinRightTurnRadius, "до ADR-0028 поворот был резким - проверка что-то меряет");

        // Радиус, из которого считается лимит скорости connector'а (EmitBezier: хорда / (2 sin(поворот/2))), - для прямоугольных узлов.
        Assert.True(aaAfter.MinChordRadius >= MinRightTurnRadius, $"радиус {aaAfter.MinChordRadius}");
        Assert.True(asAfter.MinChordRadius >= MinRightTurnRadius, $"радиус (артерия -> улица) {asAfter.MinChordRadius}");
        Assert.True(aaAfter.MinSpeed > aaBefore.MinSpeed);
        Assert.True(allAfter.MinChordRadius > allBefore.MinChordRadius && allAfter.MinCurvature > allBefore.MinCurvature);
    }

    [Theory]
    [InlineData("s1k")]
    [InlineData("s10k")]
    [InlineData("s100k")]
    [InlineData("s1k-res")]
    [InlineData("s10k-res")]
    [InlineData("s100k-res")]
    public void Generation_Ok_SetbacksByArms(string scenario)
    {
        GenRun r = GenFixture.Run(scenario);
        Assert.Equal(CreateStatus.Ok, r.CityStatus);
        NetworkStore n = r.Net;
        NetworkOptions o = r.Builder.Options;
        int inflated = 0;
        for (int seg = 0; seg < n.SegmentCount; seg++)
        {
            ref readonly SegmentData sd = ref n.Segments[seg];
            for (int end = 0; end < 2; end++)
            {
                int node = end == 0 ? sd.NodeA : sd.NodeB;
                Vec2 p = n.SegmentPoints[end == 0 ? sd.FirstPoint : sd.FirstPoint + sd.PointCount - 1];
                float cut = Dist(n.Nodes[node].Position, p);
                int degree = Degree(n, node);
                if (degree <= 2)
                {
                    Assert.Equal(o.NodeSetback, cut, 2);   // тупики и изгибы - без раздувания
                }
                else
                {
                    Assert.True(cut >= o.NodeSetback - 0.01f && cut <= n.Nodes[node].Radius + 0.01f);
                    inflated += cut > o.NodeSetback + 0.01f ? 1 : 0;
                }
            }

            Assert.True(sd.Length >= o.MinLaneLength - 0.001f);
        }

        _out.WriteLine($"{scenario}: сегментов {n.SegmentCount}, концов с увеличенным отступом {inflated}, сжатых надбавок {r.Builder.SetbackClampCount}");
        Assert.True(inflated > 0);
    }

    [Fact]
    public void Clearance_AllRedGrowsWithBox_ZeroWithoutCornerRadius()
    {
        int prev = -1;
        float[] radii = { -1f, 2f, 4f, 6f, 8f, 10f };
        foreach (float r in radii)
        {
            GenRun g = GenFixture.Run("s1k", run => run.City.CornerRadius = r);
            Assert.Equal(CreateStatus.Ok, g.CityStatus);
            SignalPlanSeed s = g.City0.SignalPlans[0];
            int extra = Sim.Core.Rules.Intersections.SignalStore.ClearanceExtraTicks(g.Net, s.Node, g.Builder.Options);
            float box = 2f * g.Net.Nodes[s.Node].Radius;
            _out.WriteLine($"corner {r}: коробка {box:0.0} м, all-red {s.AllRedTicks} тиков (+{extra})");
            Assert.Equal(g.Rules.DefaultAllRedTicks + extra, s.AllRedTicks);
            if (r < 0)
            {
                Assert.Equal(0, extra);   // коробка 14 м - интергрин прежний
            }
            else
            {
                Assert.True(extra > prev, $"all-red не растёт с коробкой: {extra} <= {prev}");
            }

            prev = extra;
        }
    }

    [Fact]
    public void ShortSegment_InflatedSetbacksShrink_NotRejected()
    {
        var store = new NetworkStore(NetworkTests.Caps());
        var b = new NetworkBuilder(store);
        // Две артерии крест-накрест на расстоянии 20 м: отступ рукавов 7 + 6 = 13, сумма 26 > 20 - 1.
        int a0 = b.AddNode(new Vec2(0, 0), NodeControl.None);
        int a1 = b.AddNode(new Vec2(20, 0), NodeControl.None);
        int[] ends = { b.AddNode(new Vec2(0, 100), NodeControl.None), b.AddNode(new Vec2(0, -100), NodeControl.None), b.AddNode(new Vec2(20, 100), NodeControl.None), b.AddNode(new Vec2(20, -100), NodeControl.None), b.AddNode(new Vec2(-100, 0), NodeControl.None), b.AddNode(new Vec2(120, 0), NodeControl.None) };
        Assert.True(b.AddSegment(ends[0], a0, 2, 2, 15f, RoadKind.Arterial) >= 0);
        Assert.True(b.AddSegment(ends[1], a0, 2, 2, 15f, RoadKind.Arterial) >= 0);
        Assert.True(b.AddSegment(ends[2], a1, 2, 2, 15f, RoadKind.Arterial) >= 0);
        Assert.True(b.AddSegment(ends[3], a1, 2, 2, 15f, RoadKind.Arterial) >= 0);
        Assert.True(b.AddSegment(ends[4], a0, 2, 2, 15f, RoadKind.Arterial) >= 0);
        Assert.True(b.AddSegment(ends[5], a1, 2, 2, 15f, RoadKind.Arterial) >= 0);
        int clampsBefore = b.SetbackClampCount;
        int mid = b.AddSegment(a0, a1, 2, 2, 15f, RoadKind.Arterial);
        Assert.True(mid >= 0);
        Assert.Equal(clampsBefore + 1, b.SetbackClampCount);
        Assert.Equal(b.Options.MinLaneLength, store.Segments[mid].Length, 2);
        Assert.True(b.BuildAllConnectors());
    }

    [Fact]
    public void Tools_NewArmAtExistingCross_GetsInflatedSetback_OldArmsKeepTheirs()
    {
        var store = new NetworkStore(NetworkTests.Caps());
        var b = new NetworkBuilder(store);
        int c = b.AddNode(new Vec2(0, 0), NodeControl.None);
        int w = b.AddNode(new Vec2(-100, 0), NodeControl.None);
        int e = b.AddNode(new Vec2(100, 0), NodeControl.None);
        int s = b.AddNode(new Vec2(0, -100), NodeControl.None);
        int sw = b.AddSegment(w, c, 2, 2, 15f, RoadKind.Arterial);
        int se = b.AddSegment(c, e, 2, 2, 15f, RoadKind.Arterial);
        Assert.Equal(b.Options.NodeSetback, Dist(store.Nodes[c].Position, store.SegmentPoints[store.Segments[sw].FirstPoint + 1]), 3);   // изгиб/прямая: без раздувания
        int ss = b.AddSegment(s, c, 1, 1, 10f, RoadKind.Street);   // третий рукав: поперечная - артерия (полуширина 7 м)
        float cut = Dist(store.Nodes[c].Position, store.SegmentPoints[store.Segments[ss].FirstPoint + 1]);
        Assert.Equal(7f + NetworkOptions.DefaultCornerRadius, cut, 3);
        Assert.Equal(cut, store.Nodes[c].Radius, 3);
        Assert.Equal(b.Options.NodeSetback, Dist(store.Nodes[c].Position, store.SegmentPoints[store.Segments[se].FirstPoint]), 3);
    }

    private struct TurnStats
    {
        public int Count;
        public double MinCurvature, MinChordRadius, MinSpeed;
    }

    private static string Line(string what, TurnStats a, TurnStats b) =>
        $"{what}: connector'ов {b.Count}; радиус лимита (хорда) {a.MinChordRadius:0.00} -> {b.MinChordRadius:0.00} м; мин. радиус кривизны Безье {a.MinCurvature:0.00} -> {b.MinCurvature:0.00} м; " +
        $"лимит скорости {a.MinSpeed:0.00} -> {b.MinSpeed:0.00} м/с";

    // Правые повороты connector'ами из крайней полосы (LaneNo 0) артерии; intoArterial - только в артерию; perpendicular - только поворот 90° (|cos| < 0.05).
    private static TurnStats RightTurns(GenRun r, bool intoArterial, bool perpendicular)
    {
        Assert.Equal(CreateStatus.Ok, r.CityStatus);
        NetworkStore n = r.Net;
        var st = new TurnStats { MinCurvature = double.MaxValue, MinChordRadius = double.MaxValue, MinSpeed = double.MaxValue };
        for (int i = 0; i < n.LaneCount; i++)
        {
            ref readonly LaneData c = ref n.Lanes[i];
            if (c.Kind != LaneKind.Connector || c.FromLane < 0 || c.ToLane < 0)
            {
                continue;
            }

            ref readonly LaneData li = ref n.Lanes[c.FromLane];
            ref readonly LaneData lo = ref n.Lanes[c.ToLane];
            if (li.LaneNo != 0 || n.Segments[li.Segment].Kind != RoadKind.Arterial || (intoArterial && n.Segments[lo.Segment].Kind != RoadKind.Arterial))
            {
                continue;
            }

            Vec2 p0 = n.LanePoints[li.FirstPoint + li.PointCount - 1];
            Vec2 t0 = Norm(p0 - n.LanePoints[li.FirstPoint + li.PointCount - 2]);
            Vec2 p3 = n.LanePoints[lo.FirstPoint];
            Vec2 t3 = Norm(n.LanePoints[lo.FirstPoint + 1] - p3);
            double cos = (t0.X * t3.X) + (t0.Z * t3.Z);
            double cross = (t0.X * t3.Z) - (t0.Z * t3.X);
            if (cos > 0.5 || cross <= 0 || (perpendicular && Math.Abs(cos) >= 0.05))
            {
                continue;   // не правый поворот (прямо или налево) или не 90°
            }

            double chord = Dist(p0, p3);
            double d = chord / 3.0;
            st.Count++;
            st.MinCurvature = Math.Min(st.MinCurvature, MinBezierRadius(p0, p0 + (t0 * (float)d), p3 - (t3 * (float)d), p3));
            st.MinChordRadius = Math.Min(st.MinChordRadius, chord / (2.0 * Math.Sqrt(Math.Max(0, (1 - cos) * 0.5))));
            st.MinSpeed = Math.Min(st.MinSpeed, c.SpeedLimit);
        }

        return st;
    }

    // Минимальный радиус кривизны кубической Безье (аналитически, 400 шагов по t).
    private static double MinBezierRadius(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3)
    {
        double best = double.MaxValue;
        for (int k = 0; k <= 400; k++)
        {
            double t = k / 400.0;
            double u = 1 - t;
            double dx = 3 * ((u * u * (p1.X - p0.X)) + (2 * u * t * (p2.X - p1.X)) + (t * t * (p3.X - p2.X)));
            double dz = 3 * ((u * u * (p1.Z - p0.Z)) + (2 * u * t * (p2.Z - p1.Z)) + (t * t * (p3.Z - p2.Z)));
            double ex = 6 * ((u * (p2.X - (2 * p1.X) + p0.X)) + (t * (p3.X - (2 * p2.X) + p1.X)));
            double ez = 6 * ((u * (p2.Z - (2 * p1.Z) + p0.Z)) + (t * (p3.Z - (2 * p2.Z) + p1.Z)));
            double sp = Math.Sqrt((dx * dx) + (dz * dz));
            double k2 = Math.Abs((dx * ez) - (dz * ex));
            if (k2 > 1e-12)
            {
                best = Math.Min(best, sp * sp * sp / k2);
            }
        }

        return best;
    }

    private static int Degree(NetworkStore n, int node)
    {
        int d = 0;
        for (int code = n.IncHead[node]; code >= 0; code = n.IncNext[code])
        {
            d++;
        }

        return d;
    }

    private static Vec2 Norm(Vec2 v)
    {
        float l = MathF.Sqrt((v.X * v.X) + (v.Z * v.Z));
        return new Vec2(v.X / l, v.Z / l);
    }

    private static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));
}
