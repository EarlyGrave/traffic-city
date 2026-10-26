using System.Runtime.InteropServices;
using Sim.Core.Util;

namespace Sim.Core.Agents;

/// <summary>Отложенная поездка, ожидающая слот машины (core-api 6.6).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PendingTrip
{
    public int FromZone;
    public int ToZone;
    public int FromLane;
    public float FromS;
    public byte Type;
    public int DestLane;     // полоса прибытия или -1 (ещё не выбрана; выбирает спавнер при генерации либо фаза 3)
    public float DestS;
    public int Route;        // слот RouteStore; действителен только при RouteGen != 0
    public int RouteGen;     // поколение маршрута; 0 = маршрут ещё не построен (поколение живого слота всегда нечётное и > 0)
    public int ResidentRef;  // 0 - поездка OD-генератора/команды/автобуса; иначе индекс жителя города + 1 (zoning-demand.md §4.1; нулевое значение структуры = «нет»)

    /// <summary>true = маршрут построен (фаза 2 ставит на дорогу только такие поездки).</summary>
    public readonly bool HasRoute => RouteGen != 0;
}

/// <summary>
/// FIFO-очередь поездок фиксированной ёмкости <c>MaxPendingTrips</c> на <see cref="RingBuffer{T}"/>.
/// Переполнение: <see cref="TryEnqueue"/> возвращает false, вызывающий делает <c>TripsDropped++</c>.
/// Спавнер читает голову через <see cref="TryPeek"/> и снимает её <see cref="TryDequeue"/> только при успешном спавне
/// (нехватка MaxVehicles - поездка остаётся в очереди).
/// </summary>
internal sealed class PendingTrips
{
    private readonly RingBuffer<PendingTrip> _ring;
    private int _unrouted;

    public PendingTrips(int capacity)
    {
        _ring = new RingBuffer<PendingTrip>(capacity);
    }

    public int Capacity => _ring.Capacity;

    // ---- сейв (Save/) ----

    internal RingBuffer<PendingTrip> Ring => _ring;

    /// <summary>Загрузка: очередь из <paramref name="count"/> поездок (голова = 0); <see cref="Unrouted"/> считается <see cref="RecountUnrouted"/> после заполнения.</summary>
    internal void RestoreCount(int count) => _ring.RestoreCount(count);

    internal void RecountUnrouted()
    {
        int n = 0;
        for (int i = 0; i < _ring.Count; i++)
        {
            if (!_ring.At(i).HasRoute)
            {
                n++;
            }
        }

        _unrouted = n;
    }

    public int Count => _ring.Count;

    public bool IsEmpty => _ring.IsEmpty;

    public bool IsFull => _ring.IsFull;

    /// <summary>Сколько поездок в очереди ещё без маршрута (фаза 3 не обходит очередь целиком, когда их нет).</summary>
    public int Unrouted => _unrouted;

    public bool TryEnqueue(in PendingTrip trip)
    {
        if (!_ring.TryEnqueue(in trip))
        {
            return false;
        }

        if (!trip.HasRoute)
        {
            _unrouted++;
        }

        return true;
    }

    public bool TryPeek(out PendingTrip trip) => _ring.TryPeek(out trip);

    public bool TryDequeue(out PendingTrip trip)
    {
        if (!_ring.TryDequeue(out trip))
        {
            return false;
        }

        if (!trip.HasRoute)
        {
            _unrouted--;
        }

        return true;
    }

    /// <summary>Заменяет поездку по порядку очереди (фаза 3 записывает построенный маршрут и точку прибытия).</summary>
    public void Set(int offset, in PendingTrip trip)
    {
        bool was = _ring.At(offset).HasRoute;
        _ring.Set(offset, in trip);
        _unrouted += (was ? 0 : -1) + (trip.HasRoute ? 0 : 1);
    }

    /// <summary>Удаляет поездку из середины очереди (нет пути); порядок остальных сохраняется.</summary>
    public void RemoveAt(int offset)
    {
        if (!_ring.At(offset).HasRoute)
        {
            _unrouted--;
        }

        _ring.RemoveAt(offset);
    }

    /// <summary>Элемент по порядку очереди (0 = голова), для хэша состояния.</summary>
    public ref readonly PendingTrip At(int offset) => ref _ring.At(offset);

    public void Clear()
    {
        _ring.Clear();
        _unrouted = 0;
    }
}
