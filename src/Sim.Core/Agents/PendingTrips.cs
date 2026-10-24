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

    public PendingTrips(int capacity)
    {
        _ring = new RingBuffer<PendingTrip>(capacity);
    }

    public int Capacity => _ring.Capacity;

    public int Count => _ring.Count;

    public bool IsEmpty => _ring.IsEmpty;

    public bool IsFull => _ring.IsFull;

    public bool TryEnqueue(in PendingTrip trip) => _ring.TryEnqueue(in trip);

    public bool TryPeek(out PendingTrip trip) => _ring.TryPeek(out trip);

    public bool TryDequeue(out PendingTrip trip) => _ring.TryDequeue(out trip);

    /// <summary>Элемент по порядку очереди (0 = голова), для хэша состояния.</summary>
    public ref readonly PendingTrip At(int offset) => ref _ring.At(offset);

    public void Clear() => _ring.Clear();
}
