using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Terrain;
using Sim.Core.Util;

namespace Sim.Core.Tools;

/// <summary>Проверки <c>BuildRoad</c> на рельефе (terrain.md §4.1, §4.3-§4.5, TR3): общие для превью и применения, без мутации, 0 Б.</summary>
internal static partial class ToolValidator
{
    /// <summary>
    /// Высоты концов (существующий узел, врезка - профиль разрезаемого сегмента, новый - земля) и подгонка под предел уклона (сначала B, затем A, в пределах
    /// <c>[ground − max_cut, ground + max_fill]</c>); проход по оси (TooDeep, мост); ограничения моста; объём земляных работ по предсказанной геометрии (отступы как в
    /// <c>CheckFootprint</c>). Обратная команда (undo) рельефом не отклоняется: сеть восстанавливается как была, мост ставится по необходимости.
    /// </summary>
    private static RejectReason CheckBuildTerrain(World w, in Command cmd, ReadOnlySpan<Vec2> curve, float len, ref ToolCheck c)
    {
        TerrainStore t = w.Terrain!;
        ref readonly TerrainDesc cfg = ref w.TerrainCfg;
        BuildRoadCmd b = cmd.BuildRoad;
        bool inverse = (cmd.Flags & CommandFlags.Inverse) != 0;
        NetworkStore n = w.Net;
        NetworkBuilder bld = w.Builder;
        NetworkOptions o = bld.Options;
        Vec2 pa = curve[0];
        Vec2 pb = curve[^1];

        int ya = EndCm(w, c.SnapKindA, c.NodeA, c.SplitSegA, c.SplitSA, pa, inverse, out bool wetA);
        int yb = EndCm(w, c.SnapKindB, c.NodeB, c.SplitSegB, c.SplitSB, pb, inverse, out bool wetB);
        if (!inverse && (wetA || wetB))
        {
            Mark(ref c, EntityKind.None, -1, 0, wetA ? pa : pb);
            return RejectReason.NeedsBridge;   // новый узел в воде: мост начинается и кончается на берегу
        }

        float sa = MathF.Min(c.NodeA >= 0 ? bld.IncrementalSetback(c.NodeA, curve[1], 0f) : o.NodeSetback, len * 0.5f);
        float sb = MathF.Min(c.NodeB >= 0 ? bld.IncrementalSetback(c.NodeB, curve[^2], 0f) : o.NodeSetback, len * 0.5f);
        float gl = MathF.Max(len - sa - sb, o.MinLaneLength);   // длина уклонной части (как SegmentData.Length после построения)
        RoadKind kind = c.Profile >= 0 ? c.Kind : b.Kind;
        int maxPm = RoadTerrain.MaxGradePm(w, c.Profile, kind);
        int allow = DetMath.ToIntSat(MathF.Floor(maxPm * gl / 10f));   // наибольший перепад, см
        if (Math.Abs(yb - ya) > allow && c.SnapKindB == SnapKind.NewNode)
        {
            yb = Fit(t, cfg, pb, yb, ya, allow);
        }

        if (Math.Abs(yb - ya) > allow && c.SnapKindA == SnapKind.NewNode)
        {
            ya = Fit(t, cfg, pa, ya, yb, allow);
        }

        if (Math.Abs(yb - ya) > allow && !inverse)
        {
            c.RejectValue = DetMath.ToIntSat(MathF.Round(Math.Abs(yb - ya) * 10f / gl));
            Mark(ref c, EntityKind.None, -1, 0, pa);
            return RejectReason.TooSteep;
        }

        c.ElevACm = ya;
        c.ElevBCm = yb;
        float hw = RoadTerrain.HalfWidth(n, in o, c.Profile, b.LanesAB + b.LanesBA);
        bool flag = (b.Flags & BuildRoadFlags.Bridge) != 0;
        var ax = new RoadAxis { YaCm = ya, YbCm = yb, SetA = sa, SetB = sb, Full = len, Hw = hw, Bridge = flag };
        RoadTerrain.ScanResult r = RoadTerrain.Scan(w, curve, in ax, out _);
        if (!inverse)
        {
            if (r.TooDeep)
            {
                Mark(ref c, EntityKind.None, -1, 0, r.DeepAt);
                return RejectReason.TooDeep;
            }

            if (r.NeedBridge && !flag && cfg.NoAutoBridge)
            {
                Mark(ref c, EntityKind.None, -1, 0, r.BridgeAt);
                return RejectReason.NeedsBridge;
            }
        }

        c.Bridge = flag || r.NeedBridge;
        c.BridgeAuto = c.Bridge && !flag;
        ax.Bridge = c.Bridge;
        if (c.Bridge && !inverse)
        {
            if (len > cfg.MaxBridgeM)
            {
                c.RejectValue = -2;
                Mark(ref c, EntityKind.None, -1, 0, r.NeedBridge ? r.BridgeAt : pa);
                return RejectReason.NeedsBridge;
            }

            if (r.LowDeck)
            {
                c.RejectValue = -1;
                Mark(ref c, EntityKind.None, -1, 0, r.LowAt);
                return RejectReason.NeedsBridge;
            }
        }

        // Земляные работы: тот же штамп, что применение (сегмент, затем площадки концов), без записи.
        Span<RoadPad> pads = stackalloc RoadPad[2];
        pads[0] = PadPredicted(w, c.NodeA, c.SplitSegA, pa, sa, hw, ya);
        pads[1] = PadPredicted(w, c.NodeB, c.SplitSegB, pb, sb, hw, yb);
        var rect = default(SampleRect);
        long sum = RoadTerrain.Stamp(t, curve, in ax, pads, cfg.BankSlopePm, apply: false, respectLock: true, ref rect);
        c.EarthworkM3 = RoadTerrain.VolumeM3(t, sum);
        c.EarthworkCost = c.EarthworkM3 * w.ToolsCfg.Costs.EarthworkPerM3;
        return RejectReason.None;
    }

    // Высота конца, абсолютные см. Новый узел в воде: wet = true (не undo) или над водой на просвет моста (undo, как в Create).
    private static int EndCm(World w, byte snap, int node, int splitSeg, float splitS, Vec2 p, bool inverse, out bool wet)
    {
        wet = false;
        if (snap == SnapKind.ExistingNode)
        {
            return RoadTerrain.Cm(w.Net.Nodes[node].Elevation);
        }

        if (snap == SnapKind.SplitSegment)
        {
            return RoadTerrain.SegmentYCm(w, splitSeg, splitS);
        }

        TerrainStore t = w.Terrain!;
        int y = t.GroundCmAt(p.X, p.Z);
        int i = t.NearestSample(p.X, p.Z);
        if (t.Water[i] > t.Ground[i])
        {
            if (!inverse)
            {
                wet = true;
            }

            y = Math.Max(y, t.BaseCm + t.Water[i] + w.TerrainCfg.BridgeClearanceCm);
        }

        return y;
    }

    // Сдвиг нового конца к высоте другого до ровно предела, в пределах выемки/насыпи своей точки.
    private static int Fit(TerrainStore t, in TerrainDesc cfg, Vec2 p, int y, int other, int allow)
    {
        int target = y > other ? other + allow : other - allow;
        int g = t.GroundCmAt(p.X, p.Z);
        return Math.Clamp(target, g - cfg.MaxCutCm, g + cfg.MaxFillCm);
    }

    // Площадка конца до построения: существующий узел - его площадка с полушириной новой дороги; новый (и врезка) - диск отступа + полуширина.
    private static RoadPad PadPredicted(World w, int node, int splitSeg, Vec2 p, float setback, float hw, int yCm)
    {
        NetworkStore n = w.Net;
        NetworkOptions o = w.Builder.Options;
        if (node >= 0)
        {
            RoadPad pad = RoadTerrain.PadOf(n, in o, node, hw);
            pad.R = MathF.Max(pad.R, setback + hw);
            return pad;
        }

        float h = splitSeg >= 0 ? MathF.Max(hw, RoadTerrain.HalfWidth(n, in o, in n.Segments[splitSeg])) : hw;
        return new RoadPad { C = p, R = setback + h, YCm = yCm };
    }
}
