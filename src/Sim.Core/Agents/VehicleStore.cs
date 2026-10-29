using System;
using System.Diagnostics;
using Sim.Core.Api;
using Sim.Core.Rules;
using Sim.Core.Util;

namespace Sim.Core.Agents;

/// <summary>
/// SoA-хранилище машин (core-api 6.3). Все массивы длиной MaxVehicles, индекс = <see cref="VehicleHandle.Index"/>.
/// Слоты выдаёт <see cref="HandlePool"/>; свежий слот инициализирован значениями по умолчанию
/// (ссылки = -1, остальное = 0). Порядок на полосах ведёт <see cref="LaneOccupancy"/>.
/// </summary>
internal sealed class VehicleStore
{
    public readonly HandlePool Pool;

    // --- горячие, читаются IDM каждый тик ---
    public readonly int[] Lane;
    public readonly float[] S;
    public readonly float[] Speed;
    public readonly float[] Accel;
    public readonly float[] Length;
    public readonly byte[] Type;
    public readonly float[] DesiredSpeed;
    public readonly float[] ClampLeadGap;  // зазор до лидера для клэмпа I6: пишет фаза 5, читает фаза 6 (ADR-0016, D2); не в хэше
    public readonly byte[] ClampFlags;     // бит0 - лидер/препятствие есть, бит1 - виртуальное (без MinPhysicalGap)
    public readonly float[] ClampGateGap;  // сырой зазор ворот (без ограничения видимости); Idm.FreeGap - нет

    // --- порядок на полосе (intrusive double-linked list, 6.4) ---
    public readonly int[] Ahead;
    public readonly int[] Behind;
    public readonly int[] GhostLane;
    public readonly int[] GhostAhead;
    public readonly int[] GhostBehind;

    // --- смена полосы ---
    public readonly sbyte[] LaneChangeDir;
    public readonly ushort[] LaneChangeTicks;
    public readonly ushort[] NextMobilTick;

    // --- маршрут ---
    public readonly int[] Route;
    public readonly int[] RouteGen;
    public readonly int[] RouteCursor;
    public readonly int[] DestZone;
    public readonly int[] DestLane;
    public readonly float[] DestS;
    public readonly int[] NextConn;        // кэш NextConnector: connector ближайшей развилки по маршруту (-1 = не выбран); пересчитывает конец фазы 7
    public readonly ulong[] RouteDirtyBits; // бит i = у машины изменился вход AssignConnectors (полоса, маршрут, окно, флаги); вне хэша (ADR-0016, D7; ADR-0019, A3: обход по маске)

    // --- ворота перекрёстка (intersections.md §4, §5; биты GateState - Rules.Intersections.GateRules) ---
    public readonly byte[] GateState;      // бит0 Granted (допуск выдан), бит1 StoppedAtLine
    public readonly int[] GrantConnector;  // connector допуска или -1
    public readonly uint[] Ticket;         // билет узла (порядок внутри перекрёстка), валиден при Granted
    public readonly ushort[] WaitTicks;    // тиков подряд кандидатом у ворот без допуска

    // --- геометрия/рендер ---
    public readonly int[] PointHint;
    public readonly float[] Lateral;

    // --- метрики/состояние ---
    public readonly long[] SpawnTick;
    public readonly ushort[] StillTicks;
    public readonly byte[] Flags;
    public readonly int[] BusRoute;
    public readonly int[] BusNextStop;
    public readonly ushort[] DwellTicks;
    public readonly ushort[] Passengers;
    public readonly int[] Resident;        // 3.2 (zoning-demand.md §4.1): житель-владелец поездки или -1 (OD-генератор, автобусы); пишет Z2
    public readonly int[] ParkFac;         // parking.md §2.3 (K1): резерв машины жителя в пути (объект стоянки) или -1 (OD, автобусы, машины без режима)
    public readonly int[] ParkFacGen;
    public readonly long[] ParkSearchTick; // начало поиска после потери резерва (§7, K3) или -1

    // --- уровни детализации (ADR-0013, sim-lod.md §1): выделены только при Lod.Mode != Off, иначе пустые (длина 0) ---
    public readonly byte[] Lod;            // бит0 meso, бит1 MesoFresh (LodBits)
    public readonly float[] MesoAllow;     // путь, который meso-машина может проехать до следующего пересчёта, м
    public readonly float[] MesoNext;      // скорость, назначенная фазой 5 meso-машине на этот тик (применяет фаза 6; < 0 - нет); в границах тика, вне хэша

    /// <summary>Q-meso (<c>Lod/QueueMeso.cs</c>): бит i = машина очереди полосы, фазы 5-6 её пропускают. Пустой - модели очереди нет (обход по всем слотам).</summary>
    public ulong[] QueueBits = Array.Empty<ulong>();

    /// <summary>
    /// Бит доступа класса по номеру типа (<c>World.TypeAccess</c>, 256 элементов; vehicle-classes.md §2.1): ссылка на ту же таблицу, чтобы правила полос
    /// читали права машины через <see cref="Access"/>, не зная <c>World</c>. Без мира (тесты хранилища) - все права (<see cref="VehicleAccess.All"/>).
    /// </summary>
    public byte[] TypeAccess = AllAccess();

    /// <summary>Права машины <paramref name="i"/>: бит класса типа | <c>Emergency</c> (0x20), пока стоит <see cref="VehicleFlags.OnCall"/> (128 &gt;&gt; 2 = 0x20, без ветви).</summary>
    public byte Access(int i) => (byte)(TypeAccess[Type[i]] | ((Flags[i] & (byte)VehicleFlags.OnCall) >> 2));

    private static byte[] AllAccess()
    {
        var t = new byte[256];
        Array.Fill(t, VehicleAccess.All);
        return t;
    }

    public VehicleStore(int maxVehicles, bool lod = false)
    {
        Debug.Assert(maxVehicles >= 0);
        Pool = new HandlePool(maxVehicles);
        Lane = new int[maxVehicles];
        S = new float[maxVehicles];
        Speed = new float[maxVehicles];
        Accel = new float[maxVehicles];
        Length = new float[maxVehicles];
        Type = new byte[maxVehicles];
        DesiredSpeed = new float[maxVehicles];
        ClampLeadGap = new float[maxVehicles];
        ClampFlags = new byte[maxVehicles];
        ClampGateGap = new float[maxVehicles];
        Array.Fill(ClampGateGap, Idm.FreeGap);
        Ahead = new int[maxVehicles];
        Behind = new int[maxVehicles];
        GhostLane = new int[maxVehicles];
        GhostAhead = new int[maxVehicles];
        GhostBehind = new int[maxVehicles];
        LaneChangeDir = new sbyte[maxVehicles];
        LaneChangeTicks = new ushort[maxVehicles];
        NextMobilTick = new ushort[maxVehicles];
        Route = new int[maxVehicles];
        RouteGen = new int[maxVehicles];
        RouteCursor = new int[maxVehicles];
        DestZone = new int[maxVehicles];
        DestLane = new int[maxVehicles];
        DestS = new float[maxVehicles];
        NextConn = new int[maxVehicles];
        RouteDirtyBits = new ulong[(maxVehicles + 63) >> 6];
        Array.Fill(RouteDirtyBits, ulong.MaxValue);
        GateState = new byte[maxVehicles];
        GrantConnector = new int[maxVehicles];
        Ticket = new uint[maxVehicles];
        WaitTicks = new ushort[maxVehicles];
        PointHint = new int[maxVehicles];
        Lateral = new float[maxVehicles];
        SpawnTick = new long[maxVehicles];
        StillTicks = new ushort[maxVehicles];
        Flags = new byte[maxVehicles];
        BusRoute = new int[maxVehicles];
        BusNextStop = new int[maxVehicles];
        DwellTicks = new ushort[maxVehicles];
        Passengers = new ushort[maxVehicles];
        Resident = new int[maxVehicles];
        ParkFac = new int[maxVehicles];
        ParkFacGen = new int[maxVehicles];
        ParkSearchTick = new long[maxVehicles];
        Lod = lod ? new byte[maxVehicles] : Array.Empty<byte>();
        MesoAllow = lod ? new float[maxVehicles] : Array.Empty<float>();
        MesoNext = lod ? new float[maxVehicles] : Array.Empty<float>();
        for (int i = 0; i < maxVehicles; i++)
        {
            ResetSlot(i);
        }
    }

    public int Capacity => Pool.Capacity;

    public int AliveCount => Pool.AliveCount;

    /// <summary>Верхняя граница для обходов «по всем слотам».</summary>
    public int HighWater => Pool.HighWater;

    /// <summary>false = ёмкость MaxVehicles исчерпана (вызывающий инкрементирует счётчик). Слот не привязан ни к одной полосе.</summary>
    public bool TrySpawn(out VehicleHandle handle)
    {
        if (!Pool.TryAllocate(out int index, out int generation))
        {
            handle = VehicleHandle.Null;
            return false;
        }

        ResetSlot(index);
        handle = new VehicleHandle(index, generation);
        return true;
    }

    /// <summary>
    /// Освобождает слот (generation++). Машина должна быть уже снята со всех списков
    /// (<see cref="LaneOccupancy.UnlinkAll"/>). false = handle невалиден (no-op).
    /// </summary>
    public bool Despawn(VehicleHandle handle)
    {
        if (!IsAlive(handle))
        {
            return false;
        }

        Debug.Assert(Lane[handle.Index] == -1 && GhostLane[handle.Index] == -1, "despawn: машина ещё в списке полосы");
        ResetSlot(handle.Index);
        Pool.Free(handle.Index);
        return true;
    }

    public bool IsAlive(VehicleHandle handle) => !handle.IsNull && Pool.IsAlive(handle.Index, handle.Generation);

    /// <summary>Handle живого слота (для снапшотов/событий); слот должен быть жив.</summary>
    public VehicleHandle HandleOf(int index)
    {
        Debug.Assert(Pool.IsAlive(index));
        return new VehicleHandle(index, Pool.GenerationOf(index));
    }

    /// <summary>Помечает все машины для пересчёта в <c>AssignConnectors</c> (изменилась сеть/узлы; ADR-0016, D7).</summary>
    public void MarkAllRouteDirty() => Array.Fill(RouteDirtyBits, ulong.MaxValue);

    /// <summary>Помечает машину для пересчёта в <c>AssignConnectors</c>.</summary>
    public void MarkRouteDirty(int i) => RouteDirtyBits[i >> 6] |= 1UL << (i & 63);

    public bool IsRouteDirty(int i) => ((RouteDirtyBits[i >> 6] >> (i & 63)) & 1UL) != 0UL;

    public void ClearRouteDirty(int i) => RouteDirtyBits[i >> 6] &= ~(1UL << (i & 63));

    private void ResetSlot(int i)
    {
        Lane[i] = -1;
        S[i] = 0f;
        Speed[i] = 0f;
        Accel[i] = 0f;
        Length[i] = 0f;
        Type[i] = 0;
        DesiredSpeed[i] = 0f;
        ClampLeadGap[i] = 0f;
        ClampFlags[i] = 0;
        ClampGateGap[i] = Idm.FreeGap;
        Ahead[i] = -1;
        Behind[i] = -1;
        GhostLane[i] = -1;
        GhostAhead[i] = -1;
        GhostBehind[i] = -1;
        LaneChangeDir[i] = 0;
        LaneChangeTicks[i] = 0;
        NextMobilTick[i] = 0;
        Route[i] = -1;
        RouteGen[i] = 0;
        RouteCursor[i] = 0;
        DestZone[i] = -1;
        DestLane[i] = -1;
        DestS[i] = 0f;
        NextConn[i] = -1;
        MarkRouteDirty(i);
        GateState[i] = 0;
        GrantConnector[i] = -1;
        Ticket[i] = 0;
        WaitTicks[i] = 0;
        PointHint[i] = 0;
        Lateral[i] = 0f;
        SpawnTick[i] = 0;
        StillTicks[i] = 0;
        Flags[i] = 0;
        BusRoute[i] = -1;
        BusNextStop[i] = 0;
        DwellTicks[i] = 0;
        Passengers[i] = 0;
        Resident[i] = -1;
        ParkFac[i] = -1;
        ParkFacGen[i] = 0;
        ParkSearchTick[i] = -1;
        if (Lod.Length != 0)
        {
            Lod[i] = 0;
            MesoAllow[i] = 0f;
            MesoNext[i] = -1f;
        }
    }
}
