using System;
using System.Diagnostics;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Gen;

/// <summary>Стартовые параметры цикла светофора (city-generator §3.8). Состав фаз — спека перекрёстков.</summary>
internal struct SignalPlanSeed
{
    public int Node;
    public byte PhaseCount;       // всегда 2: фаза 0 — рукава оси X, фаза 1 — рукава оси Z
    public ushort Green0, Green1; // тики
    public ushort YellowTicks, AllRedTicks;
    public ushort OffsetTicks;    // сдвиг цикла (зелёная волна)
    public bool Adaptive;         // 2.5 (ADR-0018): city.signal_mode = adaptive; Green0/Green1 тогда - MaxGreen осей
}

internal struct CityGenResult
{
    public float Width, Height, BlockX, BlockZ;
    public int Nx, Nz, ArterialLinesX, ArterialLinesZ;
    public int NodeCount, SegmentCount, LaneCount, ConnectorCount, LanePointCount;
    public int RemovedStreets, RepairedEdges, ShiftedNodes, MergedNodes, IrregularSkipped;
    public int SignalizedNodes;
    public SignalPlanSeed[] SignalPlans;      // длина SignalizedNodes; выделяется в Generate
}

/// <summary>
/// Процедурный генератор города (docs/design/city-generator.md, ADR-0003): решётка с джиттером, удаление улиц,
/// починка связности, нерегулярные узлы, эмиссия в <see cref="NetworkBuilder"/>, назначение управления узлами.
/// Порядок обращений к PRNG нормативен (§3.9).
/// </summary>
internal static class CityGenerator
{
    internal const float MinSegmentLength = 30f;
    internal const float MinArmAngle = 0.43633232f; // 25°
    internal const int MaxArms = 6;
    internal const float StraightProb = 0.7f;
    internal const float TerminalStopS = 40f;
    internal const float StopSpacingTolerance = 0.75f;
    internal const float MinRouteLenShare = 0.6f;
    internal const int RouteAttempts = 8;
    internal const int Tail1MaxEdges = 3;
    internal const int Tail2Edges = 2;
    internal const ushort DefaultGreenTicks = 480;
    internal const ushort MajorGreenTicks = 720;
    internal const ushort MinorGreenTicks = 280;

    public static CreateStatus Generate(
        in CityGenDesc city,
        in RulesDesc rules,
        in SimCapacities caps,
        NetworkBuilder b,
        ref Pcg32 rng,
        out CityGenResult result)
    {
        result = default;
        if (!ValidateDesc(in city))
        {
            return CreateStatus.InvalidNetwork;
        }

        // ---- производные величины (§1) ----
        float w = city.SizeX * city.Scale;
        float h = city.SizeZ * city.Scale;
        int nx = Math.Max(2, RoundI(w / city.BlockSize));
        int nz = Math.Max(2, RoundI(h / city.BlockSize));
        float bx = w / nx;
        float bz = h / nz;
        float g = city.NodeJitter;
        float halfW = w * 0.5f;
        float halfH = h * 0.5f;

        var pl = new GridPlan(nx, nz);
        int artLinesX = MarkArterialLines(pl.ArtX, nx, city.ArterialEvery);
        int artLinesZ = MarkArterialLines(pl.ArtZ, nz, city.ArterialEvery);

        result.Width = w;
        result.Height = h;
        result.BlockX = bx;
        result.BlockZ = bz;
        result.Nx = nx;
        result.Nz = nz;
        result.ArterialLinesX = artLinesX;
        result.ArterialLinesZ = artLinesZ;

        // ---- шаг 1: решётка с джиттером (§3.2) ----
        int stride = nx + 1;
        for (int j = 0; j <= nz; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                int p = (j * stride) + i;
                float x = i == 0 ? -halfW : (i == nx ? halfW : (-halfW) + (i * bx));
                float z = j == 0 ? -halfH : (j == nz ? halfH : (-halfH) + (j * bz));
                float dx = rng.NextRange(-g, g);
                float dz = rng.NextRange(-g, g);
                bool bi = i == 0 || i == nx;
                bool bj = j == 0 || j == nz;
                // Внутренний: оба. Левый/правый край (bi, !bj): только dz. Нижний/верхний (bj, !bi): только dx. Угол: ничего.
                if (!bi)
                {
                    x += dx;
                }

                if (!bj)
                {
                    z += dz;
                }

                pl.X[p] = x;
                pl.Z[p] = z;
                pl.NodeAlive[p] = 1;
                byte flags = 0;
                if (bi || bj)
                {
                    flags |= GridPlan.FlagBorder;
                }

                if (pl.ArtX[i] != 0 || pl.ArtZ[j] != 0)
                {
                    flags |= GridPlan.FlagOnArterialLine;
                }

                pl.NodeFlags[p] = flags;
            }
        }

        for (int j = 0; j <= nz; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int e = pl.HorizontalEdge(i, j);
                InitEdge(pl, e, (j * stride) + i, (j * stride) + i + 1, pl.ArtZ[j] != 0);
            }
        }

        for (int j = 0; j < nz; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                int e = pl.VerticalEdge(i, j);
                InitEdge(pl, e, (j * stride) + i, ((j + 1) * stride) + i, pl.ArtX[i] != 0);
            }
        }

        // ---- шаг 2: удаление внутренних улиц (§3.3) ----
        int[] cand = pl.Candidates;
        int cc = 0;
        for (int e = 0; e < pl.EdgeCap; e++)
        {
            if (pl.EdgeAlive[e] != 0 && pl.EdgeKind[e] == (byte)RoadKind.Street &&
                (pl.NodeFlags[pl.EdgeA[e]] & GridPlan.FlagBorder) == 0 &&
                (pl.NodeFlags[pl.EdgeB[e]] & GridPlan.FlagBorder) == 0)
            {
                cand[cc++] = e;
            }
        }

        int removeCount = RoundI(city.RemovedStreetShare * cc);
        if (removeCount > cc)
        {
            removeCount = cc;
        }

        for (int t = 0; t < removeCount; t++)
        {
            int r = rng.NextInt(t, cc);
            (cand[t], cand[r]) = (cand[r], cand[t]);
        }

        int removedN = 0;
        for (int t = 0; t < removeCount; t++)
        {
            int e = cand[t];
            int a = pl.EdgeA[e];
            int bb = pl.EdgeB[e];
            if (pl.Deg[a] > 2 && pl.Deg[bb] > 2)
            {
                pl.EdgeAlive[e] = 0;
                pl.RemoveAdj(a, e);
                pl.RemoveAdj(bb, e);
                pl.Removed[removedN++] = e;
            }
        }

        // ---- шаг 3: связность (§3.4) ----
        for (int p = 0; p < pl.NodeCap; p++)
        {
            pl.UnionParent[p] = p;
        }

        for (int e = 0; e < pl.EdgeCap; e++)
        {
            if (pl.EdgeAlive[e] != 0)
            {
                pl.Union(pl.EdgeA[e], pl.EdgeB[e]);
            }
        }

        int repaired = 0;
        for (int t = 0; t < removedN; t++)
        {
            int e = pl.Removed[t];
            if (pl.Find(pl.EdgeA[e]) != pl.Find(pl.EdgeB[e]))
            {
                pl.EdgeAlive[e] = 1;
                pl.AddAdj(pl.EdgeA[e], e);
                pl.AddAdj(pl.EdgeB[e], e);
                pl.Union(pl.EdgeA[e], pl.EdgeB[e]);
                repaired++;
            }
        }

        Debug.Assert(Reachable(pl) == pl.NodeCap, "grid must be connected after repair");
        result.RepairedEdges = repaired;
        result.RemovedStreets = removedN - repaired;

        // ---- шаг 4: нерегулярные узлы (§3.5) ----
        int m = 0;
        for (int p = 0; p < pl.NodeCap; p++)
        {
            if (pl.NodeAlive[p] != 0 && (pl.NodeFlags[p] & (GridPlan.FlagBorder | GridPlan.FlagOnArterialLine)) == 0 && pl.Deg[p] == 4)
            {
                cand[m++] = p;
            }
        }

        int kCount = Math.Min(city.IrregularNodes, m);
        for (int t = 0; t < kCount; t++)
        {
            int r = rng.NextInt(t, m);
            (cand[t], cand[r]) = (cand[r], cand[t]);
        }

        int shifted = 0;
        int merged = 0;
        int skipped = 0;
        for (int k = 0; k < kCount; k++)
        {
            int p = cand[k];
            float rr = rng.NextRange(0.5f, 1f) * city.IrregularJitter;
            float theta = rng.NextRange(0f, 2f * MathF.PI);
            if (pl.NodeAlive[p] == 0 || (pl.NodeFlags[p] & (GridPlan.FlagShifted | GridPlan.FlagMerged)) != 0)
            {
                skipped++;
                continue;
            }

            if ((k & 1) == 1)
            {
                int mr = TryMerge(pl, p);
                if (mr == 1)
                {
                    merged++;
                    continue;
                }

                if (mr == 2)
                {
                    skipped++;
                    continue;
                }
            }

            if (TryShift(pl, p, rr * MathF.Cos(theta), rr * MathF.Sin(theta)))
            {
                shifted++;
            }
            else
            {
                skipped++;
            }
        }

        result.ShiftedNodes = shifted;
        result.MergedNodes = merged;
        result.IrregularSkipped = skipped;

        // ---- шаг 5: эмиссия (§3.7) ----
        int aliveNodes = 0;
        for (int p = 0; p < pl.NodeCap; p++)
        {
            if (pl.NodeAlive[p] != 0)
            {
                aliveNodes++;
#if DEBUG
                Debug.Assert(pl.Validate(p), "node geometry invalid before emission");
#endif
            }
        }

        int aliveEdges = 0;
        for (int e = 0; e < pl.EdgeCap; e++)
        {
            if (pl.EdgeAlive[e] != 0)
            {
                aliveEdges++;
            }
        }

        if (aliveNodes > caps.MaxNodes || aliveEdges > caps.MaxSegments)
        {
            return CreateStatus.NetworkTooLarge;
        }

        int node0 = b.NodeCount;
        int seg0 = b.SegmentCount;
        int lane0 = b.LaneCount;
        int pts0 = b.LanePointCount;
        for (int p = 0; p < pl.NodeCap; p++)
        {
            if (pl.NodeAlive[p] == 0)
            {
                continue;
            }

            NetworkStatus st = b.AddNode(new Vec2(pl.X[p], pl.Z[p]), NodeControl.None, out int ni);
            if (st != NetworkStatus.Ok)
            {
                return st.ToCreateStatus();
            }

            pl.NodeOut[p] = ni;
        }

        int normalLanes = 0;
        for (int e = 0; e < pl.EdgeCap; e++)
        {
            if (pl.EdgeAlive[e] == 0)
            {
                continue;
            }

            bool art = pl.EdgeKind[e] == (byte)RoadKind.Arterial;
            int lanes = art ? 2 : 1;
            float speed = art ? city.ArterialSpeed : city.StreetSpeed;
            NetworkStatus st = b.AddSegment(pl.NodeOut[pl.EdgeA[e]], pl.NodeOut[pl.EdgeB[e]], lanes, lanes, speed, (RoadKind)pl.EdgeKind[e], out int si);
            if (st != NetworkStatus.Ok)
            {
                return st.ToCreateStatus();
            }

            pl.EdgeOut[e] = si;
            normalLanes += 2 * lanes;
        }

        NetworkStatus cs = b.BuildAllConnectorsEx();
        if (cs != NetworkStatus.Ok)
        {
            return cs.ToCreateStatus();
        }

        result.NodeCount = b.NodeCount - node0;
        result.SegmentCount = b.SegmentCount - seg0;
        result.LaneCount = b.LaneCount - lane0;
        result.ConnectorCount = result.LaneCount - normalLanes;
        result.LanePointCount = b.LanePointCount - pts0;

        // ---- шаг 6: управление узлами (§3.8) ----
        return AssignControl(in city, in rules, in caps, b, pl, bx, ref result);
    }

    private static CreateStatus AssignControl(
        in CityGenDesc city,
        in RulesDesc rules,
        in SimCapacities caps,
        NetworkBuilder b,
        GridPlan pl,
        float bx,
        ref CityGenResult result)
    {
        NetworkStore store = b.Store;
        int nx = pl.Nx;
        int stride = nx + 1;
        NodeControl minor = city.MinorControl switch
        {
            MinorControl.Stop => NodeControl.Stop,
            MinorControl.RightHand => NodeControl.RightHandRule,
            _ => NodeControl.Yield,
        };

        // порядковый номер артериальной колонки (число артериальных колонок строго левее i)
        int[] colOrd = new int[stride];
        int acc = 0;
        for (int i = 0; i < stride; i++)
        {
            colOrd[i] = acc;
            if (pl.ArtX[i] != 0)
            {
                acc++;
            }
        }

        // проход 1: класс и счёт светофоров
        int signalCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int written = 0;
            for (int p = 0; p < pl.NodeCap; p++)
            {
                if (pl.NodeAlive[p] == 0)
                {
                    continue;
                }

                int n = pl.NodeOut[p];
                int arms = b.ArmCount(n);
                int ac = 0;
                int sc = 0;
                for (int k = 0; k < arms; k++)
                {
                    if (store.Segments[b.ArmSegment(n, k)].Kind == RoadKind.Arterial)
                    {
                        ac++;
                    }
                    else
                    {
                        sc++;
                    }
                }

                NodeControl ctl;
                if (arms == 2)
                {
                    ctl = NodeControl.None;
                }
                else if (ac == 0)
                {
                    ctl = NodeControl.RightHandRule;
                }
                else if (sc > 0)
                {
                    ctl = city.Signals == CitySignals.AllArterial ? NodeControl.Signal : minor;
                }
                else
                {
                    ctl = city.Signals != CitySignals.None ? NodeControl.Signal : NodeControl.RightHandRule;
                }

                if (ctl != NodeControl.Signal)
                {
                    if (pass == 1)
                    {
                        b.SetNodeControl(n, ctl);
                    }

                    continue;
                }

                if (pass == 0)
                {
                    signalCount++;
                    continue;
                }

                b.SetNodeControl(n, ctl);
                int i = p % stride;
                int j = p / stride;
                bool onCol = pl.ArtX[i] != 0;
                bool onRow = pl.ArtZ[j] != 0;
                ushort g0;
                ushort g1;
                if (sc == 0)
                {
                    g0 = DefaultGreenTicks;
                    g1 = DefaultGreenTicks;
                }
                else if (onRow && !onCol)
                {
                    g0 = MajorGreenTicks;   // артерия вдоль X
                    g1 = MinorGreenTicks;
                }
                else
                {
                    g0 = MinorGreenTicks;   // артерия вдоль Z
                    g1 = MajorGreenTicks;
                }

                int yellow = rules.DefaultYellowTicks;
                int allRed = rules.DefaultAllRedTicks;
                int cycle = g0 + g1 + (2 * (yellow + allRed));
                float offF = colOrd[i] * (float)city.ArterialEvery * bx / city.ArterialSpeed * 20f;
                int offset = RoundI(offF) % cycle;
                result.SignalPlans[written++] = new SignalPlanSeed
                {
                    Node = n,
                    PhaseCount = 2,
                    Green0 = g0,
                    Green1 = g1,
                    YellowTicks = (ushort)yellow,
                    AllRedTicks = (ushort)allRed,
                    OffsetTicks = (ushort)offset,
                    Adaptive = city.SignalMode == CitySignalMode.Adaptive,
                };
            }

            if (pass == 0)
            {
                if (signalCount > caps.MaxSignalizedNodes)
                {
                    return CreateStatus.NetworkTooLarge;
                }

                result.SignalizedNodes = signalCount;
                result.SignalPlans = new SignalPlanSeed[signalCount];
            }
        }

        return CreateStatus.Ok;
    }

    private static bool ValidateDesc(in CityGenDesc c)
    {
        return c.BlockSize >= 50f &&
            c.SizeX >= 2f * c.BlockSize && c.SizeZ >= 2f * c.BlockSize &&
            c.Scale > 0f && float.IsFinite(c.Scale) &&
            c.ArterialEvery >= 2 &&
            c.ArterialSpeed > 0f && c.StreetSpeed > 0f &&
            c.LaneWidth >= 2.5f && c.LaneWidth <= 5f &&
            c.IrregularNodes >= 0 &&
            c.IrregularJitter >= 0f && c.IrregularJitter <= 0.45f * c.BlockSize &&
            c.NodeJitter >= 0f && c.NodeJitter <= 0.45f * c.BlockSize &&
            c.RemovedStreetShare >= 0f && c.RemovedStreetShare <= 0.5f &&
            c.ConnectorSamples >= 2 && c.ConnectorSamples <= 32;
    }

    /// <summary>Округление «от нуля» (как f32::round в Rust), чтобы порт совпадал на .5.</summary>
    internal static int RoundI(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);

    private static int MarkArterialLines(byte[] lines, int n, int every)
    {
        int count = 0;
        int half = every / 2;
        for (int i = 1; i < n; i++)
        {
            if (i % every == half)
            {
                lines[i] = 1;
                count++;
            }
        }

        if (count == 0)
        {
            lines[n / 2] = 1;
            count = 1;
        }

        return count;
    }

    private static void InitEdge(GridPlan pl, int e, int a, int bNode, bool arterial)
    {
        pl.EdgeA[e] = a;
        pl.EdgeB[e] = bNode;
        pl.EdgeAlive[e] = 1;
        pl.EdgeKind[e] = (byte)(arterial ? RoadKind.Arterial : RoadKind.Street);
        pl.AddAdj(a, e);
        pl.AddAdj(bNode, e);
    }

    private static int Reachable(GridPlan pl)
    {
        for (int p = 0; p < pl.NodeCap; p++)
        {
            pl.UnionParent[p] = -1;   // посещён = 0
        }

        int head = 0;
        int tail = 0;
        pl.Queue[tail++] = 0;
        pl.UnionParent[0] = 0;
        while (head < tail)
        {
            int p = pl.Queue[head++];
            int baseIdx = p * GridPlan.MaxArmSlots;
            for (int s = 0; s < pl.Deg[p]; s++)
            {
                int q = pl.Other(pl.Adj[baseIdx + s], p);
                if (pl.UnionParent[q] < 0)
                {
                    pl.UnionParent[q] = 0;
                    pl.Queue[tail++] = q;
                }
            }
        }

        return tail;
    }

    private static bool ValidateAround(GridPlan pl, int p)
    {
        if (!pl.Validate(p))
        {
            return false;
        }

        int baseIdx = p * GridPlan.MaxArmSlots;
        for (int s = 0; s < pl.Deg[p]; s++)
        {
            if (!pl.Validate(pl.Other(pl.Adj[baseIdx + s], p)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryShift(GridPlan pl, int p, float dx, float dz)
    {
        float ox = pl.X[p];
        float oz = pl.Z[p];
        pl.X[p] = ox + dx;
        pl.Z[p] = oz + dz;
        if (ValidateAround(pl, p))
        {
            pl.NodeFlags[p] |= GridPlan.FlagShifted;
            return true;
        }

        pl.X[p] = ox;
        pl.Z[p] = oz;
        return false;
    }

    /// <summary>0 — условия слияния не выполнены (делается сдвиг), 1 — слито, 2 — отменено валидацией (пропуск).</summary>
    private static int TryMerge(GridPlan pl, int p)
    {
        int q = p + 1;
        if (q >= pl.NodeCap || pl.NodeAlive[q] == 0 ||
            (pl.NodeFlags[q] & (GridPlan.FlagBorder | GridPlan.FlagOnArterialLine | GridPlan.FlagMerged | GridPlan.FlagShifted)) != 0)
        {
            return 0;
        }

        int stride = pl.Nx + 1;
        int epq = pl.HorizontalEdge(p % stride, p / stride);
        if (pl.EdgeAlive[epq] == 0 || pl.EdgeA[epq] != p || pl.EdgeB[epq] != q)
        {
            return 0;
        }

        if (pl.Deg[p] + pl.Deg[q] - 2 > CityGenerator.MaxArms)
        {
            return 0;
        }

        // общие соседи p и q (кроме самих p, q) дали бы кратное ребро
        int bp = p * GridPlan.MaxArmSlots;
        int bq = q * GridPlan.MaxArmSlots;
        for (int s = 0; s < pl.Deg[q]; s++)
        {
            int wq = pl.Other(pl.Adj[bq + s], q);
            if (wq == p)
            {
                continue;
            }

            for (int t = 0; t < pl.Deg[p]; t++)
            {
                if (pl.Other(pl.Adj[bp + t], p) == wq)
                {
                    return 0;
                }
            }
        }

        // сохранить состояние для отката
        byte degP = pl.Deg[p];
        byte degQ = pl.Deg[q];
        float ox = pl.X[p];
        float oz = pl.Z[p];
        for (int s = 0; s < GridPlan.MaxArmSlots; s++)
        {
            pl.SaveAdjP[s] = pl.Adj[bp + s];
            pl.SaveAdjQ[s] = pl.Adj[bq + s];
        }

        for (int s = 0; s < degQ; s++)
        {
            int e = pl.Adj[bq + s];
            pl.SaveEdgeA[s] = pl.EdgeA[e];
            pl.SaveEdgeB[s] = pl.EdgeB[e];
        }

        float mx = (pl.X[p] + pl.X[q]) * 0.5f;
        float mz = (pl.Z[p] + pl.Z[q]) * 0.5f;
        pl.EdgeAlive[epq] = 0;
        pl.RemoveAdj(p, epq);
        pl.RemoveAdj(q, epq);
        int remaining = pl.Deg[q];
        for (int s = 0; s < remaining; s++)
        {
            int e = pl.Adj[bq + s];
            int w = pl.Other(e, q);
            if (p < w)
            {
                pl.EdgeA[e] = p;
                pl.EdgeB[e] = w;
            }
            else
            {
                pl.EdgeA[e] = w;
                pl.EdgeB[e] = p;
            }

            pl.AddAdj(p, e);
        }

        for (int s = 0; s < GridPlan.MaxArmSlots; s++)
        {
            pl.Adj[bq + s] = -1;
        }

        pl.Deg[q] = 0;
        pl.NodeAlive[q] = 0;
        pl.X[p] = mx;
        pl.Z[p] = mz;
        byte oldFlags = pl.NodeFlags[p];
        pl.NodeFlags[p] |= GridPlan.FlagMerged;

        if (ValidateAround(pl, p))
        {
            return 1;
        }

        // откат
        pl.NodeFlags[p] = oldFlags;
        pl.X[p] = ox;
        pl.Z[p] = oz;
        pl.NodeAlive[q] = 1;
        pl.Deg[p] = degP;
        pl.Deg[q] = degQ;
        for (int s = 0; s < GridPlan.MaxArmSlots; s++)
        {
            pl.Adj[bp + s] = pl.SaveAdjP[s];
            pl.Adj[bq + s] = pl.SaveAdjQ[s];
        }

        for (int s = 0; s < degQ; s++)
        {
            int e = pl.SaveAdjQ[s];
            pl.EdgeA[e] = pl.SaveEdgeA[s];
            pl.EdgeB[e] = pl.SaveEdgeB[s];
        }

        pl.EdgeAlive[epq] = 1;
        return 2;
    }
}
