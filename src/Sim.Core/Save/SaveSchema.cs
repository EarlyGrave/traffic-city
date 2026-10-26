using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Реестр блоков и столбцов <c>world_version</c> (save-format.md §3, приложение A): единственное место, где живут id столбцов. Блоки идут в каноническом
/// порядке; id столбца - порядковый номер в блоке, выбывший id не переиспользуется. Таблица приложения A строится из реестра
/// (<see cref="AppendixMarkdown"/>), тест сверяет её с документом.
/// </summary>
internal static partial class SaveSchema
{
    /// <summary>Версия мира (save-format.md §4, §12): растёт при любом изменении набора блоков/столбцов/смысла.</summary>
    public const int WorldVersion = 4;

    /// <summary>Версия порядка хэша состояния (core-api §9), записывается в заголовок сейва.</summary>
    public const int HashVersion = 3;

    /// <summary>Версия ядра-писателя: <c>major &lt;&lt; 20 | minor &lt;&lt; 10 | patch</c> (справочно).</summary>
    public const int WriterVersion = (0 << 20) | (1 << 10) | 0;

    private static readonly Func<World, long> _one = static _ => 1;

    private static BlockDef[]? _blocks;

    /// <summary>Блоки мира в каноническом порядке записи.</summary>
    public static BlockDef[] Blocks => _blocks ??= BuildBlocks();

    private static BlockDef[] BuildBlocks() => ApplyRefs(new[]
    {
        Extend(Params(), PedParams(), CityParams()),
        Demand(),
        VehicleTypes(),
        BuildingTypesBlock(),
        Extend(WorldState(), PedWorldState(), CityWorldState()),
        Extend(Counters(), PedCounters(), CityCounters()),
        NetworkCounts(),
        Nodes(),
        Segments(),
        Lanes(),
        Arms(),
        LaneNext(),
        Points(),
        Zones(),
        BusStatic(),
        Connectors(),
        Extend(Signals(), PedSignalColumns()),
        NodeDynamic(),
        LaneDynamic(),
        SegmentDynamic(),
        BusDynamic(),
        Extend(Vehicles(), new[] { VehicleResident() }),
        Trips(),
        Queues(),
        Routes(),
        RouteGraphBlock(),
        OverlayBlock(),
        LodBlock(),
        QueueMesoBlock(),
        SignalMetricsBlock(),
        PedsBlock(),
        PedPaths(),
        ResidentsBlock(),
        CrossingsBlock(),
        PedPathQueue(),
        LotsBlock(),
        BuildingsBlock(),
        ResidentsCityBlock(),
        SkimsBlock(),
        LedgerBlock(),
        Manifest(),
    });

    /// <summary>FourCC всех блоков мира (читателю контейнера: остальные необязательные блоки уходят в <c>kept</c>).</summary>
    public static uint[] KnownBlockIds()
    {
        BlockDef[] b = Blocks;
        var ids = new uint[b.Length];
        for (int i = 0; i < b.Length; i++)
        {
            ids[i] = b[i].Id;
        }

        return ids;
    }

    /// <summary>Таблица приложения A save-format.md: блок / id / имя / тип / длина / дефолт.</summary>
    public static string AppendixMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("| Блок | id | Имя | Тип | Длина | Дефолт |\n");
        sb.Append("|---|---|---|---|---|---|\n");
        foreach (BlockDef b in Blocks)
        {
            string cond = b.PresentText == "всегда" ? string.Empty : " (" + b.PresentText + ")";
            foreach (ColumnDef c in b.Columns)
            {
                sb.Append("| `").Append(b.Name).Append('`').Append(cond).Append(" | ")
                    .Append(c.Id.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(c.Name).Append(" | ")
                    .Append(Col.NameOf(c.Type, c.ElemSize)).Append(" | ")
                    .Append(c.Length).Append(" | ")
                    .Append(c.Default).Append(" |\n");
            }
        }

        return sb.ToString();
    }

    private static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Блоки, присутствующие у мира <paramref name="w"/> (условные блоки - по состоянию мира).</summary>
    public static List<BlockDef> PresentBlocks(World w)
    {
        var list = new List<BlockDef>(Blocks.Length);
        foreach (BlockDef b in Blocks)
        {
            if (b.Present == null || b.Present(w))
            {
                list.Add(b);
            }
        }

        return list;
    }

    // Счётчики структур, от которых зависит длина столбцов.
    private static readonly Func<World, int> _nodeN = static w => w.Net.NodeCount;
    private static readonly Func<World, int> _segN = static w => w.Net.SegmentCount;
    private static readonly Func<World, int> _laneN = static w => w.Net.LaneCount;
    private static readonly Func<World, int> _armN = static w => w.Net.NodeArmCount;
    private static readonly Func<World, int> _laneNextN = static w => w.Net.LaneNextCount;
    private static readonly Func<World, int> _segPointN = static w => w.Net.SegmentPointCount;
    private static readonly Func<World, int> _lanePointN = static w => w.Net.LanePointCount;
    private static readonly Func<World, int> _zoneN = static w => w.Net.ZoneCount;
    private static readonly Func<World, int> _zoneLaneN = static w => w.Net.ZoneLaneCount;
    private static readonly Func<World, int> _stopN = static w => w.Net.BusStopCount;
    private static readonly Func<World, int> _busRouteN = static w => w.Net.BusRouteCount;
    private static readonly Func<World, int> _busRouteStopN = static w => w.Net.BusRouteStopCount;
    private static readonly Func<World, int> _connN = static w => w.Connectors.Count;
    private static readonly Func<World, int> _conflictN = static w => w.Connectors.ConflictsUsed;
    private static readonly Func<World, int> _signalN = static w => w.Signals.Count;
    private static readonly Func<World, int> _signalStateN = static w => w.SignalCount;
    private static readonly Func<World, int> _vehicleHw = static w => w.Vehicles.HighWater;

    private static LoadStatus Range(long v, long min, long max) => v >= min && v <= max ? LoadStatus.Ok : LoadStatus.InvalidValue;
}
