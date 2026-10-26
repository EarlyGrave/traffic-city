using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

// Построение сети пешеходов (pedestrians.md §2). Порядок обходов фиксирован (узлы, слоты, сегменты, остановки по возрастанию индекса),
// математика - только + - * / и sqrt, поэтому результат одинаков на любой платформе и при каждой пересборке.
internal sealed partial class PedNetwork
{
    private const float ParallelSin = 0.2f;          // |sin θ| меньше - рукава считаются лежащими на одной прямой
    private const float MidBeforeStopM = 20f;        // переход на перегоне стоит на столько метров до остановки (по ходу A→B)
    private const float MidEdgeMarginM = 15f;        // и не ближе этого к концам сегмента

    /// <summary>
    /// Полная пересборка по текущей сети (<c>Create</c>, после <c>BuildRoad</c>, после <c>Load</c>). Не аллоцирует.
    /// false - не хватило ёмкости массивов (выводится из ёмкостей сети, поэтому на практике не случается).
    /// </summary>
    public bool Rebuild(World w)
    {
        NetworkStore net = w.Net;
        float laneW = w.Builder.Options.LaneWidth;
        float sw = SidewalkWidthM;

        EdgeCount = 0;
        SidewalkEdgeCount = 0;
        WalkwayCount = 0;
        PointCount = 0;
        MidCount = 0;
        CrossingNodeCount = 0;
        CompCount = 0;
        _armUsed = net.NodeArmCount;
        Array.Clear(NodePos);
        Array.Clear(XIn);
        Array.Clear(XOut);
        Array.Fill(XEdge, -1);
        Array.Fill(StopMid, -1);
        Array.Fill(LaneMidX, -1);
        Array.Fill(PedComp, -1);
        Array.Fill(_midHead, -1);
        Array.Fill(MidEdge, -1);
        Array.Fill(MidNextFwd, -1);
        Array.Fill(MidNextBwd, -1);
        Array.Clear(MidSegment);
        Array.Clear(MidS);
        if (_conn.CrossInX != null && _conn.CrossOutX != null)
        {
            Array.Fill(_conn.CrossInX, -1);
            Array.Fill(_conn.CrossOutX, -1);
        }

        BuildCorners(net, laneW, sw);
        BuildMids(net);
        if (!BuildSidewalks(net, laneW, sw))
        {
            return false;
        }

        if (!BuildEndCaps(net, laneW, sw))
        {
            return false;
        }

        WalkwayCount = EdgeCount;
        if (!BuildNodeCrossings(w, laneW, sw) || !BuildMidCrossings(net, laneW, sw))
        {
            return false;
        }

        BuildCrossingMasks(net);
        BuildAdjacency();
        BuildComponents();
        BuildCumulative();
        BuildStamp++;
        return true;
    }

    // ---------------------------------------------------------------- углы

    private static float HalfRoad(in SegmentData sd, float laneW) => (sd.LanesAB + sd.LanesBA) * laneW * 0.5f;

    private static Vec2 Perp(Vec2 d) => new(-d.Z, d.X);   // «вправо» в смысле RightNormal сети: к рукаву со следующим по часовой индексом

    private static float Cross(Vec2 a, Vec2 b) => (a.X * b.Z) - (a.Z * b.X);

    private int CornerId(in NodeData nd, int k)
    {
        int ac = nd.ArmCount;
        if (ac == 1)
        {
            return k == 0 ? nd.FirstArm : DeadBase + nd.FirstArm;
        }

        return nd.FirstArm + (((k % ac) + ac) % ac);
    }

    private static int LocalArm(NetworkStore net, in NodeData nd, int segment)
    {
        for (int a = 0; a < nd.ArmCount; a++)
        {
            if (net.NodeArms[nd.FirstArm + a] == segment)
            {
                return a;
            }
        }

        return 0;
    }

    private static bool Built(NetworkStore net, int node) => net.Nodes[node].FirstArm >= 0 && net.Nodes[node].ArmCount > 0;

    /// <summary>Позиции углов: пересечение параллелей соседних рукавов, сдвинутых на <c>h + sw/2</c> в сторону сектора между ними (§2.1).</summary>
    private void BuildCorners(NetworkStore net, float laneW, float sw)
    {
        float half = sw * 0.5f;
        for (int n = 0; n < net.NodeCount; n++)
        {
            ref readonly NodeData nd = ref net.Nodes[n];
            if (!Built(net, n))
            {
                continue;
            }

            int ac = nd.ArmCount;
            int fa = nd.FirstArm;
            Vec2 p = nd.Position;
            if (ac == 1)
            {
                Vec2 d = net.ArmDir[fa];
                float o = HalfRoad(in net.Segments[net.NodeArms[fa]], laneW) + half;
                NodePos[fa] = p + (Perp(d) * o);
                NodePos[DeadBase + fa] = p + (Perp(d) * -o);
                continue;
            }

            for (int k = 0; k < ac; k++)
            {
                int k2 = k + 1 == ac ? 0 : k + 1;
                Vec2 d1 = net.ArmDir[fa + k];
                Vec2 d2 = net.ArmDir[fa + k2];
                float o1 = HalfRoad(in net.Segments[net.NodeArms[fa + k]], laneW) + half;
                float o2 = HalfRoad(in net.Segments[net.NodeArms[fa + k2]], laneW) + half;
                Vec2 p1 = Perp(d1) * o1;
                float cr = Cross(d1, d2);
                if (MathF.Abs(cr) < ParallelSin)
                {
                    NodePos[fa + k] = p + p1;
                    continue;
                }

                Vec2 p2 = Perp(d2) * -o2;
                float t = Cross(p2 - p1, d2) / cr;
                NodePos[fa + k] = p + p1 + (d1 * t);
            }
        }
    }

    // ---------------------------------------------------------------- переходы на перегоне: поиск мест

    private void BuildMids(NetworkStore net)
    {
        if (MidCap == 0)
        {
            return;
        }

        float cw2 = 2f * CrosswalkWidthM;
        for (int i = 0; i < net.BusStopCount; i++)
        {
            ref readonly BusStopData bs = ref net.BusStops[i];
            if ((uint)bs.Lane >= (uint)net.LaneCount)
            {
                continue;
            }

            ref readonly LaneData ld = ref net.Lanes[bs.Lane];
            if (ld.Kind != LaneKind.Normal || ld.Segment < 0)
            {
                continue;
            }

            ref readonly SegmentData sd = ref net.Segments[ld.Segment];
            float len = sd.Length;
            if (sd.Kind != RoadKind.Street || len < 2f * MidEdgeMarginM || !Built(net, sd.NodeA) || !Built(net, sd.NodeB))
            {
                continue;
            }

            float sAxis = ld.Direction == 0 ? bs.S : len - bs.S;
            float sc = MathF.Min(MathF.Max(sAxis - MidBeforeStopM, MidEdgeMarginM), len - MidEdgeMarginM);

            // Остановки, чьи переходы ближе двух ширин перехода, делят один переход.
            int found = -1;
            for (int m = _midHead[ld.Segment]; m >= 0; m = _midPrev[m])
            {
                if (MathF.Abs(MidS[m] - sc) < cw2)
                {
                    found = m;
                    break;
                }
            }

            if (found < 0)
            {
                found = MidCount++;
                MidSegment[found] = ld.Segment;
                MidS[found] = sc;
                _midPrev[found] = _midHead[ld.Segment];
                _midHead[ld.Segment] = found;
            }

            StopMid[i] = found;
        }
    }

    // ---------------------------------------------------------------- тротуары

    private bool BuildSidewalks(NetworkStore net, float laneW, float sw)
    {
        int segs = net.SegmentCount;
        SideCount = 2 * segs;
        for (int seg = 0; seg < segs; seg++)
        {
            ref readonly SegmentData sd = ref net.Segments[seg];
            if (!Built(net, sd.NodeA) || !Built(net, sd.NodeB))
            {
                SideFirstEdge[2 * seg] = EdgeCount;
                SideFirstEdge[(2 * seg) + 1] = EdgeCount;
                continue;
            }

            // Переходы сегмента по возрастанию s (ничья - по номеру).
            int cnt = 0;
            for (int m = _midHead[seg]; m >= 0; m = _midPrev[m])
            {
                int pos = cnt++;
                while (pos > 0 && (MidS[_bmid[pos - 1]] > MidS[m] || (MidS[_bmid[pos - 1]] == MidS[m] && _bmid[pos - 1] > m)))
                {
                    _bmid[pos] = _bmid[pos - 1];
                    pos--;
                }

                _bmid[pos] = m;
            }

            for (int j = 0; j < cnt; j++)
            {
                int m = _bmid[j];
                MidNextFwd[m] = j + 1 < cnt ? _bmid[j + 1] : -1;
                MidNextBwd[m] = j > 0 ? _bmid[j - 1] : -1;
            }

            if (cnt > 0)
            {
                MarkLanesWithMid(in sd, _bmid[0], _bmid[cnt - 1]);
            }

            float off = HalfRoad(in sd, laneW) + (sw * 0.5f);
            ref readonly NodeData ndA = ref net.Nodes[sd.NodeA];
            ref readonly NodeData ndB = ref net.Nodes[sd.NodeB];
            int la = LocalArm(net, in ndA, seg);
            int lb = LocalArm(net, in ndB, seg);
            for (int side = 0; side < 2; side++)
            {
                SideFirstEdge[(2 * seg) + side] = EdgeCount;
                int pc = ShiftAxis(net, in sd, side == 0 ? off : -off);
                float total = _ss[pc - 1];
                int cornerA = CornerId(in ndA, side == 0 ? la : la - 1);
                int cornerB = CornerId(in ndB, side == 0 ? lb - 1 : lb);

                _bnd[0] = 0f;
                for (int j = 0; j < cnt; j++)
                {
                    float sc = MathF.Min(MidS[_bmid[j]], total);
                    _bnd[j + 1] = sc;
                    NodePos[MidNode(_bmid[j], side)] = Interpolate(pc, sc);
                }

                _bnd[cnt + 1] = total;
                if (EdgeCount + cnt + 1 > _edgeCap || PointCount + pc + (2 * (cnt + 1)) > _pointCap)
                {
                    return false;
                }

                for (int e = 0; e <= cnt; e++)
                {
                    int a = e == 0 ? cornerA : MidNode(_bmid[e - 1], side);
                    int b = e == cnt ? cornerB : MidNode(_bmid[e], side);
                    float s0 = _bnd[e];
                    float s1 = _bnd[e + 1];
                    int first = PointCount;
                    Points[PointCount++] = NodePos[a];
                    for (int i = 0; i < pc; i++)
                    {
                        if (_ss[i] > s0 && _ss[i] < s1)
                        {
                            Points[PointCount++] = _sp[i];
                        }
                    }

                    Points[PointCount++] = NodePos[b];
                    AddEdge(PedEdgeKind.Sidewalk, a, b, PolylineMm(first, PointCount - first), 0, (byte)side, seg, -1, -1, s0, s1, first, PointCount - first);
                }
            }
        }

        SideFirstEdge[SideCount] = EdgeCount;
        SidewalkEdgeCount = EdgeCount;
        return true;
    }

    /// <summary>Заполняет <c>_sp/_ss</c>: ось сегмента, сдвинутая вправо по ходу A→B на <paramref name="off"/> (миттер в изломах); возвращает число точек.</summary>
    private int ShiftAxis(NetworkStore net, in SegmentData sd, float off)
    {
        int pc = sd.PointCount;
        int fp = sd.FirstPoint;
        _ss[0] = 0f;
        for (int i = 1; i < pc; i++)
        {
            Vec2 d = net.SegmentPoints[fp + i] - net.SegmentPoints[fp + i - 1];
            _ss[i] = _ss[i - 1] + MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        }

        for (int i = 0; i < pc; i++)
        {
            Vec2 nPrev = i > 0 ? RightNormal(net.SegmentPoints[fp + i - 1], net.SegmentPoints[fp + i]) : default;
            Vec2 nNext = i < pc - 1 ? RightNormal(net.SegmentPoints[fp + i], net.SegmentPoints[fp + i + 1]) : default;
            Vec2 n;
            if (i == 0)
            {
                n = nNext;
            }
            else if (i == pc - 1)
            {
                n = nPrev;
            }
            else
            {
                float denom = 1f + (nPrev.X * nNext.X) + (nPrev.Z * nNext.Z);
                n = denom > 0.2f ? (nPrev + nNext) * (1f / denom) : nNext;
            }

            _sp[i] = net.SegmentPoints[fp + i] + (n * off);
        }

        return pc;
    }

    private static Vec2 RightNormal(Vec2 a, Vec2 b)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float inv = 1f / MathF.Sqrt((dx * dx) + (dz * dz));
        return new Vec2(-dz * inv, dx * inv);
    }

    /// <summary>Точка сдвинутой полилинии (<c>_sp/_ss</c>) на параметре <paramref name="s"/>.</summary>
    private Vec2 Interpolate(int pc, float s)
    {
        int i = 1;
        while (i < pc - 1 && _ss[i] < s)
        {
            i++;
        }

        float span = _ss[i] - _ss[i - 1];
        float t = span > 0f ? (s - _ss[i - 1]) / span : 0f;
        return _sp[i - 1] + ((_sp[i] - _sp[i - 1]) * t);
    }

    private void MarkLanesWithMid(in SegmentData sd, int firstFwd, int firstBwd)
    {
        for (int k = 0; k < sd.LanesAB; k++)
        {
            LaneMidX[sd.FirstLaneAB + k] = firstFwd;
        }

        for (int k = 0; k < sd.LanesBA; k++)
        {
            LaneMidX[sd.FirstLaneBA + k] = firstBwd;
        }
    }

    private int PolylineMm(int first, int count)
    {
        float len = 0f;
        for (int i = 1; i < count; i++)
        {
            Vec2 d = Points[first + i] - Points[first + i - 1];
            len += MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
        }

        return DetMath.ToIntSat(MathF.Round(len * 1000f));
    }

    // ---------------------------------------------------------------- обходы тупиков

    private bool BuildEndCaps(NetworkStore net, float laneW, float sw)
    {
        for (int n = 0; n < net.NodeCount; n++)
        {
            ref readonly NodeData nd = ref net.Nodes[n];
            if (!Built(net, n) || nd.ArmCount != 1)
            {
                continue;
            }

            if (EdgeCount + 1 > _edgeCap || PointCount + 3 > _pointCap)
            {
                return false;
            }

            int fa = nd.FirstArm;
            int seg = net.NodeArms[fa];
            int first = PointCount;
            Points[PointCount++] = NodePos[fa];
            Points[PointCount++] = nd.Position + (net.ArmDir[fa] * (-0.5f * sw));
            Points[PointCount++] = NodePos[DeadBase + fa];
            int mm = DetMath.ToIntSat(MathF.Round(((2f * HalfRoad(in net.Segments[seg], laneW)) + sw) * 1000f));
            AddEdge(PedEdgeKind.EndCap, fa, DeadBase + fa, mm, 0, 0, seg, n, fa, 0f, 0f, first, 3);
        }

        return true;
    }

    // ---------------------------------------------------------------- переходы

    private bool BuildNodeCrossings(World w, float laneW, float sw)
    {
        NetworkStore net = w.Net;
        for (int n = 0; n < net.NodeCount; n++)
        {
            ref readonly NodeData nd = ref net.Nodes[n];
            if (!Built(net, n) || nd.ArmCount < 3)
            {
                continue;
            }

            int ac = nd.ArmCount;
            int fa = nd.FirstArm;
            bool signal = nd.Control == NodeControl.Signal;
            for (int k = 0; k < ac; k++)
            {
                int slot = fa + k;
                int seg = net.NodeArms[slot];
                ref readonly SegmentData sd = ref net.Segments[seg];
                bool arterial = sd.Kind == RoadKind.Arterial;
                if (arterial && !signal && Cfg.ArterialCrossings != ArterialCrossings.All)
                {
                    continue;
                }

                if (EdgeCount + 1 > _edgeCap || PointCount + 2 > _pointCap)
                {
                    return false;
                }

                int a = fa + (((k - 1) + ac) % ac);
                int b = slot;
                int first = PointCount;
                Points[PointCount++] = NodePos[a];
                Points[PointCount++] = NodePos[b];
                int mm = DetMath.ToIntSat(MathF.Round(((2f * HalfRoad(in sd, laneW)) + sw) * 1000f));
                int idx = AddEdge(PedEdgeKind.Crossing, a, b, mm, signal ? (byte)1 : (byte)0, 0, seg, n, slot, 0f, 0f, first, 2);
                Edges[idx].CostTicks += WaitTicks(w, signal, arterial);
                XEdge[slot] = idx;
                CrossingNodeCount++;
            }
        }

        return true;
    }

    private bool BuildMidCrossings(NetworkStore net, float laneW, float sw)
    {
        for (int m = 0; m < MidCount; m++)
        {
            if (EdgeCount + 1 > _edgeCap || PointCount + 2 > _pointCap)
            {
                return false;
            }

            int seg = MidSegment[m];
            int a = MidNode(m, 0);
            int b = MidNode(m, 1);
            int first = PointCount;
            Points[PointCount++] = NodePos[a];
            Points[PointCount++] = NodePos[b];
            int mm = DetMath.ToIntSat(MathF.Round(((2f * HalfRoad(in net.Segments[seg], laneW)) + sw) * 1000f));
            int idx = AddEdge(PedEdgeKind.MidCrossing, a, b, mm, 0, 0, seg, -1, m, MidS[m], MidS[m], first, 2);
            Edges[idx].CostTicks += 2 * 20;   // улица без светофора: 2 с (§2.3)
            MidEdge[m] = idx;
        }

        return true;
    }

    /// <summary>Статический приор ожидания у перехода, тики (§2.3): Webster (C − g)² / 2C для светофора, 2 с на улице и 6 с на артерии без него.</summary>
    private static int WaitTicks(World w, bool signal, bool arterial)
    {
        if (!signal)
        {
            return (arterial ? 6 : 2) * 20;
        }

        long g = (w.Rules.SignalMinGreenTicks + (long)w.Rules.SignalMaxGreenTicks) / 2;
        long c = 2 * (g + w.Rules.DefaultYellowTicks + w.Rules.DefaultAllRedTicks);   // двухфазный приор; измеренное среднее добавит P3
        return c > 0 ? (int)(((c - g) * (c - g)) / (2 * c)) : 0;
    }

    /// <summary>Маски <c>XIn/XOut</c> перехода по connector'ам узла и производные <c>CrossInX/CrossOutX</c> ConnectorStore (§2.2).</summary>
    private void BuildCrossingMasks(NetworkStore net)
    {
        for (int n = 0; n < net.NodeCount; n++)
        {
            ref readonly NodeData nd = ref net.Nodes[n];
            if (!Built(net, n) || nd.ArmCount < 3 || nd.ConnectorCount == 0)
            {
                continue;
            }

            for (int i = 0; i < nd.ConnectorCount; i++)
            {
                int g = net.Lanes[nd.FirstConnector + i].ConflictGroup;
                if (g < 0)
                {
                    continue;
                }

                ulong bit = 1UL << _conn.LocalBit[g];
                int slotIn = nd.FirstArm + _conn.FromArm[g];
                int slotOut = nd.FirstArm + LocalArm(net, in nd, net.Lanes[_conn.OutLane[g]].Segment);
                if (XEdge[slotIn] >= 0)
                {
                    XIn[slotIn] |= bit;
                    if (_conn.CrossInX != null)
                    {
                        _conn.CrossInX[g] = slotIn;
                    }
                }

                if (XEdge[slotOut] >= 0)
                {
                    XOut[slotOut] |= bit;
                    if (_conn.CrossOutX != null)
                    {
                        _conn.CrossOutX[g] = slotOut;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- рёбра, смежность, компоненты

    private int AddEdge(PedEdgeKind kind, int a, int b, int lenMm, byte flags, byte side, int seg, int node, int slot, float s0, float s1, int first, int count)
    {
        int idx = EdgeCount++;
        Edges[idx] = new PedEdgeData
        {
            A = a,
            B = b,
            LenMm = lenMm,
            CostTicks = (int)(((lenMm * 20L) + (NominalSpeedMmPerSec / 2)) / NominalSpeedMmPerSec),
            Kind = kind,
            Side = side,
            Flags = flags,
            Segment = seg,
            Node = node,
            Slot = slot,
            S0 = s0,
            S1 = s1,
            FirstPoint = first,
            PointCount = count,
        };
        return idx;
    }

    private void BuildAdjacency()
    {
        Array.Clear(AdjFirst);
        for (int e = 0; e < EdgeCount; e++)
        {
            AdjFirst[Edges[e].A + 1]++;
            if (Edges[e].B != Edges[e].A)
            {
                AdjFirst[Edges[e].B + 1]++;
            }
        }

        for (int v = 0; v < NodeCap; v++)
        {
            AdjFirst[v + 1] += AdjFirst[v];
        }

        for (int v = 0; v < NodeCap; v++)
        {
            _stack[v] = AdjFirst[v];
        }

        for (int e = 0; e < EdgeCount; e++)
        {
            int a = Edges[e].A;
            int b = Edges[e].B;
            AdjEdge[_stack[a]++] = e;
            if (b != a)
            {
                AdjEdge[_stack[b]++] = e;
            }
        }
    }

    /// <summary>Компоненты связности BFS по возрастанию индекса узла (T6): номер компоненты канонический.</summary>
    private void BuildComponents()
    {
        for (int v = 0; v < NodeCap; v++)
        {
            if (PedComp[v] >= 0 || AdjFirst[v + 1] == AdjFirst[v])
            {
                continue;
            }

            int comp = CompCount++;
            int sp = 0;
            _stack[sp++] = v;
            PedComp[v] = comp;
            while (sp > 0)
            {
                int u = _stack[--sp];
                for (int i = AdjFirst[u]; i < AdjFirst[u + 1]; i++)
                {
                    int o = Other(AdjEdge[i], u);
                    if (PedComp[o] < 0)
                    {
                        PedComp[o] = comp;
                        _stack[sp++] = o;
                    }
                }
            }
        }
    }
}
