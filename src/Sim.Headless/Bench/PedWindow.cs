using Sim.Core.Api;

namespace Sim.Headless.Bench;

/// <summary>
/// Метрики пешеходов и пассажиров за окно замера (pedestrians.md §9): разность накопителей <c>SimCounters</c> и гистограмм ожидания (корзины по 1 с) после и до окна.
/// Средние и p95 считает хост; ядро их не хранит. Все цифры - из реальных счётчиков прогона.
/// </summary>
internal sealed class PedWindow
{
    private const double TickS = 1.0 / 20.0;

    public SimCounters Counters;
    public long[] CrossHist = new long[Simulation.PedWaitBuckets];
    public long[] StopHist = new long[Simulation.PedWaitBuckets];

    public int ActiveNow;
    public int Target;
    public double ActiveMean;
    public long Trips;
    public double TripsPerMin;
    public double? CrossWaitMeanS, CrossWaitP95S, StopWaitMeanS, StopWaitP95S;
    public long StuckEvents;
    public double? CarCrossDelaySPerVehH;
    public double? CrossingClosedShare;
    public long Boarded, Alighted;
    public double PaxOnBoardMean;
    public long PathQueries, PathWork, NoPath, ModeFallback;
    public double EventsPerTick;
    public long AllocFailed, NetResets;

    public static PedWindow Snapshot(Simulation sim)
    {
        var p = new PedWindow { Counters = sim.Counters };
        sim.CopyPedWaitHistograms(p.CrossHist, p.StopHist);
        return p;
    }

    public static PedWindow Compute(Simulation sim, PedWindow before, in SimCounters vehBefore, in SimWork workBefore, int ticks)
    {
        SimCounters c = sim.Counters;
        SimCounters b = before.Counters;
        PedWindow after = Snapshot(sim);
        double n = System.Math.Max(1, ticks);
        var w = new PedWindow
        {
            ActiveNow = c.PedActive,
            Target = sim.PedTarget,
            ActiveMean = (c.PedActiveTicksTotal - b.PedActiveTicksTotal) / n,
            Trips = c.PedTripsCompleted - b.PedTripsCompleted,
            StuckEvents = c.PedStuckEvents - b.PedStuckEvents,
            Boarded = c.PedStopBoardings - b.PedStopBoardings,
            Alighted = c.BusPassengersAlighted - b.BusPassengersAlighted,
            PaxOnBoardMean = (c.BusPaxOnBoardTicks - b.BusPaxOnBoardTicks) / n,
            PathQueries = c.PedPathQueries - b.PedPathQueries,
            PathWork = c.PedPathWork - b.PedPathWork,
            NoPath = c.PedNoPath - b.PedNoPath,
            ModeFallback = c.PedModeFallback - b.PedModeFallback,
            AllocFailed = c.PedAllocFailed - b.PedAllocFailed,
            NetResets = c.PedNetResets - b.PedNetResets,
            EventsPerTick = (sim.Work.PedEvents - workBefore.PedEvents) / n,
        };
        w.TripsPerMin = w.Trips / (n * TickS / 60.0);
        long crossReleases = c.PedCrossReleases - b.PedCrossReleases;
        if (crossReleases > 0)
        {
            w.CrossWaitMeanS = (c.PedCrossWaitTicksTotal - b.PedCrossWaitTicksTotal) * TickS / crossReleases;
            w.CrossWaitP95S = P95(after.CrossHist, before.CrossHist, crossReleases);
        }

        long boardings = c.PedStopBoardings - b.PedStopBoardings;
        if (boardings > 0)
        {
            w.StopWaitMeanS = (c.PedStopWaitTicksTotal - b.PedStopWaitTicksTotal) * TickS / boardings;
            w.StopWaitP95S = P95(after.StopHist, before.StopHist, boardings);
        }

        long vt = c.VehicleTicksTotal - vehBefore.VehicleTicksTotal;
        if (vt > 0)
        {
            w.CarCrossDelaySPerVehH = (c.CarCrossDelayTicks - b.CarCrossDelayTicks) / (double)vt * 3600.0;
        }

        long observed = c.CrossingObservedTicks - b.CrossingObservedTicks;
        if (observed > 0)
        {
            w.CrossingClosedShare = (c.CrossingClosedTicks - b.CrossingClosedTicks) / (double)observed;
        }

        return w;
    }

    /// <summary>Наименьшая корзина (1 с), в которой накопленная доля выборок за окно &gt;= 95%.</summary>
    private static double P95(long[] after, long[] before, long total)
    {
        long need = ((total * 95) + 99) / 100;
        long acc = 0;
        for (int i = 0; i < after.Length; i++)
        {
            acc += after[i] - before[i];
            if (acc >= need)
            {
                return i;
            }
        }

        return after.Length - 1;
    }
}
