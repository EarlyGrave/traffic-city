using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Секции хэша состояния пешеходов (core-api §9, pedestrians.md §11): добавляются только при <c>peds</c> (<c>hash_version</c> 2), поэтому хэш прежних сценариев не меняется.
/// Порядок фиксирован: PRNG и цель, пул и поля пешеходов (живые слоты; путь - только шаги <c>[0, PathCount)</c>), жители, переходы, очереди остановок,
/// списки пассажиров, очередь путей, гистограммы, счётчики. Производные (списки рёбер, колесо, анкеры, <c>RenderHint</c>) не хэшируются.
/// </summary>
internal static class PedHash
{
    public static void Add(ref Fnv1a64 h, World w)
    {
        PedWorld pw = w.Peds!;
        PedStore ps = pw.P;
        h.Add(w.RngPeds.State);
        h.Add(w.RngPeds.Inc);
        h.Add(w.TargetPeds);

        int high = ps.HighWater;
        h.Add(high);
        for (int i = 0; i < high; i++)
        {
            h.Add(ps.Pool.GenerationOf(i));
            if (!ps.Pool.IsAlive(i))
            {
                continue;
            }

            h.Add(ps.State[i]);
            h.Add(ps.Leg[i]);
            h.Add(ps.Resident[i]);
            h.Add(ps.Edge[i]);
            h.Add(ps.T0[i]);
            h.Add(ps.S0Mm[i]);
            h.Add(ps.VMmT[i]);
            h.Add(ps.EvTick[i]);
            h.Add(ps.PathCursor[i]);
            h.Add(ps.PathCount[i]);
            h.Add(ps.GoalEdge[i]);
            h.Add(ps.GoalSMm[i]);
            h.Add(ps.Next[i]);
            h.Add(ps.Bus[i]);
            h.Add(ps.AlightStop[i]);
            h.Add(ps.TripTick[i]);
            int n = ps.PathCount[i];
            int b = i * ps.MaxSteps;
            for (int k = 0; k < n; k++)
            {
                h.Add(ps.Path[b + k]);
            }
        }

        ResidentStore rs = pw.R;
        h.Add(rs.Count);
        h.Add(rs.ReadyHead);
        h.Add(rs.ReadyTail);
        for (int r = 0; r < rs.Count; r++)
        {
            h.Add(rs.HomeEdge[r]);
            h.Add(rs.HomeSMm[r]);
            h.Add(rs.WorkEdge[r]);
            h.Add(rs.WorkSMm[r]);
            h.Add(rs.HomeZone[r]);
            h.Add(rs.WorkZone[r]);
            h.Add(rs.ModeOut[r]);
            h.Add(rs.ModeBack[r]);
            h.Add(rs.RouteOut[r]);
            h.Add(rs.StopAOut[r]);
            h.Add(rs.StopBOut[r]);
            h.Add(rs.RouteBack[r]);
            h.Add(rs.StopABack[r]);
            h.Add(rs.StopBBack[r]);
            h.Add(rs.Where[r]);
            h.Add(rs.ReadyTick[r]);
            h.Add(rs.Ped[r]);
            h.Add(rs.ReadyNext[r]);
        }

        CrossingState x = pw.X;
        int xn = PedWorld.XCount(w);
        h.Add(xn);
        for (int k = 0; k < xn; k++)
        {
            h.Add(x.XState[k]);
            h.Add(x.QHead[k]);
            h.Add(x.QTail[k]);
            h.Add(x.QCount[k]);
            h.Add(x.OnCross[k]);
            h.Add(x.BusyUntil[k]);
            h.Add(x.ClosedSince[k]);
            h.Add(x.CarWindowUntil[k]);
            h.Add(x.CarsWaiting[k]);
            h.Add(x.WaitSum[k]);
            h.Add(x.Releases[k]);
            h.Add(x.MeasuredWait[k]);
        }

        int stops = w.Net.BusStopCount;
        h.Add(stops);
        for (int s = 0; s < stops; s++)
        {
            h.Add(pw.StopQHead[s]);
            h.Add(pw.StopQTail[s]);
            h.Add(pw.StopQCount[s]);
        }

        int veh = w.Vehicles.HighWater;
        h.Add(veh);
        for (int v = 0; v < veh; v++)
        {
            h.Add(w.Vehicles.Pool.IsAlive(v) ? pw.PaxHead[v] : -1);
        }

        h.Add(pw.PathQueue.Count);
        for (int q = 0; q < pw.PathQueue.Count; q++)
        {
            ref readonly PathRequest req = ref pw.PathQueue.At(q);
            h.Add(req.Index);
            h.Add(req.Generation);
        }

        for (int k = 0; k < PedWorld.HistBuckets; k++)
        {
            h.Add(pw.CrossWaitHist[k]);
            h.Add(pw.StopWaitHist[k]);
        }

        ref readonly SimCounters c = ref w.Counters;
        h.Add(c.PedActive);
        h.Add(c.PedActiveTicksTotal);
        h.Add(c.PedSpawned);
        h.Add(c.PedTripsCompleted);
        h.Add(c.PedTripTicksTotal);
        h.Add(c.PedCrossWaitTicksTotal);
        h.Add(c.PedCrossReleases);
        h.Add(c.PedStopWaitTicksTotal);
        h.Add(c.PedStopBoardings);
        h.Add(c.PedStuckEvents);
        h.Add(c.CarCrossDelayTicks);
        h.Add(c.CrossingClosedTicks);
        h.Add(c.CrossingObservedTicks);
        h.Add(c.BusPassengersAlighted);
        h.Add(c.BusPaxOnBoardTicks);
        h.Add(c.PedPathQueries);
        h.Add(c.PedPathWork);
        h.Add(c.PedNoPath);
        h.Add(c.PedModeFallback);
        h.Add(c.GrantsRevokedPed);
        h.Add(c.PedAllocFailed);
        h.Add(c.PedNetResets);
    }
}
