using System.Collections.Generic;

namespace Sim.Core.Save;

// Реестр столбцов-ссылок на сущности сети (4.3, T2; player-tools.md §4.2, TL8). Теги живут в одном месте, а не в каждой строке схемы: так видно полный список и не нужно
// править файлы схем соседних задач. Добавил столбец с индексом полосы/connector'а/сегмента/DirSeg/узла/остановки - добавь строку сюда и обработчик в Relocate (T3);
// тест RefKindTests ловит и пропущенный тег (по именам столбцов), и тег на несуществующий столбец.
internal static partial class SaveSchema
{
    /// <summary>Тег: блок, столбец, вид сущности, допустим ли -1 («нет»). Диапазон по виду - <c>SaveValidator.ValidateRefs</c>.</summary>
    internal readonly record struct RefTag(string Block, string Column, RefKind Kind, bool Nullable);

    internal static readonly RefTag[] Refs =
    {
        // Статика сети.
        new("NSEG", "NodeA", RefKind.Node, true),   // -1 у мёртвого слота
        new("NSEG", "NodeB", RefKind.Node, true),
        new("NSEG", "FirstLaneAB", RefKind.Lane, true),
        new("NSEG", "FirstLaneBA", RefKind.Lane, true),
        new("NLAN", "Segment", RefKind.Segment, true),
        new("NLAN", "Node", RefKind.Node, true),
        new("NLAN", "FromLane", RefKind.Lane, true),
        new("NLAN", "ToLane", RefKind.Lane, true),
        new("NLAN", "Left", RefKind.Lane, true),
        new("NLAN", "Right", RefKind.Lane, true),
        new("NLAN", "ConflictGroup", RefKind.Connector, true),
        new("NARM", "NodeArms", RefKind.Segment, false),
        new("NLNX", "LaneNext", RefKind.Lane, false),
        new("NZON", "ZoneLanes", RefKind.Lane, false),
        new("NBUS", "StopLane", RefKind.Lane, true),
        new("NBUS", "BusRouteStops", RefKind.Stop, false),
        new("CONN", "Lane", RefKind.Lane, true),   // -1 у мёртвой строки (дыра или запас блока строк узла)
        new("CONN", "Node", RefKind.Node, true),
        new("CONN", "InLane", RefKind.Lane, true),
        new("CONN", "OutLane", RefKind.Lane, true),
        new("SIGS", "Node", RefKind.Node, true),
        new("SIGS", "StateNode", RefKind.Node, false),

        // Машины, поездки, маршруты (NextConn и GrantConnector хранят полосу connector'а).
        new("VEHC", "Lane", RefKind.Lane, true),
        new("VEHC", "GhostLane", RefKind.Lane, true),
        new("VEHC", "DestLane", RefKind.Lane, true),
        new("VEHC", "NextConn", RefKind.Lane, true),
        new("VEHC", "GrantConnector", RefKind.Lane, true),
        new("TRIP", "FromLane", RefKind.Lane, true),
        new("TRIP", "DestLane", RefKind.Lane, true),
        new("ROUT", "WinLane", RefKind.Lane, true),
        new("ROUT", "WinNode", RefKind.Node, true),
        new("OVLY", "Entries", RefKind.DirSeg, true),
        new("OVLY", "Exits", RefKind.DirSeg, true),

        // Пешеходы и город: остановки и сегменты участков.
        new("PEDS", "AlightStop", RefKind.Stop, true),
        new("RESI", "StopAOut", RefKind.Stop, true),
        new("RESI", "StopBOut", RefKind.Stop, true),
        new("RESI", "StopABack", RefKind.Stop, true),
        new("RESI", "StopBBack", RefKind.Stop, true),
        new("RESD", "LegStopA", RefKind.Stop, true),
        new("RESD", "LegStopB", RefKind.Stop, true),
        new("LOTS", "Seg", RefKind.Segment, true),
    };

    private static BlockDef[] ApplyRefs(BlockDef[] blocks)
    {
        foreach (RefTag t in Refs)
        {
            ColumnDef? col = FindColumn(blocks, t.Block, t.Column);
            if (col == null)
            {
                continue;   // отсутствие столбца ловит тест RefKindTests (а не инициализация схемы)
            }

            col.Ref = t.Kind;
            col.RefNullable = t.Nullable;
        }

        return blocks;
    }

    private static ColumnDef? FindColumn(IEnumerable<BlockDef> blocks, string block, string column)
    {
        foreach (BlockDef b in blocks)
        {
            if (b.Name != block)
            {
                continue;
            }

            foreach (ColumnDef c in b.Columns)
            {
                if (c.Name == column)
                {
                    return c;
                }
            }
        }

        return null;
    }
}
