using System.Runtime.InteropServices;
using Sim.Core.Network;

namespace Sim.Core.Api;

public enum CommandKind : ushort
{
    None = 0,
    BuildRoad = 1,
    SpawnTrip = 2,
    SetSignalPlan = 3,
    CreateBusRoute = 4,
    AppendBusStop = 5,
    SpawnBus = 6,
    SelectVehicle = 7,
    ClearSelection = 8,
    SetTargetVehicleCount = 9,
    SetNodeControl = 10,
    // 11 зарезервирован под SetLodFocus (core-api §4, ADR-0013): в ядре пока не реализован
    SetPedTarget = 12,   // 3.1, ADR-0020: цель активных пешеходов (как SetTargetVehicleCount); без секции peds отклоняется
    // 13..22 свободны (резерв). Инструменты игрока (player-tools.md §1, ADR-0023) нумеруются с 25.
    PaintZone = 23,     // 3.2, ADR-0011: зона на участках в прямоугольнике (zoning-demand.md §14); Z1 - заглушка Zoning.Apply (NotSupported), тело - Z6
    SetTripExecution = 24, // 3.2: micro/aggregate на границе суток (zoning-demand.md §9.2); Z1 - заглушка AggregateExec.ApplySwitch (NotSupported), тело - Z5

    // Инструменты игрока (4.3, T1): типы и номера закреплены, поведение - в T3..T9; до тех пор диспетчер отвечает NotSupported.
    RemoveRoad = 25,
    UpgradeRoad = 26,
    SplitRoad = 27,
    SetArmControl = 28,
    SetTurnBan = 29,
    SetLaneFlags = 30,
    InsertBusStop = 31,
    RemoveBusStop = 32,
    RemoveBusRoute = 33,
    SetBusRouteParams = 34,
    MergeAtNode = 35,

    // Экономика города (4.2, city-economy.md §11, E1). Номер 25 из спеки занят инструментами (T1) - взят следующий свободный.
    SetBudgetPolicy = 36,   // политика бюджета: ожидающая, действует с границы суток; без режима city_budget - см. BudgetFlows.SetPolicy
}

public enum SignalMode : int
{
    Off = 0,
    Fixed = 1,
    FlashYellow = 2,
    Adaptive = 3,   // 2.5, ADR-0018: актуированный режим со стрелкой по спросу (intersections.md §6.5)
}

/// <summary>Биты <see cref="SetSignalPlanCmd.Flags"/> (2.5).</summary>
public static class SignalPlanFlags
{
    /// <summary>Adaptive: стрелка по спросу; Fixed: стрелка каждый цикл (длительность <c>ArrowMaxTicks</c>).</summary>
    public const byte Arrows = 1;
}

public enum BusStopKind : int
{
    Curb = 0,
    Bay = 1,
}

/// <summary>Биты <see cref="Command.Flags"/> (city-economy.md §8.3).</summary>
public static class CommandFlags
{
    /// <summary>Обратная команда (ставит <c>InverseBuilder</c>): при живой записи undo-кредита по цели - цена по кредиту (возврат 100%), иначе обычная.</summary>
    public const ushort Inverse = 1;
}

/// <summary>Плоский tagged-union фиксированного размера 64 байта (core-api §4.1). Payload — с offset 8.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct Command
{
    [FieldOffset(0)]
    public CommandKind Kind;

    /// <summary>Биты <see cref="CommandFlags"/> (старшие два байта прежнего 32-битного <c>Kind</c>; нули = как раньше, раскладка и байты журнала не меняются).</summary>
    [FieldOffset(2)]
    public ushort Flags;

    /// <summary>Произвольная метка клиента, эхо в <see cref="SimEvent.Tag"/>.</summary>
    [FieldOffset(4)]
    public uint Tag;

    [FieldOffset(8)]
    public BuildRoadCmd BuildRoad;

    [FieldOffset(8)]
    public SpawnTripCmd SpawnTrip;

    [FieldOffset(8)]
    public SetSignalPlanCmd SetSignalPlan;

    [FieldOffset(8)]
    public CreateBusRouteCmd CreateBusRoute;

    [FieldOffset(8)]
    public AppendBusStopCmd AppendBusStop;

    [FieldOffset(8)]
    public SpawnBusCmd SpawnBus;

    [FieldOffset(8)]
    public SelectVehicleCmd SelectVehicle;

    [FieldOffset(8)]
    public SetTargetVehicleCountCmd SetTargetVehicleCount;

    [FieldOffset(8)]
    public SetNodeControlCmd SetNodeControl;

    [FieldOffset(8)]
    public SetPedTargetCmd SetPedTarget;

    [FieldOffset(8)]
    public PaintZoneCmd PaintZone;

    [FieldOffset(8)]
    public SetTripExecutionCmd SetTripExecution;

    [FieldOffset(8)]
    public RemoveRoadCmd RemoveRoad;

    [FieldOffset(8)]
    public UpgradeRoadCmd UpgradeRoad;

    [FieldOffset(8)]
    public SplitRoadCmd SplitRoad;

    [FieldOffset(8)]
    public SetArmControlCmd SetArmControl;

    [FieldOffset(8)]
    public SetTurnBanCmd SetTurnBan;

    [FieldOffset(8)]
    public SetLaneFlagsCmd SetLaneFlags;

    [FieldOffset(8)]
    public InsertBusStopCmd InsertBusStop;

    [FieldOffset(8)]
    public RemoveBusStopCmd RemoveBusStop;

    [FieldOffset(8)]
    public RemoveBusRouteCmd RemoveBusRoute;

    [FieldOffset(8)]
    public SetBusRouteParamsCmd SetBusRouteParams;

    [FieldOffset(8)]
    public MergeAtNodeCmd MergeAtNode;

    [FieldOffset(8)]
    public SetBudgetPolicyCmd SetBudgetPolicy;

    // Фабрики называются Make*: имя Command.BuildRoad занято полем-payload (core-api §4.1).
    public static Command MakeBuildRoad(in BuildRoadCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.BuildRoad;
        c.Tag = tag;
        c.BuildRoad = payload;
        return c;
    }

    public static Command MakeSpawnTrip(in SpawnTripCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SpawnTrip;
        c.Tag = tag;
        c.SpawnTrip = payload;
        return c;
    }

    public static Command MakeSetSignalPlan(in SetSignalPlanCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetSignalPlan;
        c.Tag = tag;
        c.SetSignalPlan = payload;
        return c;
    }

    public static Command MakeCreateBusRoute(in CreateBusRouteCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.CreateBusRoute;
        c.Tag = tag;
        c.CreateBusRoute = payload;
        return c;
    }

    public static Command MakeAppendBusStop(in AppendBusStopCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.AppendBusStop;
        c.Tag = tag;
        c.AppendBusStop = payload;
        return c;
    }

    public static Command MakeSpawnBus(int route, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SpawnBus;
        c.Tag = tag;
        c.SpawnBus = new SpawnBusCmd { Route = route };
        return c;
    }

    public static Command MakeSelectVehicle(VehicleHandle h, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SelectVehicle;
        c.Tag = tag;
        c.SelectVehicle = new SelectVehicleCmd { Index = h.Index, Generation = h.Generation };
        return c;
    }

    public static Command MakeClearSelection(uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.ClearSelection;
        c.Tag = tag;
        return c;
    }

    public static Command MakeSetTargetVehicleCount(int count, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetTargetVehicleCount;
        c.Tag = tag;
        c.SetTargetVehicleCount = new SetTargetVehicleCountCmd { Count = count };
        return c;
    }

    public static Command MakeSetPedTarget(int count, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetPedTarget;
        c.Tag = tag;
        c.SetPedTarget = new SetPedTargetCmd { Count = count };
        return c;
    }

    public static Command MakePaintZone(in PaintZoneCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.PaintZone;
        c.Tag = tag;
        c.PaintZone = payload;
        return c;
    }

    public static Command MakeSetTripExecution(TripExecution mode, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetTripExecution;
        c.Tag = tag;
        c.SetTripExecution = new SetTripExecutionCmd { Mode = (byte)mode };
        return c;
    }

    public static Command MakeSetBudgetPolicy(in BudgetPolicy policy, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetBudgetPolicy;
        c.Tag = tag;
        c.SetBudgetPolicy = new SetBudgetPolicyCmd { Policy = policy };
        return c;
    }

    public static Command MakeRemoveRoad(in RemoveRoadCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.RemoveRoad;
        c.Tag = tag;
        c.RemoveRoad = payload;
        return c;
    }

    public static Command MakeUpgradeRoad(in UpgradeRoadCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.UpgradeRoad;
        c.Tag = tag;
        c.UpgradeRoad = payload;
        return c;
    }

    public static Command MakeSplitRoad(in SplitRoadCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SplitRoad;
        c.Tag = tag;
        c.SplitRoad = payload;
        return c;
    }

    public static Command MakeSetArmControl(in SetArmControlCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetArmControl;
        c.Tag = tag;
        c.SetArmControl = payload;
        return c;
    }

    public static Command MakeSetTurnBan(in SetTurnBanCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetTurnBan;
        c.Tag = tag;
        c.SetTurnBan = payload;
        return c;
    }

    public static Command MakeSetLaneFlags(in SetLaneFlagsCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetLaneFlags;
        c.Tag = tag;
        c.SetLaneFlags = payload;
        return c;
    }

    public static Command MakeInsertBusStop(in InsertBusStopCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.InsertBusStop;
        c.Tag = tag;
        c.InsertBusStop = payload;
        return c;
    }

    public static Command MakeRemoveBusStop(in RemoveBusStopCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.RemoveBusStop;
        c.Tag = tag;
        c.RemoveBusStop = payload;
        return c;
    }

    public static Command MakeRemoveBusRoute(in RemoveBusRouteCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.RemoveBusRoute;
        c.Tag = tag;
        c.RemoveBusRoute = payload;
        return c;
    }

    public static Command MakeSetBusRouteParams(in SetBusRouteParamsCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetBusRouteParams;
        c.Tag = tag;
        c.SetBusRouteParams = payload;
        return c;
    }

    public static Command MakeMergeAtNode(in MergeAtNodeCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.MergeAtNode;
        c.Tag = tag;
        c.MergeAtNode = payload;
        return c;
    }

    /// <summary>С полным payload: <see cref="SetNodeControlCmd.NodeGen"/> задаёт поколение узла (4.3, T2: чужое поколение или мёртвый узел - <c>InvalidHandle</c>).</summary>
    public static Command MakeSetNodeControl(in SetNodeControlCmd payload, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetNodeControl;
        c.Tag = tag;
        c.SetNodeControl = payload;
        return c;
    }

    public static Command MakeSetNodeControl(int node, NodeControl control, uint tag = 0)
    {
        var c = default(Command);
        c.Kind = CommandKind.SetNodeControl;
        c.Tag = tag;
        c.SetNodeControl = new SetNodeControlCmd { Node = node, Control = control };
        return c;
    }
}

/// <summary>
/// Раскладка явная (56 Б): <see cref="Profile"/> лежит на младших двух байтах <see cref="Cz"/> - контрольная точка Безье нужна только при <c>BuildRoadFlags.Curve</c>
/// (T5, пока <c>NotSupported</c>); когда кривые появятся вместе с профилями, поле придётся переложить (в payload 56 Б свободного места нет).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 56)]
public struct BuildRoadCmd
{
    [FieldOffset(0)]
    public float Ax;                  // концы оси, м; концы прилипают к узлу в радиусе SnapRadius (5 м)

    [FieldOffset(4)]
    public float Az;

    [FieldOffset(8)]
    public float Bx;

    [FieldOffset(12)]
    public float Bz;

    [FieldOffset(16)]
    public float SpeedLimit;          // м/с

    [FieldOffset(20)]
    public byte LanesAB;              // полос A→B (0 допустимо для одностороннего)

    [FieldOffset(21)]
    public byte LanesBA;              // полос B→A

    [FieldOffset(22)]
    public RoadKind Kind;             // Street = 0, Arterial = 1

    [FieldOffset(23)]
    public byte Flags;                // BuildRoadFlags: бит0 NoConnectors (тесты), бит1 AutoSplit, бит2 Curve (T5; пока NotSupported), бит3 Mirrored (RP7)

    // player-tools.md §2 (24 -> 56 Б). Нулевая инициализация = прежнее поведение.
    [FieldOffset(24)]
    public float Cx;                  // контрольная точка квадратичной Безье (Curve), иначе 0

    [FieldOffset(28)]
    public float Cz;

    /// <summary>
    /// Профиль дороги (ADR-0030, RP7): индекс в каталоге + 1, 0 - без профиля (прежнее поведение). Полосы и тип обязаны совпасть с профилем (с учётом <c>Mirrored</c>), иначе <c>InvalidArgument</c>.
    /// Делит байты с <see cref="Cz"/> (см. описание типа).
    /// </summary>
    [FieldOffset(28)]
    public ushort Profile;

    [FieldOffset(32)]
    public EntityRef RestoreOf;       // сегмент, который команда восстанавливает (undo); Generation == 0 - не задан

    [FieldOffset(40)]
    public EntityRef RestoreNodeA;    // узлы концов, если их тоже пересоздать; Generation == 0 - не задан

    [FieldOffset(48)]
    public EntityRef RestoreNodeB;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SpawnTripCmd
{
    public int FromZone;              // индекс зоны или -1, если задан FromLane
    public int ToZone;                // индекс зоны (обязателен)
    public int FromLane;              // индекс полосы или -1
    public float FromS;               // м вдоль FromLane
    public int VehicleType;           // индекс в ScenarioDesc.VehicleTypes
    public int Count;                 // сколько машин поставить в очередь поездок (1..N)
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct SetSignalPlanCmd
{
    public int Node;
    public SignalMode Mode;
    public ushort OffsetTicks;        // сдвиг цикла
    public byte PhaseCount;           // 1..8
    public byte Reserved;
    public ushort Green0, Green1, Green2, Green3, Green4, Green5, Green6, Green7; // зелёный по фазам, тики
    public ushort YellowTicks, AllRedTicks;
    // 2.5 (ADR-0018, intersections.md §6.5): 0 -> дефолт RulesDesc. Размер 32 -> 46 Б (payload <= 56 Б).
    public ushort MinGreenTicks, GapTicks, ArrowTriggerTicks, ArrowMinTicks, ArrowMaxTicks;
    public ushort CycleTicks;         // Adaptive: > 0 - координация (S3; пока команда с CycleTicks > 0 отклоняется)
    public byte Flags;                // SignalPlanFlags: бит0 Arrows
    public byte Reserved2;
    // 4.3 (T1, player-tools.md §2): поколение узла, 0 - без проверки. Размер 46 -> 50 Б. Проверка поколения - T2 (HandlePool).
    public int NodeGen;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CreateBusRouteCmd
{
    public int HeadwayTicks;
    public int VehicleType;
    public int Flags;
    public EntityRef RestoreOf;       // 4.3 (T1): маршрут, который команда восстанавливает (undo); Generation == 0 - не задан. 12 -> 20 Б
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AppendBusStopCmd
{
    public int Route;
    public int Lane;
    public float S;
    public BusStopKind Kind;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SpawnBusCmd
{
    public int Route;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SelectVehicleCmd
{
    public int Index;
    public int Generation;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetTargetVehicleCountCmd
{
    public int Count;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetNodeControlCmd
{
    public int Node;
    public NodeControl Control;
    public int NodeGen;               // 4.3 (T1): поколение узла, 0 - без проверки; мёртвый слот или чужое поколение -> InvalidHandle (T2). 8 -> 12 Б
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetPedTargetCmd
{
    public int Count;
}

/// <summary>Прямоугольник зонирования (player-tools.md §2): участок попадает под команду, если его центр внутри. <c>ZoneKind</c> - <see cref="LotZone"/> (0 снимает зону), <c>Density</c> 0/1.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PaintZoneCmd
{
    public float X0, Z0, X1, Z1;      // углы прямоугольника, м (упорядочиваются)
    public byte ZoneKind, Density;
    public byte R0;                   // PaintZoneFlags (0 у прямой команды)
    public byte R1;
}

/// <summary>Биты <see cref="PaintZoneCmd.R0"/>.</summary>
public static class PaintZoneFlags
{
    /// <summary>Обратная запись: участкам не ставится <c>Painted</c> (прежде их зонировал не игрок).</summary>
    public const byte RestoreUnpainted = 1;
}

/// <summary>Режим исполнения ног (<see cref="TripExecution"/>); применяется на границе суток.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetTripExecutionCmd
{
    public byte Mode;
}

/// <summary>Политика бюджета (city-economy.md §2.2, §11): 16 байт.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct SetBudgetPolicyCmd
{
    public BudgetPolicy Policy;
}
