using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Peds;

/// <summary>
/// Житель-заготовка (pedestrians.md §3, ADR-0011): дом и работа на тротуарах, способ на оба направления, время готовности. SoA по индексу
/// <c>0..Count-1</c>, жители не создаются и не удаляются. Всё - состояние (хэш, сейв <c>RESI</c>), кроме анкеров <c>*Anchor*</c> (scratch для <c>BuildRoad</c>).
/// <para>
/// <c>Where</c>: <see cref="AtHome"/>, <see cref="AtWork"/>, <see cref="ToWork"/> (идёт из дома на работу), <see cref="ToHome"/> (обратно). Спека знает только «2 в пути»;
/// два кода нужны, чтобы после сброса пешеходов (<c>BuildRoad</c>) житель вернулся туда, откуда вышел. Очередь готовых - интрузивная FIFO через
/// <see cref="ReadyNext"/>: <c>-2</c> - жителя в очереди нет, <c>-1</c> - хвост, иначе следующий.
/// </para>
/// <para>
/// P2: автобусные ноги не выбираются (<c>ModeOut = ModeBack = 0</c>, маршруты и остановки -1); все ноги пешие, работа - в радиусе <c>walk_only_m</c>.
/// Выбор пары остановок и способа (§3 п. 2-3) добавляет P4 в <see cref="Create"/>, поля уже заморожены.
/// </para>
/// </summary>
internal sealed class ResidentStore
{
    public const byte AtHome = 0;
    public const byte AtWork = 1;
    public const byte ToWork = 2;
    public const byte ToHome = 3;

    public const int NotQueued = -2;

    public readonly int Capacity;
    public int Count;

    public readonly int[] HomeEdge;
    public readonly int[] HomeSMm;
    public readonly int[] WorkEdge;
    public readonly int[] WorkSMm;
    public readonly int[] HomeZone;
    public readonly int[] WorkZone;
    public readonly byte[] ModeOut;       // 0 пешком, 1 автобус (P4)
    public readonly byte[] ModeBack;
    public readonly int[] RouteOut;       // маршрут и остановки посадки/высадки (индексы в BusRouteStops) или -1 (P4)
    public readonly int[] StopAOut;
    public readonly int[] StopBOut;
    public readonly int[] RouteBack;
    public readonly int[] StopABack;
    public readonly int[] StopBBack;
    public readonly byte[] Where;
    public readonly long[] ReadyTick;     // раньше этого тика поездку не начинать
    public readonly int[] Ped;            // индекс активного пешехода или -1
    public readonly int[] ReadyNext;      // FIFO готовых (см. выше)
    public int ReadyHead = -1;
    public int ReadyTail = -1;

    // ---- scratch для BuildRoad: точка на тротуаре как (сегмент·2 + сторона, s по оси); вне хэша и сейва ----
    public readonly int[] HomeAnchor;
    public readonly float[] HomeAnchorS;
    public readonly int[] WorkAnchor;
    public readonly float[] WorkAnchorS;

    public ResidentStore(int capacity)
    {
        Capacity = capacity;
        HomeEdge = new int[capacity];
        HomeSMm = new int[capacity];
        WorkEdge = new int[capacity];
        WorkSMm = new int[capacity];
        HomeZone = new int[capacity];
        WorkZone = new int[capacity];
        ModeOut = new byte[capacity];
        ModeBack = new byte[capacity];
        RouteOut = new int[capacity];
        StopAOut = new int[capacity];
        StopBOut = new int[capacity];
        RouteBack = new int[capacity];
        StopABack = new int[capacity];
        StopBBack = new int[capacity];
        Where = new byte[capacity];
        ReadyTick = new long[capacity];
        Ped = new int[capacity];
        ReadyNext = new int[capacity];
        HomeAnchor = new int[capacity];
        HomeAnchorS = new float[capacity];
        WorkAnchor = new int[capacity];
        WorkAnchorS = new float[capacity];
        Array.Fill(HomeEdge, -1);
        Array.Fill(WorkEdge, -1);
        Array.Fill(HomeZone, -1);
        Array.Fill(WorkZone, -1);
        Array.Fill(RouteOut, -1);
        Array.Fill(StopAOut, -1);
        Array.Fill(StopBOut, -1);
        Array.Fill(RouteBack, -1);
        Array.Fill(StopABack, -1);
        Array.Fill(StopBBack, -1);
        Array.Fill(Ped, -1);
        Array.Fill(ReadyNext, NotQueued);
    }

    /// <summary>Жители, не вышедшие из дома или с работы (<c>Where</c> 0 или 1); <c>Residents = Idle + Active</c> (PD2).</summary>
    public int IdleCount()
    {
        int n = 0;
        for (int r = 0; r < Count; r++)
        {
            if (Where[r] <= AtWork)
            {
                n++;
            }
        }

        return n;
    }

    public void EnqueueReady(int r)
    {
        ReadyNext[r] = -1;
        if (ReadyTail >= 0)
        {
            ReadyNext[ReadyTail] = r;
        }
        else
        {
            ReadyHead = r;
        }

        ReadyTail = r;
    }

    /// <summary>Снимает голову очереди готовых (очередь не пуста).</summary>
    public int DequeueReady()
    {
        int r = ReadyHead;
        ReadyHead = ReadyNext[r];
        if (ReadyHead < 0)
        {
            ReadyTail = -1;
        }

        ReadyNext[r] = NotQueued;
        return r;
    }

    // ------------------------------------------------------------------ Create (pedestrians.md §3)

    /// <summary>
    /// Создаёт <c>PedCfg.Residents</c> жителей (PRNG <c>Peds</c>, по индексу жителя). Зона дома - внутренняя, пропорционально <c>Weight</c>;
    /// работа - гравитация <c>Weight / d^α</c> среди внутренних зон в радиусе <c>walk_only_m</c> той же компоненты связности (T6); нет кандидата - работа в зоне дома.
    /// Нет внутренних зон с полосами появления - «зоной» считается вся сеть. Нет тротуаров - жителей нет.
    /// </summary>
    public void Create(World w)
    {
        PedNetwork pn = w.Net.PedNet!;
        NetworkStore net = w.Net;
        PedDesc cfg = w.PedCfg;
        Count = 0;
        if (pn.SidewalkEdgeCount == 0 || net.SegmentCount == 0)
        {
            return;
        }

        int n = Math.Min(cfg.Residents, Capacity);
        int zc = net.ZoneCount;
        var usable = new bool[zc];
        var comp = new int[zc];
        var cum = new float[zc + 1];
        int usableCount = 0;
        for (int z = 0; z < zc; z++)
        {
            comp[z] = -1;
            ref readonly ZoneData zd = ref net.Zones[z];
            if (zd.IsExternal != 0 || zd.SpawnLaneCount <= 0 || !(zd.Weight > 0f))
            {
                cum[z + 1] = cum[z];
                continue;
            }

            int seg = net.Lanes[net.ZoneLanes[zd.FirstSpawnLane]].Segment;
            comp[z] = seg >= 0 ? SegmentComponent(pn, seg) : -1;
            usable[z] = comp[z] >= 0;
            cum[z + 1] = cum[z] + (usable[z] ? zd.Weight : 0f);
            if (usable[z])
            {
                usableCount++;
            }
        }

        float alpha = w.Demand.GravityExponent > 0f ? w.Demand.GravityExponent : 1f;
        float walkOnlyM = cfg.WalkOnlyMm * 0.001f;
        Pcg32 rng = w.RngPeds;
        var candW = new float[Math.Max(1, zc)];
        for (int r = 0; r < n; r++)
        {
            int hz = -1;
            int homeComp = -1;
            int he = -1;
            int hs = 0;
            for (int attempt = 0; attempt < 16 && he < 0; attempt++)
            {
                hz = usableCount > 0 ? PickZone(cum, zc, ref rng) : -1;
                if (PickPoint(w, hz, ref rng, out he, out hs))
                {
                    homeComp = pn.PedComp[pn.Edges[he].A];
                }

                if (he >= 0 && homeComp < 0)
                {
                    he = -1;
                }
            }

            if (he < 0)
            {
                continue;   // сеть без пригодных тротуаров: житель не создаётся (индексы подряд - Count ниже)
            }

            int wz = hz;
            int we = -1;
            int ws = 0;
            if (hz >= 0)
            {
                float total = 0f;
                Vec2 hc = net.Zones[hz].Center;
                for (int z = 0; z < zc; z++)
                {
                    candW[z] = 0f;
                    if (!usable[z] || comp[z] != homeComp)
                    {
                        continue;
                    }

                    Vec2 d = net.Zones[z].Center - hc;
                    float dist = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
                    if (dist > walkOnlyM)
                    {
                        continue;
                    }

                    float dd = MathF.Max(dist, 100f);
                    candW[z] = net.Zones[z].Weight / (alpha == 1f ? dd : MathF.Pow(dd, alpha));
                    total += candW[z];
                }

                if (total > 0f)
                {
                    float x = rng.NextFloat01() * total;
                    int pick = -1;
                    for (int z = 0; z < zc; z++)
                    {
                        if (candW[z] <= 0f)
                        {
                            continue;
                        }

                        pick = z;
                        x -= candW[z];
                        if (x < 0f)
                        {
                            break;
                        }
                    }

                    wz = pick >= 0 ? pick : hz;
                }
            }

            for (int attempt = 0; attempt < 8 && we < 0; attempt++)
            {
                if (PickPoint(w, wz, ref rng, out we, out ws) && pn.PedComp[pn.Edges[we].A] != homeComp)
                {
                    we = -1;
                }

                if (we < 0 && attempt == 5)
                {
                    wz = hz;   // зона работы недостижима: работа в зоне дома
                }
            }

            if (we < 0)
            {
                we = he;
                ws = hs;
                wz = hz;
            }

            int i = Count++;
            HomeEdge[i] = he;
            HomeSMm[i] = hs;
            WorkEdge[i] = we;
            WorkSMm[i] = ws;
            HomeZone[i] = hz;
            WorkZone[i] = wz;
            ModeOut[i] = 0;
            ModeBack[i] = 0;
            Where[i] = AtHome;
            ReadyTick[i] = rng.NextBelow((uint)Math.Max(1, cfg.ActivityMaxTicks));
            Ped[i] = -1;
            ReadyNext[i] = NotQueued;
        }

        w.RngPeds = rng;
    }

    private static int PickZone(float[] cum, int zc, ref Pcg32 rng)
    {
        float total = cum[zc];
        float x = rng.NextFloat01() * total;
        for (int z = 0; z < zc; z++)
        {
            if (cum[z + 1] > cum[z] && x < cum[z + 1])
            {
                return z;
            }
        }

        for (int z = zc - 1; z >= 0; z--)
        {
            if (cum[z + 1] > cum[z])
            {
                return z;
            }
        }

        return -1;
    }

    /// <summary>Компонента связности тротуаров сегмента или -1, если у него нет тротуаров.</summary>
    private static int SegmentComponent(PedNetwork pn, int seg)
    {
        for (int side = 0; side < 2; side++)
        {
            pn.SideEdges(seg, side, out int first, out int count);
            if (count > 0)
            {
                return pn.PedComp[pn.Edges[first].A];
            }
        }

        return -1;
    }

    /// <summary>
    /// Случайная точка на тротуаре зоны <paramref name="zone"/> (или любого сегмента, если зоны нет): полоса появления зоны -> сегмент,
    /// сторона, ось <c>s</c> равномерно в <c>[5 м, Len - 5 м]</c> -> ребро и смещение от <c>A</c>, мм.
    /// </summary>
    internal static bool PickPoint(World w, int zone, ref Pcg32 rng, out int edge, out int sMm)
    {
        NetworkStore net = w.Net;
        PedNetwork pn = net.PedNet!;
        edge = -1;
        sMm = 0;
        int seg;
        if (zone >= 0)
        {
            ref readonly ZoneData zd = ref net.Zones[zone];
            int lane = net.ZoneLanes[zd.FirstSpawnLane + (int)rng.NextBelow((uint)zd.SpawnLaneCount)];
            seg = net.Lanes[lane].Segment;
        }
        else
        {
            seg = (int)rng.NextBelow((uint)net.SegmentCount);
        }

        int side = (int)rng.NextBelow(2);
        if ((uint)seg >= (uint)net.SegmentCount)
        {
            return false;
        }

        float len = net.Segments[seg].Length;
        float s = len > 12f ? rng.NextRange(5f, len - 5f) : len * 0.5f;
        return pn.PointOnSide(seg, side, s, out edge, out sMm);
    }
}
