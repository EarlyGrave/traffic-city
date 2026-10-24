using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Util;

namespace Sim.Core.Tests.Agents;

public class AgentsAllocationTests
{
    private static void Churn(VehicleStore v, LaneOccupancy occ, PendingTrips q, ref Pcg32 rng, int ops)
    {
        const int Lanes = 4;
        for (int n = 0; n < ops; n++)
        {
            if (v.TrySpawn(out VehicleHandle h))
            {
                int i = h.Index;
                v.S[i] = rng.NextBelow(100);
                occ.InsertSorted(rng.NextInt(0, Lanes), i);
                int target = (v.Lane[i] + 1) % Lanes;
                occ.FindNeighbours(target, v.S[i], out int ahead, out int behind);
                occ.BeginLaneChange(i, target, ahead, behind);
                occ.GhostUnlink(i);
                occ.Unlink(i);
                v.S[i] = 0f;
                occ.PushTail(0, i);
                if (rng.NextBelow(4) != 0)
                {
                    occ.UnlinkAll(i);
                    v.Despawn(h);
                }
            }
            else
            {
                // ёмкость исчерпана - освобождаем всё, что живо
                for (int i = 0; i < v.HighWater; i++)
                {
                    if (v.Pool.IsAlive(i))
                    {
                        occ.UnlinkAll(i);
                        v.Despawn(v.HandleOf(i));
                    }
                }
            }

            var trip = new PendingTrip { FromZone = n, ToZone = n + 1, FromLane = 1, FromS = 2f, Type = 1 };
            q.TryEnqueue(trip);
            if ((n & 1) == 0)
            {
                q.TryDequeue(out _);
            }
        }
    }

    [Fact]
    public void HotOperations_Allocate_ZeroBytes()
    {
        var v = new VehicleStore(64);
        var occ = new LaneOccupancy(v, 4);
        var q = new PendingTrips(16);
        var rng = Pcg32.Create(1UL, 2UL);

        Churn(v, occ, q, ref rng, 5_000); // прогрев JIT

        Assert.Equal(0, AllocationProbe.Measure(() => Churn(v, occ, q, ref rng, 100_000)));
    }
}
