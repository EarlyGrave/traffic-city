using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Tests.Agents;

public class LaneOccupancyTests
{
    private const int Lanes = 4;

    private static int Spawn(VehicleStore v, float s)
    {
        Assert.True(v.TrySpawn(out VehicleHandle h));
        v.S[h.Index] = s;
        return h.Index;
    }

    // Список полосы от головы к хвосту (с учётом ghost-ролей).
    private static List<int> Walk(VehicleStore v, LaneOccupancy occ, int lane)
    {
        var res = new List<int>();
        for (int cur = occ.LaneHead[lane]; cur != -1;)
        {
            res.Add(cur);
            Assert.True(res.Count <= v.Capacity + 1, "цикл в списке");
            cur = v.Lane[cur] == lane ? v.Behind[cur] : v.GhostBehind[cur];
        }

        return res;
    }

    /// <summary>Полная проверка инвариантов всех списков.</summary>
    private static void CheckInvariants(VehicleStore v, LaneOccupancy occ)
    {
        int memberships = 0;
        var seen = new int[v.Capacity];
        for (int lane = 0; lane < occ.MaxLanes; lane++)
        {
            int head = occ.LaneHead[lane];
            int tail = occ.LaneTail[lane];
            Assert.Equal(head == -1, tail == -1);
            int count = 0;
            int prev = -1;
            for (int cur = head; cur != -1;)
            {
                bool primary = v.Lane[cur] == lane;
                bool ghost = v.GhostLane[cur] == lane;
                Assert.True(primary ^ ghost, $"машина {cur} на полосе {lane}: primary={primary} ghost={ghost}");
                int ahead = primary ? v.Ahead[cur] : v.GhostAhead[cur];
                int behind = primary ? v.Behind[cur] : v.GhostBehind[cur];
                Assert.Equal(prev, ahead);
                if (prev != -1)
                {
                    Assert.True(v.S[prev] >= v.S[cur], $"порядок S нарушен: {prev}({v.S[prev]}) перед {cur}({v.S[cur]})");
                }

                count++;
                seen[cur]++;
                Assert.True(count <= v.Capacity, "цикл в списке");
                prev = cur;
                cur = behind;
            }

            Assert.Equal(tail, prev);
            Assert.Equal(count, occ.LaneCount[lane]);
            memberships += count;
        }

        int expected = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                Assert.Equal(-1, v.Lane[i]);
                Assert.Equal(-1, v.GhostLane[i]);
                Assert.Equal(0, seen[i]);
                continue;
            }

            int lanesOf = (v.Lane[i] != -1 ? 1 : 0) + (v.GhostLane[i] != -1 ? 1 : 0);
            Assert.Equal(lanesOf, seen[i]); // в каждом списке не более одного раза, вне ghost - только в одном
            if (v.Lane[i] == -1)
            {
                Assert.Equal(-1, v.Ahead[i]);
                Assert.Equal(-1, v.Behind[i]);
            }

            if (v.GhostLane[i] == -1)
            {
                Assert.Equal(-1, v.GhostAhead[i]);
                Assert.Equal(-1, v.GhostBehind[i]);
            }
            else
            {
                Assert.NotEqual(v.Lane[i], v.GhostLane[i]);
            }

            expected += lanesOf;
        }

        Assert.Equal(expected, memberships);
    }

    [Fact]
    public void PushTail_BuildsOrder_HeadIsLargestS()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int a = Spawn(v, 30);
        int b = Spawn(v, 20);
        int c = Spawn(v, 10);
        occ.PushTail(1, a);
        occ.PushTail(1, b);
        occ.PushTail(1, c);

        Assert.Equal(a, occ.LaneHead[1]);
        Assert.Equal(c, occ.LaneTail[1]);
        Assert.Equal(3, occ.LaneCount[1]);
        Assert.Equal(new[] { a, b, c }, Walk(v, occ, 1));
        Assert.Equal(a, v.Ahead[b]);
        Assert.Equal(-1, v.Ahead[a]);
        Assert.Equal(-1, v.Behind[c]);
        Assert.Equal(1, v.Lane[b]);
        CheckInvariants(v, occ);
    }

    [Fact]
    public void PushHead_PutsCarInFront()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int a = Spawn(v, 10);
        int b = Spawn(v, 20);
        occ.PushHead(0, a);
        occ.PushHead(0, b);
        Assert.Equal(b, occ.LaneHead[0]);
        Assert.Equal(a, occ.LaneTail[0]);
        Assert.Equal(new[] { b, a }, Walk(v, occ, 0));
        CheckInvariants(v, occ);
    }

    [Fact]
    public void Unlink_Middle_Head_Tail_Only()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int[] cars = { Spawn(v, 40), Spawn(v, 30), Spawn(v, 20), Spawn(v, 10) };
        foreach (int c in cars)
        {
            occ.PushTail(2, c);
        }

        occ.Unlink(cars[1]);
        Assert.Equal(new[] { cars[0], cars[2], cars[3] }, Walk(v, occ, 2));
        Assert.Equal(-1, v.Lane[cars[1]]);
        CheckInvariants(v, occ);

        occ.Unlink(cars[0]);
        Assert.Equal(cars[2], occ.LaneHead[2]);
        occ.Unlink(cars[3]);
        Assert.Equal(cars[2], occ.LaneTail[2]);
        occ.Unlink(cars[2]);
        Assert.Equal(-1, occ.LaneHead[2]);
        Assert.Equal(-1, occ.LaneTail[2]);
        Assert.Equal(0, occ.LaneCount[2]);
        occ.Unlink(cars[2]); // повторный - no-op
        CheckInvariants(v, occ);
    }

    [Fact]
    public void InsertSorted_FindsPosition_TiesGoBehindExisting()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int a = Spawn(v, 50);
        int b = Spawn(v, 30);
        int c = Spawn(v, 10);
        occ.InsertSorted(0, b);
        occ.InsertSorted(0, a);
        occ.InsertSorted(0, c);
        Assert.Equal(new[] { a, b, c }, Walk(v, occ, 0));

        int tie = Spawn(v, 30);
        occ.InsertSorted(0, tie);
        Assert.Equal(new[] { a, b, tie, c }, Walk(v, occ, 0));
        CheckInvariants(v, occ);

        occ.FindNeighbours(0, 35, out int ahead, out int behind);
        Assert.Equal(a, ahead);
        Assert.Equal(b, behind);
        occ.FindNeighbours(0, 5, out ahead, out behind);
        Assert.Equal(c, ahead);
        Assert.Equal(-1, behind);
        occ.FindNeighbours(0, 99, out ahead, out behind);
        Assert.Equal(-1, ahead);
        Assert.Equal(a, behind);
        occ.FindNeighbours(3, 1, out ahead, out behind);
        Assert.Equal(-1, ahead);
        Assert.Equal(-1, behind);
    }

    [Fact]
    public void InsertBetween_UsesGivenNeighbours()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int a = Spawn(v, 50);
        int c = Spawn(v, 10);
        occ.PushTail(0, a);
        occ.PushTail(0, c);
        int b = Spawn(v, 30);
        occ.InsertBetween(0, b, a, c);
        Assert.Equal(new[] { a, b, c }, Walk(v, occ, 0));
        CheckInvariants(v, occ);
    }

    [Fact]
    public void LaneChange_LeavesGhost_BothListsSeeCar()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int lead = Spawn(v, 50);
        int mover = Spawn(v, 30);
        int follower = Spawn(v, 10);
        foreach (int c in new[] { lead, mover, follower })
        {
            occ.PushTail(0, c);
        }

        int other = Spawn(v, 40);
        occ.PushTail(1, other);

        occ.FindNeighbours(1, v.S[mover], out int ahead, out int behind);
        occ.BeginLaneChange(mover, 1, ahead, behind);

        Assert.Equal(1, v.Lane[mover]);
        Assert.Equal(0, v.GhostLane[mover]);
        Assert.Equal(new[] { lead, mover, follower }, Walk(v, occ, 0)); // ghost - препятствие для follower
        Assert.Equal(new[] { other, mover }, Walk(v, occ, 1));
        Assert.Equal(mover, v.Ahead[follower]);
        Assert.Equal(3, occ.LaneCount[0]);
        Assert.Equal(2, occ.LaneCount[1]);
        CheckInvariants(v, occ);

        occ.GhostUnlink(mover);
        Assert.Equal(new[] { lead, follower }, Walk(v, occ, 0));
        Assert.Equal(lead, v.Ahead[follower]);
        Assert.Equal(-1, v.GhostLane[mover]);
        Assert.Equal(new[] { other, mover }, Walk(v, occ, 1));
        CheckInvariants(v, occ);
    }

    [Fact]
    public void CancelLaneChange_RestoresOriginalLane()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int a = Spawn(v, 50);
        int b = Spawn(v, 30);
        int c = Spawn(v, 10);
        foreach (int x in new[] { a, b, c })
        {
            occ.PushTail(0, x);
        }

        occ.FindNeighbours(2, 30, out int ahead, out int behind);
        occ.BeginLaneChange(b, 2, ahead, behind);
        occ.CancelLaneChange(b);

        Assert.Equal(0, v.Lane[b]);
        Assert.Equal(-1, v.GhostLane[b]);
        Assert.Equal(new[] { a, b, c }, Walk(v, occ, 0));
        Assert.Equal(0, occ.LaneCount[2]);
        CheckInvariants(v, occ);
    }

    [Fact]
    public void UnlinkAll_RemovesFromBothLists_ThenDespawn()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int a = Spawn(v, 20);
        occ.PushTail(0, a);
        occ.BeginLaneChange(a, 1, -1, -1);
        occ.UnlinkAll(a);
        Assert.Equal(0, occ.LaneCount[0]);
        Assert.Equal(0, occ.LaneCount[1]);
        Assert.True(v.Despawn(v.HandleOf(a)));
        CheckInvariants(v, occ);
    }

    [Fact]
    public void Transition_UnlinkThenPushTail_MovesToNextLane()
    {
        var v = new VehicleStore(8);
        var occ = new LaneOccupancy(v, Lanes);
        int a = Spawn(v, 100);
        int b = Spawn(v, 5);
        occ.PushTail(0, a);
        occ.PushTail(1, b);
        occ.Unlink(a);
        v.S[a] = 2f;
        occ.PushTail(1, a);
        Assert.Equal(new[] { b, a }, Walk(v, occ, 1));
        Assert.Equal(0, occ.LaneCount[0]);
        CheckInvariants(v, occ);
    }

    [Fact]
    public void RandomOps_10000_KeepAllInvariants()
    {
        const int Cap = 48;
        var v = new VehicleStore(Cap);
        var occ = new LaneOccupancy(v, Lanes);
        var rng = Pcg32.Create(0xC0FFEEUL, 7UL);

        for (int step = 0; step < 10_000; step++)
        {
            uint op = rng.NextBelow(100);
            int idx = PickAlive(v, ref rng);
            if (op < 22 || idx == -1)
            {
                if (v.TrySpawn(out VehicleHandle h))
                {
                    v.S[h.Index] = rng.NextBelow(60); // целые S - много равных
                    occ.InsertSorted(rng.NextInt(0, Lanes), h.Index);
                }
            }
            else if (op < 40)
            {
                occ.UnlinkAll(idx);
                Assert.True(v.Despawn(v.HandleOf(idx)));
            }
            else if (op < 60)
            {
                if (v.GhostLane[idx] == -1)
                {
                    int target = (v.Lane[idx] + 1 + rng.NextInt(0, Lanes - 1)) % Lanes;
                    occ.FindNeighbours(target, v.S[idx], out int ahead, out int behind);
                    occ.BeginLaneChange(idx, target, ahead, behind);
                }
            }
            else if (op < 72)
            {
                occ.GhostUnlink(idx);
            }
            else if (op < 78)
            {
                if (v.GhostLane[idx] != -1)
                {
                    occ.CancelLaneChange(idx);
                }
            }
            else if (op < 90)
            {
                // переход на другую полосу: сняли с основной, встали в хвост с S = 0
                if (v.GhostLane[idx] == -1)
                {
                    occ.Unlink(idx);
                    v.S[idx] = 0f;
                    occ.PushTail(rng.NextInt(0, Lanes), idx);
                }
            }
            else if (v.GhostLane[idx] == -1)
            {
                occ.Unlink(idx);
                v.S[idx] = 60f + rng.NextBelow(5);
                int lane = rng.NextInt(0, Lanes);
                if (occ.LaneHead[lane] == -1 || v.S[occ.LaneHead[lane]] <= v.S[idx])
                {
                    occ.PushHead(lane, idx);
                }
                else
                {
                    occ.InsertSorted(lane, idx);
                }
            }

            CheckInvariants(v, occ);
        }
    }

    private static int PickAlive(VehicleStore v, ref Pcg32 rng)
    {
        if (v.AliveCount == 0)
        {
            return -1;
        }

        int start = rng.NextInt(0, v.HighWater);
        for (int k = 0; k < v.HighWater; k++)
        {
            int i = (start + k) % v.HighWater;
            if (v.Pool.IsAlive(i))
            {
                return i;
            }
        }

        return -1;
    }
}
