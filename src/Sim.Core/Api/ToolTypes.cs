using System;
using System.Runtime.InteropServices;
using Sim.Core.Network;

namespace Sim.Core.Api;

// Типы инструментов игрока (player-tools.md §2, §7, §8; ADR-0023). T1: только типы и коды, поведения нет.

/// <summary>Вид сущности, на которую указывает <see cref="EntityRef"/> (для <see cref="PreviewIssue"/>, <see cref="RemapEntry"/>).</summary>
public enum EntityKind : byte
{
    None = 0,
    Node = 1,
    Segment = 2,
    BusStop = 3,
    BusRoute = 4,
    Zone = 5,            // только для PreviewIssue.Obj и EventPlace: индекс зоны (без поколения)
    Building = 6,        // EventPlace (EV1) и жалобы PG4: индекс и поколение здания города
}

/// <summary>
/// Ссылка на сущность: индекс + поколение (8 Б). <c>Generation == 0</c> - без проверки поколения (только генератор и тесты).
/// В полях <c>Restore*</c> <c>Generation == 0</c> означает «не задано» (настоящий handle мёртвого слота всегда имеет поколение &gt;= 1),
/// поэтому нулевая инициализация payload безопасна; <see cref="None"/> = {-1, 0}.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EntityRef
{
    public int Index, Generation;

    public EntityRef(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    public static EntityRef None => new(-1, 0);

    /// <summary>Для полей <c>Restore*</c>: ссылка не задана.</summary>
    public readonly bool IsUnset => Generation == 0;
}

/// <summary>Биты <see cref="BuildRoadCmd.Flags"/> (player-tools.md §2).</summary>
public static class BuildRoadFlags
{
    /// <summary>Не создавать connector'ы автоматически (тесты).</summary>
    public const byte NoConnectors = 1;

    /// <summary>Разрезать сегмент, если конец попал на его ось (§4.4).</summary>
    public const byte AutoSplit = 2;

    /// <summary>Квадратичная Безье: контрольная точка <see cref="BuildRoadCmd.SetControl"/> (T5).</summary>
    public const byte Curve = 4;

    /// <summary>Сегмент построен «против» направления рисования профиля (ADR-0030, RP7): <see cref="BuildRoadCmd.Profile"/> обязателен.</summary>
    public const byte Mirrored = 8;

    /// <summary>Биты, которые ядро понимает (остальные дают <see cref="RejectReason.NotSupported"/>; T5: AutoSplit и Curve).</summary>
    public const byte Supported = NoConnectors | AutoSplit | Curve | Mirrored;
}

/// <summary>Бит0 <see cref="RemoveRoadCmd.Flags"/>: не удалять осиротевшие узлы.</summary>
public static class RemoveRoadFlags
{
    public const int KeepNodes = 1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RemoveRoadCmd
{
    public EntityRef Seg;
    public int Flags;
}

/// <summary>Биты <see cref="UpgradeRoadCmd.Flags"/>.</summary>
public static class UpgradeRoadFlags
{
    /// <summary>Тип дороги не менять (<see cref="UpgradeRoadCmd.Kind"/> игнорируется).</summary>
    public const byte KeepKind = 1;

    /// <summary>Новый профиль применяется «против» направления рисования (ADR-0030, RP7); только вместе с <see cref="UpgradeRoadCmd.Profile"/> != 0.</summary>
    public const byte Mirrored = 2;

    /// <summary>
    /// Снять профиль (T5): сегмент становится «без профиля» (полосы стандартной ширины), полосы и тип - из команды. Только с <see cref="UpgradeRoadCmd.Profile"/> == 0, без <see cref="Mirrored"/>.
    /// Пишет обратная запись ядра для правки «без профиля -> профиль».
    /// </summary>
    public const byte NoProfile = 4;

    public const byte Supported = KeepKind | Mirrored | NoProfile;
}

/// <summary>
/// «Не менять»: <c>SpeedLimit == 0</c>, <c>LanesAB/BA == 255</c>, <c>Flags</c> бит0 KeepKind, <c>Profile == 0</c>. Профиль (RP7): индекс в каталоге + 1; полосы и тип тогда выводятся из профиля
/// (заданные явно обязаны совпасть); сегмент с профилем меняет полосы и тип только профилем.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct UpgradeRoadCmd
{
    public EntityRef Seg;
    public float SpeedLimit;
    public byte LanesAB, LanesBA;
    public RoadKind Kind;
    public byte Flags;
    public ushort Profile;
    public ushort R;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SplitRoadCmd
{
    public EntityRef Seg;
    public float S;                   // по полной оси от узла NodeA, м
    public EntityRef RestoreNode;     // узел, который команда восстанавливает (undo слияния), или {-1,0}
    public EntityRef RestoreSeg1;     // T5: половины, которые команда восстанавливает (обратная к MergeAtNode): A -> узел и узел -> B, или {-1,0}
    public EntityRef RestoreSeg2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MergeAtNodeCmd
{
    public EntityRef Node;
    public EntityRef RestoreSeg;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetArmControlCmd
{
    public EntityRef Node;
    public byte ArmSlot;              // номер рукава по часовой (intersections.md §1)
    public byte Control;
    public ushort R;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetTurnBanCmd
{
    public EntityRef Node;
    public byte FromArm, ToArm, Banned, R;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetLaneFlagsCmd
{
    public EntityRef Seg;
    public byte Dir, LaneNo, Flags, Mask;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct InsertBusStopCmd
{
    public EntityRef Route;
    public int Position;              // -1 = в конец
    public EntityRef Seg;
    public byte Dir, LaneNo;
    public byte Kind, R;
    public float S;
    public EntityRef RestoreOf;       // остановка, которую команда восстанавливает (undo)
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RemoveBusStopCmd
{
    public EntityRef Stop;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RemoveBusRouteCmd
{
    public EntityRef Route;
}

/// <summary>
/// «Не менять»: <c>HeadwayTicks == 0</c>, <c>VehicleType == -1</c>, <c>Flags == 0</c>. T9: <see cref="BusRouteFlags"/> в <c>Flags</c> - приостановить / возобновить линию (16 -> 20 Б).
/// Число автобусов на линии задаётся интервалом: выпуск раз в <c>HeadwayTicks</c> с первой остановки (содержание казны считает <c>ceil(цикл / интервал)</c>, city-economy.md §6.1).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetBusRouteParamsCmd
{
    public EntityRef Route;
    public int HeadwayTicks;
    public int VehicleType;
    public int Flags;
}

/// <summary>
/// Биты <c>CreateBusRouteCmd.Flags</c> и <c>SetBusRouteParamsCmd.Flags</c> (T9). Приостановленная линия (<c>Suspended</c>) хранит интервал со знаком минус
/// (<c>BusRouteData.HeadwayTicks &lt; 0</c>, модуль - интервал для возобновления): новых автобусов не выпускает, автобусы на линии доезжают рейс, пешеходы её не выбирают.
/// </summary>
public static class BusRouteFlags
{
    /// <summary>Создать приостановленной / приостановить.</summary>
    public const int Suspend = 1;

    /// <summary>Возобновить (только <c>SetBusRouteParams</c>; вместе с <see cref="Suspend"/> - <c>InvalidArgument</c>).</summary>
    public const int Resume = 2;
}

// ---------------------------------------------------------------- превью (§7.1)

public enum IssueSeverity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>1..99 = <see cref="RejectReason"/> (ошибки), 100+ = предупреждения и сведения.</summary>
public enum IssueCode : ushort
{
    DeadEnd = 100,
    ZoneAccessLost = 101,
    StopAccessLost = 102,
    PedAccessLost = 103,
    DrainNeeded = 104,            // Value = машин + пешеходов
    RoutesAffected = 105,
    BusRouteShortened = 106,
    SignalPlanReset = 107,
    NodeRebuildDelayed = 108,
    OverlayRebuild = 109,
    NearCapacity = 110,
    UndoUnavailable = 111,        // обратная запись не влезет
    ZoneLotsLost = 112,           // резерв ADR-0011
    ZoneBuildingsAbandoned = 114, // PaintZone: здания другого вида уйдут в Abandoned; Value = сколько
    ZoneNothingToPaint = 115,     // PaintZone: под прямоугольником нет ни одного участка (Info)
    SetbackShort = 113,           // RP7: отступ соседнего рукава меньше h + corner_radius нового профиля (ADR-0028: рукава не перестраиваются); Value = нехватка, мм
}

[Flags]
public enum PreviewOptions : uint
{
    None = 0,
    Detail = 1,                   // считать RoutesThrough (O(маршрутов))
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PreviewIssue
{
    public IssueCode Code;
    public IssueSeverity Severity;
    public byte ObjKind;          // EntityKind
    public EntityRef Obj;
    public float X, Z;
    public int Value;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PreviewResult
{
    public RejectReason Verdict;          // None - команда применится (предупреждения не мешают)
    public byte Mechanism;                // 0 на месте, 1 перенос, 2 дренаж
    public byte SnapKindA, SnapKindB;     // 0 новый узел, 1 существующий узел, 2 разрез сегмента
    public long Cost;                     // > 0 трата, < 0 возврат
    public float AX, AZ, BX, BZ, LengthM; // концы после привязки
    public EntityRef SnapA, SnapB;        // узел или разрезаемый сегмент
    public int NewNodes, NewSegments, NewLanes, NewConnectors;
    public int DrainVehicles, DrainPeds, BusStopsLost, ZonesLosingAccess;
    public int RoutesThrough;             // -1 - не считалось (нужен PreviewOptions.Detail)
    public int IssueCount, IssuesWritten, AxisPointCount;

    // PaintZone (UI-C1, ui-spec.md §5.2): тот же обход, что у применения (City/Zoning.cs). Для остальных команд - нули.
    public int ZoneLots;                  // участки, получающие зону (центр внутри прямоугольника, не Lost)
    public int ZoneLotsChanged;           // из них реально меняются (вид, плотность или Painted)
    public int ZoneCells;                 // клетки 8 м этих участков
    public int ZoneAbandon;               // здания другого вида, которые уйдут в Abandoned
    public int ZoneUnavailableCells;      // клетки прямоугольника без участка: дорога, вода, парк, нет доступа к дороге
}

/// <summary>Состояния <see cref="PaintLotMark.State"/>.</summary>
public static class PaintLotMarkState
{
    public const byte Unchanged = 0;      // участок уже в этом состоянии
    public const byte Zoned = 1;          // будет зонирован
    public const byte ZonedAbandon = 2;   // будет зонирован, здание другого вида уйдёт в Abandoned
}

/// <summary>Подсветка превью <c>PaintZone</c>: участок (индекс в <c>GetLots</c>), его центр и что с ним станет.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PaintLotMark
{
    public int Lot;
    public byte State;                    // PaintLotMarkState
    public float X, Z;
}

// ---------------------------------------------------------------- undo (§8)

/// <summary>Запись обратных команд ядра для одной применённой команды: <c>[First, First+Count)</c> в <c>InverseCommands</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct InverseRecord
{
    public uint Tag;
    public int First, Count;
    public byte Flags;                    // InverseFlags
    public byte R0, R1, R2;
}

public static class InverseFlags
{
    public const byte Truncated = 1;      // не влезла в буфер: undo недоступен
    public const byte NoInverse = 2;      // команда не инструмент (обратной нет)
}

/// <summary>Прогресс длительной операции инструмента (дренаж) для индикатора на дороге.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ToolOpView
{
    public int Id;
    public CommandKind Kind;
    public EntityRef Target;
    public int Remaining;
    public long StartTick;
}

/// <summary>Одна запись «старый handle -> новый» из события <c>*Remapped</c>. <c>OldGeneration == 0</c> - любое.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RemapEntry
{
    public EntityKind Kind;
    public int OldIndex, OldGeneration;
    public int NewIndex, NewGeneration;
}

/// <summary>Таблица перенумерации хоста (память принадлежит хосту; ядро её не хранит).</summary>
public readonly ref struct RemapTable
{
    public readonly ReadOnlySpan<RemapEntry> Entries;

    public RemapTable(ReadOnlySpan<RemapEntry> entries)
    {
        Entries = entries;
    }
}

/// <summary>Чистые функции над ссылками команд.</summary>
public static class CommandRefs
{
    /// <summary>
    /// Переводит ссылки команды по таблице (хост правит стеки undo/redo после <c>*Remapped</c>, player-tools.md §8.2).
    /// Ссылки с <c>Generation == 0</c> не трогаются (без проверки / не заданы). Одна запись = один шаг:
    /// цепочки old -> new1 -> new2 хост применяет событиями по порядку. Без аллокаций.
    /// </summary>
    public static void Remap(ref Command cmd, in RemapTable table)
    {
        ReadOnlySpan<RemapEntry> t = table.Entries;
        if (t.IsEmpty)
        {
            return;
        }

        switch (cmd.Kind)
        {
            case CommandKind.BuildRoad:
                Fix(ref cmd.BuildRoad.RestoreOf, EntityKind.Segment, t);
                Fix(ref cmd.BuildRoad.RestoreNodeA, EntityKind.Node, t);
                Fix(ref cmd.BuildRoad.RestoreNodeB, EntityKind.Node, t);
                break;
            case CommandKind.SetSignalPlan:
                FixNode(ref cmd.SetSignalPlan.Node, ref cmd.SetSignalPlan.NodeGen, t);
                break;
            case CommandKind.SetNodeControl:
                FixNode(ref cmd.SetNodeControl.Node, ref cmd.SetNodeControl.NodeGen, t);
                break;
            case CommandKind.CreateBusRoute:
                Fix(ref cmd.CreateBusRoute.RestoreOf, EntityKind.BusRoute, t);
                break;
            case CommandKind.RemoveRoad:
                Fix(ref cmd.RemoveRoad.Seg, EntityKind.Segment, t);
                break;
            case CommandKind.UpgradeRoad:
                Fix(ref cmd.UpgradeRoad.Seg, EntityKind.Segment, t);
                break;
            case CommandKind.SplitRoad:
                Fix(ref cmd.SplitRoad.Seg, EntityKind.Segment, t);
                Fix(ref cmd.SplitRoad.RestoreNode, EntityKind.Node, t);
                Fix(ref cmd.SplitRoad.RestoreSeg1, EntityKind.Segment, t);
                Fix(ref cmd.SplitRoad.RestoreSeg2, EntityKind.Segment, t);
                break;
            case CommandKind.MergeAtNode:
                Fix(ref cmd.MergeAtNode.Node, EntityKind.Node, t);
                Fix(ref cmd.MergeAtNode.RestoreSeg, EntityKind.Segment, t);
                break;
            case CommandKind.SetArmControl:
                Fix(ref cmd.SetArmControl.Node, EntityKind.Node, t);
                break;
            case CommandKind.SetTurnBan:
                Fix(ref cmd.SetTurnBan.Node, EntityKind.Node, t);
                break;
            case CommandKind.SetLaneFlags:
                Fix(ref cmd.SetLaneFlags.Seg, EntityKind.Segment, t);
                break;
            case CommandKind.InsertBusStop:
                Fix(ref cmd.InsertBusStop.Route, EntityKind.BusRoute, t);
                Fix(ref cmd.InsertBusStop.Seg, EntityKind.Segment, t);
                Fix(ref cmd.InsertBusStop.RestoreOf, EntityKind.BusStop, t);
                break;
            case CommandKind.RemoveBusStop:
                Fix(ref cmd.RemoveBusStop.Stop, EntityKind.BusStop, t);
                break;
            case CommandKind.RemoveBusRoute:
                Fix(ref cmd.RemoveBusRoute.Route, EntityKind.BusRoute, t);
                break;
            case CommandKind.SetBusRouteParams:
                Fix(ref cmd.SetBusRouteParams.Route, EntityKind.BusRoute, t);
                break;
        }
    }

    private static void FixNode(ref int index, ref int generation, ReadOnlySpan<RemapEntry> t)
    {
        var r = new EntityRef(index, generation);
        Fix(ref r, EntityKind.Node, t);
        index = r.Index;
        generation = r.Generation;
    }

    private static void Fix(ref EntityRef r, EntityKind kind, ReadOnlySpan<RemapEntry> t)
    {
        if (r.Generation == 0)
        {
            return;
        }

        for (int i = 0; i < t.Length; i++)
        {
            ref readonly RemapEntry e = ref t[i];
            if (e.Kind == kind && e.OldIndex == r.Index && (e.OldGeneration == 0 || e.OldGeneration == r.Generation))
            {
                r.Index = e.NewIndex;
                r.Generation = e.NewGeneration;
                return;
            }
        }
    }
}
