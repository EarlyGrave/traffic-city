using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.City;

/// <summary>
/// Клапан перегрузки (ADR-0035, res-congestion.md §10): город переживает затор и восстанавливается, когда спрос падает или сеть растёт.
/// <list type="number">
/// <item>Потолок входа: машин жителей в сети и в очереди выпуска не больше <see cref="DensityPerLaneKm"/> на полосо-км живых полос (производное сети, кэш по <c>Net.Version</c>).
/// Работает, только если этот потолок строже пула <c>max_vehicles</c>: иначе ограничивает пул, как раньше.</item>
/// <item>Пока потолок достигнут (сеть насыщена), заявка машинной ноги, не попавшая в очередь дольше <c>wait_max_s / 4</c>, безнадёжна: дедлайн наступает сразу
/// (нога туда - другой способ или тур сорван, обратно - домой агрегатом); тур, опоздавший к выезду дольше того же порога, срывается и не переносится в ночь.</item>
/// <item>Машина жителя, стоящая дольше <c>2 · stuck_threshold_s</c> (тупик gridlock) или едущая дольше <c>2 · wait_max_s</c> (кружит в заторе), снимается с дороги и «прибывает» к цели ноги
/// (<see cref="Teleport"/>).</item>
/// </list>
/// Новых параметров и состояния сейва нет: пороги выводятся из сохраняемых <c>wait_max_s</c> и <c>stuck_threshold_s</c>; счётчики <see cref="ValveStats"/> - диагностика вне хэша и сейва.
/// </summary>
internal static class OverloadValve
{
    /// <summary>Потолок плотности сети, машин на полосо-км: s10k - 985 полосо-км, 7 387 машин (без клапана сеть уходит в gridlock при 12 000), s10k-res в пике - до 6 030.</summary>
    internal const double DensityPerLaneKm = 7.5;

    private const int TeleportEveryTicks = 20;

    /// <summary>Фаза 2r (начало): снятие машин из тупика раз в секунду, учёт насыщения.</summary>
    internal static void OnTick(World w, CityWorld c)
    {
        if (c.Exec != TripExecution.Agents)
        {
            return;
        }

        bool saturated = Saturated(w, c);
        if (saturated)
        {
            c.Valve.EngagedTicks++;
        }

        // Снятие из тупика - только под нагрузкой (потолок клапана достигнут либо флаг gridlock): в здоровом городе одиночные долгие стоянки не трогаем (s10k-res бит-в-бит).
        if (w.Tick % TeleportEveryTicks == 0 && (saturated || w.Counters.GridlockFlag != 0))
        {
            Teleport(w, c);
        }
    }

    /// <summary>Потолок машин в сети по плотности (не ограничен пулом).</summary>
    internal static int Cap(World w, CityWorld c)
    {
        NetworkStore net = w.Net;
        if (c.ValveNetVersion != net.Version)
        {
            double len = 0;
            for (int i = 0; i < net.LaneCount; i++)
            {
                ref readonly LaneData l = ref net.Lanes[i];
                if (l.Kind != LaneKind.Connector && net.SegmentAlive(l.Segment))
                {
                    len += l.Length;
                }
            }

            c.ValveCapCache = (int)Math.Min(int.MaxValue, Math.Max(1.0, len * DensityPerLaneKm / 1000.0));
            c.ValveNetVersion = net.Version;
        }

        return c.ValveCapCache;
    }

    /// <summary>Клапан действует (потолок плотности строже пула) и машин в сети вместе с очередью выпуска не меньше потолка.</summary>
    internal static bool Saturated(World w, CityWorld c)
    {
        int cap = Cap(w, c);
        return cap < w.Vehicles.Capacity && w.Counters.ActiveVehicles + w.Trips.Count >= cap;
    }

    /// <summary>Порог безнадёжного ожидания, тики: четверть <c>wait_max_s</c> (600 с - 150 с, игровой час при <c>day_s</c> 3 600).</summary>
    private static int StaleTicks(CityWorld c) => Math.Max(20, c.Cfg.Residents.WaitMaxTicks / 4);

    /// <summary>Сеть насыщена, а заявка ждёт с <paramref name="waitBase"/> дольше порога: безнадёжна.</summary>
    internal static bool Stale(World w, CityWorld c, long waitBase) => w.Tick - waitBase >= StaleTicks(c) && Saturated(w, c);

    /// <summary>Тур с выездом по плану в слоте <paramref name="slot"/> опаздывает дольше порога, а сеть насыщена: безнадёжен.</summary>
    internal static bool TourHopeless(World w, CityWorld c, int slot) =>
        w.Tick - c.TickOfSlot(Calendar.DayOf(c, w.Tick), slot) >= StaleTicks(c) && Saturated(w, c);

    /// <summary>
    /// Машины жителей, стоящие дольше <c>2 · stuck_threshold_s</c> или в пути дольше <c>2 · wait_max_s</c>, снимаются с дороги и «прибывают» к цели: житель закрывает ногу как при прибытии (стоимость - всё время ноги, парковка -
    /// постановка на резерв). Только стоящие на обычной полосе без допуска ворот, вне смены полосы и вне meso: снятие не трогает состояние перекрёстка. Очистка - как у прибытия (фаза 8).
    /// </summary>
    private static void Teleport(World w, CityWorld c)
    {
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int limit = (int)Math.Min(ushort.MaxValue, 2L * w.Rules.StuckThresholdTicks);
        if (limit <= 0)
        {
            return;
        }

        bool lod = v.Lod.Length != 0;
        long maxAge = 2L * c.Cfg.Residents.WaitMaxTicks;   // поездка дольше двух wait_max (1 200 с, 8 игровых часов при day_s 3 600): машина кружит в заторе
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Resident[i] < 0 || (v.StillTicks[i] < limit && w.Tick - v.SpawnTick[i] < maxAge) || (lod && v.Lod[i] != 0) || v.LaneChangeTicks[i] != 0 ||
                Rules.Intersections.GateRules.IsGranted(v, i) || (uint)v.Lane[i] >= (uint)net.LaneCount || net.Lanes[v.Lane[i]].Kind == LaneKind.Connector)
            {
                continue;
            }

            c.Valve.Teleports++;
            TripLegs.OnVehicleArrived(w, i);
            w.Occupancy.UnlinkAll(i);
            if (v.Route[i] >= 0)
            {
                w.Routes.Free(new RouteHandle(v.Route[i], v.RouteGen[i]));
            }

            w.Masks.Remove(v, i);
            v.Despawn(v.HandleOf(i));
        }
    }
}

/// <summary>Диагностика клапана перегрузки (ADR-0035): монотонные счётчики с создания мира, вне хэша и сейва (после <c>Load</c> - с нуля).</summary>
internal sealed class ValveStats
{
    public long HomeLegs;       // обратных ног, отправленных домой агрегатом по дедлайну
    public long StaleLegs;      // заявок, снятых как безнадёжные при насыщении
    public long LateTours;      // туров, сорванных опозданием к выезду при насыщении
    public long Teleports;      // машин, снятых из тупика
    public long EngagedTicks;   // тиков насыщения
}
