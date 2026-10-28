using System.Runtime.InteropServices;

namespace Sim.Core.Api;

/// <summary>Состояние пешехода (pedestrians.md §4.1). Числа фиксированы: они лежат в сейве и в снапшоте.</summary>
public enum PedState : byte
{
    PendingPath = 0,   // ждёт путь в очереди PedPathQueue
    Walking = 1,       // идёт по тротуару
    AtCrossing = 2,    // стоит у бордюра в очереди перехода
    Crossing = 3,      // идёт по переходу
    AtStop = 4,        // стоит в очереди на остановке (P4)
    OnBus = 5,         // едет пассажиром (P4)
}

/// <summary>Нога поездки пешехода: куда он идёт сейчас (pedestrians.md §3, §7).</summary>
public enum PedLeg : byte
{
    Direct = 0,        // сразу к назначению
    ToStop = 1,        // к остановке посадки (конец ноги - <c>Stops.OnArrive</c>, P4)
    FromStop = 2,      // от остановки высадки к назначению
}

/// <summary>Биты <see cref="PedestrianRenderData.Flags"/>.</summary>
public static class PedRenderFlags
{
    public const byte Active = 1;
    public const byte Selected = 2;
    public const byte Passenger = 4;   // нога к остановке или от неё
}

/// <summary>
/// Снимок одного пешехода для клиента (24 байта, pedestrians.md §10.1). Индекс = слот пула пешеходов в этом же массиве;
/// пешеход вне <c>RenderView</c>, <c>PendingPath</c> или <c>OnBus</c> пишется с <c>Flags = 0</c> (остальные поля не читать).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
public struct PedestrianRenderData
{
    public float X, Y, Z;        // 0..11  м
    public float Heading;        // 12..15 рад
    public int Index;            // 16..19 слот
    public ushort Generation;    // 20..21 младшие 16 бит поколения
    public byte State;           // 22     PedState
    public byte Flags;           // 23     PedRenderFlags
}

/// <summary>Публичная копия подмножества полей одного пешехода (инспектор, HUD).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PedInfo
{
    public int Resident;         // житель-владелец
    public PedState State;
    public PedLeg Leg;
    public ushort Reserved;
    public int Edge;             // текущее ребро графа пешеходов (без битов перехода и направления)
    public int WaitTicks;        // сколько ждёт (AtCrossing, AtStop, PendingPath), иначе 0
    public int TripTicks;        // от старта поездки
    public int StepsLeft;        // шагов пути впереди
    public float X, Z;           // позиция (как в снапшоте)
}
