using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Metrics;

/// <summary>
/// Метрики качества трафика (roadmap 2.6c; критерий stuck владельца 2026-10-25): длинные ожидания и распределение потока по полосам.
/// Считаются выборкой раз в <see cref="SampleTicks"/> тиков (1 с) в фазе 9 - целыми, без аллокаций (scratch выделяется один раз и растёт
/// только со сменой сети), вне хэша и на поведение не влияют. Эпизод стояния = непрерывный <c>StillTicks</c>: он растёт на 1 за тик,
/// поэтому на выборках с шагом 20 тиков значение попадает в [порог, порог + 20) ровно один раз за эпизод. Максимум стояния и число стоящих
/// различаются с точностью до 1 с (между выборками). Машины Q-meso в очереди <c>StillTicks</c> не копят (stuck очереди считает QueueMeso) -
/// в этих метриках не видны.
/// </summary>
internal static class QualityMetrics
{
    public const int SampleTicks = 20;
    public const int Still60Ticks = 1200;
    public const int Still120Ticks = 2400;
    public const int ShareBuckets = 10;   // доля самой загруженной полосы направления, корзины по 10% (0-10% ... 90-100%)
    private const int MinVehiclesForShare = 4;

    public static void Sample(World w)
    {
        if (w.Tick % SampleTicks != 0)
        {
            return;
        }

        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        ref SimCounters c = ref w.Counters;
        int laneCount = net.LaneCount;
        if (w.LaneScratch.Length < laneCount)
        {
            w.LaneScratch = new int[net.Lanes.Length];
        }

        int[] perLane = w.LaneScratch;
        City.CityKpi? kpi = w.City?.Kpi;   // случай недовольства LongWait (progression.md §6.1): эпизод стояния >= 60 с
        int hw = v.HighWater;
        int maxStill = 0;
        int concurrent = 0;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i))
            {
                continue;
            }

            int still = v.StillTicks[i];
            if (still > maxStill)
            {
                maxStill = still;
            }

            if (still >= Still60Ticks)
            {
                concurrent++;
                if (still < Still60Ticks + SampleTicks)
                {
                    c.StillEpisodes60++;
                    if (kpi != null)
                    {
                        int seg = PlaceEvents.SegmentOfLane(net, v.Lane[i]);
                        kpi.OnLongWait(seg, seg >= 0 ? net.Segments[seg].Generation : 0);   // случай и жалоба у сегмента (progression.md §6.1)
                    }
                }

                if (still >= Still120Ticks && still < Still120Ticks + SampleTicks)
                {
                    c.StillEpisodes120++;
                }
            }

            int lane = v.Lane[i];
            if ((uint)lane < (uint)laneCount)
            {
                perLane[lane]++;
            }
        }

        if (maxStill > c.StillMaxTicks)
        {
            c.StillMaxTicks = maxStill;
        }

        if (concurrent > c.StillConcurrent60Max)
        {
            c.StillConcurrent60Max = concurrent;
        }

        int segCount = net.SegmentCount;
        for (int s = 0; s < segCount; s++)
        {
            ref readonly SegmentData sg = ref net.Segments[s];
            Direction(ref c, perLane, sg.FirstLaneAB, sg.LanesAB);
            Direction(ref c, perLane, sg.FirstLaneBA, sg.LanesBA);
        }

        System.Array.Clear(perLane, 0, laneCount);
    }

    private static void Direction(ref SimCounters c, int[] perLane, int first, int lanes)
    {
        if (lanes < 2)
        {
            return;
        }

        int sum = 0;
        int max = 0;
        for (int k = 0; k < lanes; k++)
        {
            int n = perLane[first + k];
            sum += n;
            if (n > max)
            {
                max = n;
            }
        }

        if (sum < MinVehiclesForShare)
        {
            return;
        }

        int permille = max * 1000 / sum;
        c.LaneShareSamples++;
        c.LaneShareMaxPermilleSum += permille;
        int b = permille / (1000 / ShareBuckets);
        switch (b >= ShareBuckets ? ShareBuckets - 1 : b)
        {
            case 0: c.LaneShareH0++; break;
            case 1: c.LaneShareH1++; break;
            case 2: c.LaneShareH2++; break;
            case 3: c.LaneShareH3++; break;
            case 4: c.LaneShareH4++; break;
            case 5: c.LaneShareH5++; break;
            case 6: c.LaneShareH6++; break;
            case 7: c.LaneShareH7++; break;
            case 8: c.LaneShareH8++; break;
            default: c.LaneShareH9++; break;
        }
    }
}
