using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Save;

/// <summary>
/// Реестр столбцов-ссылок (<c>SaveSchema.Refs</c>, TL8, player-tools.md §4.2): теги стоят на существующих i32-столбцах; столбец, похожий на ссылку на полосу, connector, сегмент, узел или остановку,
/// либо помечен, либо явно объявлен «не ссылкой» в списке ниже (с причиной) - новая задача не может добавить такой столбец молча. Обработчики переноса (<c>Relocate</c>) для каждого тега - T3.
/// </summary>
public sealed class RefKindTests
{
    // Имена, по которым столбец подозревается в ссылке на сущность сети.
    private static readonly Regex _suspect = new("Lane|Seg|Node|Conn|Stop|Arm|Edge|DirSeg", RegexOptions.CultureInvariant);

    // Параметры, счётчики и длительности с теми же корнями в имени ссылками не бывают.
    private static readonly Regex _plain = new("^(Max|Transit|Ped|City)|Count|Ticks|Mm$|Q16|Built|Kind|Duration|Samples|Share|Signalized", RegexOptions.CultureInvariant);

    // Блок.столбец -> почему не ссылка на объект сети (или ссылка на другое: пешеходный граф, зона, светофор, машина, маршрут).
    private static readonly Dictionary<string, string> _notRefs = new()
    {
        ["NETW.NodeCount"] = "счётчик (HighWater)",
        ["NETW.SegmentCount"] = "счётчик",
        ["NETW.LaneCount"] = "счётчик",
        ["NETW.LanePointCount"] = "счётчик",
        ["NETW.SegmentPointCount"] = "счётчик",
        ["NETW.NodeArmCount"] = "счётчик",
        ["NETW.LaneNextCount"] = "счётчик",
        ["NETW.ZoneLaneCount"] = "счётчик",
        ["NETW.BusStopCount"] = "счётчик",
        ["NETW.BusRouteStopCount"] = "счётчик",
        ["NETW.NodeFreeCount"] = "счётчик free-list",
        ["NETW.NodeFree"] = "free-list пула узлов: слоты пула, не ссылки команд",
        ["NETW.SegmentFree"] = "free-list пула сегментов",
        ["NETW.BusStopFree"] = "free-list пула остановок",
        ["NETW.SegmentFreeCount"] = "счётчик free-list",
        ["NETW.BusStopFreeCount"] = "счётчик free-list",
        ["NETW.LaneFreeCount"] = "счётчик дыр арены",
        ["NETW.LanePointFreeCount"] = "счётчик дыр арены",
        ["NETW.SegmentPointFreeCount"] = "счётчик дыр арены",
        ["NETW.ArmFreeCount"] = "счётчик дыр арены",
        ["NETW.LaneNextFreeCount"] = "счётчик дыр арены",
        ["NETW.ZoneLaneFreeCount"] = "счётчик дыр арены",
        ["NETW.BusRouteStopFreeCount"] = "счётчик дыр арены",
        ["NETW.LaneFree"] = "дыры арены: класс и первый элемент (упакованы)",
        ["NETW.LanePointFree"] = "дыры арены",
        ["NETW.SegmentPointFree"] = "дыры арены",
        ["NETW.ArmFree"] = "дыры арены",
        ["NETW.LaneNextFree"] = "дыры арены",
        ["NETW.ZoneLaneFree"] = "дыры арены",
        ["NETW.BusRouteStopFree"] = "дыры арены",
        ["NNOD.FirstArm"] = "начало диапазона NodeArms (арена), переносится вместе с блоком",
        ["NNOD.ArmCount"] = "размер диапазона",
        ["NNOD.FirstConnector"] = "начало блока connector'ов узла в Lanes (арена)",
        ["NNOD.ConnectorCount"] = "размер диапазона",
        ["NSEG.FirstPoint"] = "начало диапазона SegmentPoints (арена)",
        ["NSEG.PointCount"] = "размер диапазона",
        ["NLAN.FirstPoint"] = "начало диапазона LanePoints (арена)",
        ["NLAN.PointCount"] = "размер диапазона",
        ["NLAN.FirstNext"] = "начало диапазона LaneNext (арена)",
        ["NLAN.NextCount"] = "размер диапазона",
        ["NZON.FirstSpawnLane"] = "начало диапазона ZoneLanes (арена)",
        ["NZON.SpawnLaneCount"] = "размер диапазона",
        ["NBUS.StopRoute"] = "маршрут остановки (поколения маршрутов - отдельный пул)",
        ["NBUS.StopKind"] = "вид остановки",
        ["NBUS.RouteFirstStop"] = "начало диапазона BusRouteStops (арена)",
        ["NBUS.RouteStopCount"] = "размер диапазона",
        ["NBUS.StopGeneration"] = "поколение слота",
        ["CONN.Count"] = "счётчик",
        ["CONN.FirstConflict"] = "начало диапазона Conflicts (арена конфликтов)",
        ["CONN.ConflictCount"] = "размер диапазона",
        ["CONN.RowFreeCount"] = "счётчик дыр арены",
        ["CONN.ConflictFreeCount"] = "счётчик дыр арены",
        ["CONN.RowFree"] = "дыры арены",
        ["CONN.ConflictFree"] = "дыры арены",
        ["SIGS.ArmLeftHead"] = "машина (слот пула машин)",
        ["LDYN.LaneCount"] = "число машин на полосе",
        ["LDYN.LaneHead"] = "машина (слот пула машин), строка = полоса",
        ["LDYN.LaneTail"] = "машина (слот пула машин)",
        ["VEHC.GhostAhead"] = "машина",
        ["VEHC.GhostBehind"] = "машина",
        ["VEHC.BusRoute"] = "маршрут автобуса",
        ["VEHC.BusNextStop"] = "номер в списке остановок маршрута",
        ["BDYN.StatsActiveBuses"] = "счётчик",
        ["QMSO.LaneLastExit"] = "тик, строка = полоса",
        ["XING.StopQHead"] = "пешеход; строка = остановка",
        ["XING.StopQTail"] = "пешеход; строка = остановка",
        ["XING.StopQCount"] = "счётчик; строка = остановка",
        ["CKPI.RingIncLongStopWait"] = "счётчик случаев за час (progression.md §6.1)",
        ["CKPI.CurIncLongStopWait"] = "счётчик случаев за час",
        ["PEDS.Edge"] = "ребро графа пешеходов - производное сети, перенос по (Side, S) - T11",
        ["PEDS.GoalEdge"] = "ребро графа пешеходов (T11)",
        ["RESI.HomeEdge"] = "ребро графа пешеходов (T11)",
        ["RESI.WorkEdge"] = "ребро графа пешеходов (T11)",
        ["RESI.RouteOut"] = "маршрут автобуса",
        ["RESI.RouteBack"] = "маршрут автобуса",
        ["XING.OnCross"] = "пешеход/число на переходе",
        ["LOTS.SegGen"] = "поколение сегмента участка",
        ["PARM.NetConnectorSamples"] = "параметр построителя",
    };

    private static IEnumerable<(BlockDef Block, ColumnDef Column)> AllColumns() =>
        SaveSchema.Blocks.SelectMany(b => b.Columns.Select(c => (b, c)));

    [Fact]
    public void EveryTag_FindsAnI32Column_AndSetsRef()
    {
        foreach (SaveSchema.RefTag t in SaveSchema.Refs)
        {
            (BlockDef block, ColumnDef col) = AllColumns().SingleOrDefault(x => x.Block.Name == t.Block && x.Column.Name == t.Column);
            Assert.True(col != null, $"тег на несуществующий столбец {t.Block}.{t.Column}");
            Assert.True(col!.Type == ColumnType.I32, $"{block.Name}.{col.Name}: ссылка должна быть i32");
            Assert.Equal(t.Kind, col.Ref);
            Assert.Equal(t.Nullable, col.RefNullable);
        }

        Assert.Equal(SaveSchema.Refs.Length, AllColumns().Count(x => x.Column.Ref != RefKind.None));
    }

    [Fact]
    public void RefLikeColumns_AreTaggedOrDeclaredNotARef()
    {
        var untagged = new List<string>();
        foreach ((BlockDef b, ColumnDef c) in AllColumns())
        {
            if (c.Ref != RefKind.None || c.Type != ColumnType.I32 || !_suspect.IsMatch(c.Name) || _plain.IsMatch(c.Name))
            {
                continue;
            }

            if (!_notRefs.ContainsKey(b.Name + "." + c.Name))
            {
                untagged.Add(b.Name + "." + c.Name);
            }
        }

        Assert.True(
            untagged.Count == 0,
            "Столбцы, похожие на ссылку на объект сети, без тега RefKind (SaveSchema.Refs, добавь и обработчик в Relocate) и без записи в список «не ссылка»: " + string.Join(", ", untagged));
    }

    [Fact]
    public void NotRefList_HasNoStaleEntries_AndNoTaggedColumns()
    {
        HashSet<string> all = AllColumns().Select(x => x.Block.Name + "." + x.Column.Name).ToHashSet();
        foreach (string key in _notRefs.Keys)
        {
            Assert.True(all.Contains(key), $"запись «не ссылка» на несуществующий столбец {key}");
        }

        foreach ((BlockDef b, ColumnDef c) in AllColumns().Where(x => x.Column.Ref != RefKind.None))
        {
            Assert.False(_notRefs.ContainsKey(b.Name + "." + c.Name), $"{b.Name}.{c.Name} помечен и ссылкой, и «не ссылкой»");
        }
    }

    [Theory]
    [InlineData("lane")]
    [InlineData("node")]
    [InlineData("segment")]
    [InlineData("connector")]
    [InlineData("stop")]
    [InlineData("dirseg")]
    public void ValidateRefs_RejectsOutOfRange_AndAcceptsHonestWorld(string kind)
    {
        // Мир с оверлеем и автобусами (все виды ссылок): честный проходит, порча одного значения каждого вида - InvalidReference с id столбца.
        ScenarioDesc d = WorldSaveFixtures.Small(buses: true);
        using Simulation sim = Simulation.Create(d);
        sim.Step(200);
        World w = sim.World;
        var c = new LoadContext(new LoadOptions(), new SimRuntimeOptions()) { W = w };
        Assert.Equal(LoadStatus.Ok, SaveValidator.ValidateRefs(c));

        (string block, string column) = kind switch
        {
            "lane" => ("NLAN", "ToLane"),
            "node" => ("NSEG", "NodeB"),
            "segment" => ("NARM", "NodeArms"),
            "connector" => ("NLAN", "ConflictGroup"),
            "stop" => ("NBUS", "BusRouteStops"),
            _ => ("OVLY", "Entries"),
        };
        switch (kind)
        {
            case "lane":
                w.Net.Lanes[w.Net.Segments[0].FirstLaneAB].ToLane = w.Net.LaneCount;
                break;
            case "node":
                w.Net.Segments[0].NodeB = w.Net.NodeCount;
                break;
            case "segment":
                w.Net.NodeArms[0] = w.Net.SegmentCount;
                break;
            case "connector":
                w.Net.Lanes[w.Net.Nodes[w.Net.Segments[0].NodeA].FirstConnector].ConflictGroup = w.Connectors.Count;
                break;
            case "stop":
                w.Net.BusRouteStops[0] = w.Net.BusStopCount;
                break;
            default:
                w.Overlay!.Entries[0] = 2 * w.Net.SegmentCount;
                break;
        }

        Assert.Equal(LoadStatus.InvalidReference, SaveValidator.ValidateRefs(c));
        ColumnDef col = SaveSchema.Blocks.Single(b => b.Name == block).Columns.Single(x => x.Name == column);
        Assert.Equal(col.Id, c.FailColumn);
        Assert.Equal(SaveSchema.Blocks.Single(b => b.Name == block).Id, c.FailBlock);
    }
}
