using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Host;

/// <summary>
/// Неизменяемая копия <see cref="NetworkView"/> для главного потока (ADR-0024 п. 3, «версии Network»): поток симуляции делает её при смене
/// <c>Network.Version</c> (редко), кадры несут ссылку. Главный поток читает <see cref="View"/> без блокировок и без доступа к памяти ядра.
/// </summary>
public sealed class NetworkSnapshot
{
    private readonly NodeData[] _nodes;
    private readonly SegmentData[] _segments;
    private readonly LaneData[] _lanes;
    private readonly Vec2[] _lanePoints;
    private readonly float[] _lanePointS;
    private readonly ZoneData[] _zones;
    private readonly BusStopData[] _busStops;
    private readonly BusRouteData[] _busRoutes;
    private readonly int[] _busRouteStops;
    private readonly PedEdgeData[] _sidewalks;
    private readonly PedEdgeData[] _crossings;
    private readonly Vec2[] _pedEdgePoints;
    private readonly Vec2[] _pedCorners;
    private readonly RoadProfileData[] _roadProfiles;
    private readonly RoadElemData[] _profileElems;
    private readonly float _sidewalkWidth;
    private readonly float _crosswalkWidth;
    private readonly int _moduleMm;
    private readonly float _gridX;
    private readonly float _gridZ;

    internal NetworkSnapshot(in NetworkView v)
    {
        Version = v.Version;
        _nodes = v.Nodes.ToArray();
        _segments = v.Segments.ToArray();
        _lanes = v.Lanes.ToArray();
        _lanePoints = v.LanePoints.ToArray();
        _lanePointS = v.LanePointS.ToArray();
        _zones = v.Zones.ToArray();
        _busStops = v.BusStops.ToArray();
        _busRoutes = v.BusRoutes.ToArray();
        _busRouteStops = v.BusRouteStops.ToArray();
        _sidewalks = v.Sidewalks.ToArray();
        _crossings = v.Crossings.ToArray();
        _pedEdgePoints = v.PedEdgePoints.ToArray();
        _pedCorners = v.PedCorners.ToArray();
        _roadProfiles = v.RoadProfiles.ToArray();
        _profileElems = v.ProfileElems.ToArray();
        _sidewalkWidth = v.SidewalkWidthM;
        _crosswalkWidth = v.CrosswalkWidthM;
        _moduleMm = v.ModuleMm;
        _gridX = v.GridOriginX;
        _gridZ = v.GridOriginZ;
    }

    /// <summary>Версия сети на момент копии (<c>NetworkView.Version</c>).</summary>
    public int Version { get; }

    /// <summary>Представление поверх собственных массивов копии; валидно, пока жива ссылка на снимок.</summary>
    public NetworkView View => new(
        Version,
        _nodes,
        _segments,
        _lanes,
        _lanePoints,
        _lanePointS,
        _zones,
        _busStops,
        _busRoutes,
        _busRouteStops,
        _sidewalks,
        _crossings,
        _pedEdgePoints,
        _pedCorners,
        _sidewalkWidth,
        _crosswalkWidth,
        _roadProfiles,
        _profileElems,
        _moduleMm,
        _gridX,
        _gridZ);
}
