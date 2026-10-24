using System;
using System.IO;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tests.Gen;

/// <summary>Полный прогон генераторов по параметрам из scenarios/*.json (город, зоны, маршруты).</summary>
internal sealed class GenRun
{
    public ulong Seed;
    public CityGenDesc City;
    public RulesDesc Rules;
    public SimCapacities Caps;
    public DemandDesc Demand;
    public TransitDesc Transit;
    public NetworkStore Net = null!;
    public NetworkBuilder Builder = null!;
    public CreateStatus CityStatus, ZoneStatus, TransitStatus;
    public CityGenResult City0;
    public ZoneGenResult Zone0;
    public TransitGenResult Transit0;
    public GenZones Zones = null!;
    public GenTransit Buses = null!;
    public Pcg32 RngCity, RngZones, RngTransit;
    public long CityMs;
    public long TotalMs;
}

internal static class GenFixture
{
    public static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "traffic-city.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    public static GenRun Load(string scenario)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "scenarios", scenario + ".json")));
        JsonElement root = doc.RootElement;
        var run = new GenRun { Seed = root.GetProperty("seed").GetUInt64() };

        JsonElement c = root.GetProperty("city");
        JsonElement size = c.GetProperty("size_m");
        run.City = new CityGenDesc
        {
            Enabled = true,
            SizeX = size[0].GetSingle(),
            SizeZ = size[1].GetSingle(),
            Scale = c.GetProperty("scale").GetSingle(),
            BlockSize = c.GetProperty("block_m").GetSingle(),
            ArterialEvery = c.GetProperty("arterial_every").GetInt32(),
            ArterialSpeed = c.GetProperty("arterial_speed_kmh").GetSingle() / 3.6f,
            StreetSpeed = c.GetProperty("street_speed_kmh").GetSingle() / 3.6f,
            LaneWidth = c.GetProperty("lane_width_m").GetSingle(),
            IrregularNodes = c.GetProperty("irregular_nodes").GetInt32(),
            IrregularJitter = c.GetProperty("irregular_jitter_m").GetSingle(),
            RemovedStreetShare = c.GetProperty("removed_street_share").GetSingle(),
            ConnectorSamples = c.GetProperty("connector_samples").GetInt32(),
            Signals = c.GetProperty("signals").GetString() switch
            {
                "all_arterial" => CitySignals.AllArterial,
                "none" => CitySignals.None,
                _ => CitySignals.ArterialXArterial,
            },
            MinorControl = c.GetProperty("minor_control").GetString() switch
            {
                "stop" => MinorControl.Stop,
                "right_hand" => MinorControl.RightHand,
                _ => MinorControl.Yield,
            },
        };

        JsonElement r = root.GetProperty("rules");
        run.Rules = new RulesDesc
        {
            DefaultYellowTicks = (int)(r.GetProperty("signal_yellow_s").GetSingle() * 20f),
            DefaultAllRedTicks = (int)(r.GetProperty("signal_all_red_s").GetSingle() * 20f),
        };

        JsonElement k = root.GetProperty("capacities");
        run.Caps = new SimCapacities
        {
            MaxNodes = k.GetProperty("max_nodes").GetInt32(),
            MaxSegments = k.GetProperty("max_segments").GetInt32(),
            MaxLanes = k.GetProperty("max_lanes").GetInt32(),
            MaxLanePoints = k.GetProperty("max_lane_points").GetInt32(),
            MaxZones = k.GetProperty("max_zones").GetInt32(),
            MaxBusRoutes = k.GetProperty("max_bus_routes").GetInt32(),
            MaxBusStops = k.GetProperty("max_bus_stops").GetInt32(),
            MaxBusStopsPerRoute = k.GetProperty("max_bus_stops_per_route").GetInt32(),
            MaxSignalizedNodes = k.GetProperty("max_signalized_nodes").GetInt32(),
        };

        JsonElement d = root.GetProperty("demand");
        run.Demand = new DemandDesc
        {
            ZoneSize = d.GetProperty("zone_m").GetSingle(),
            WeightJitter = d.GetProperty("weight_jitter").GetSingle(),
        };

        JsonElement t = root.GetProperty("transit");
        run.Transit = new TransitDesc
        {
            Routes = t.GetProperty("routes").GetInt32(),
            HeadwayTicks = (int)(t.GetProperty("headway_s").GetSingle() * 20f),
            StopsPerRouteMin = t.GetProperty("stops_per_route")[0].GetInt32(),
            StopsPerRouteMax = t.GetProperty("stops_per_route")[1].GetInt32(),
            StopSpacing = t.GetProperty("stop_spacing_m").GetSingle(),
            StopKind = t.GetProperty("stop_kind").GetString() switch
            {
                "curb" => StopKindMode.Curb,
                "bay" => StopKindMode.Bay,
                _ => StopKindMode.Mixed,
            },
            BusType = 1,
        };
        return run;
    }

    public static GenRun Run(string scenario, Action<GenRun>? tweak = null)
    {
        GenRun run = Load(scenario);
        tweak?.Invoke(run);
        return Execute(run);
    }

    public static GenRun Execute(GenRun run)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        run.Net = new NetworkStore(run.Caps);
        run.Builder = new NetworkBuilder(run.Net, run.City.LaneWidth, run.City.ConnectorSamples);
        run.RngCity = Pcg32.ForStream(run.Seed, RngStream.CityGen);
        run.RngZones = Pcg32.ForStream(run.Seed, RngStream.Zones);
        run.RngTransit = Pcg32.ForStream(run.Seed, RngStream.Transit);
        run.CityStatus = CityGenerator.Generate(run.City, run.Rules, run.Caps, run.Builder, ref run.RngCity, out run.City0);
        run.CityMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        if (run.CityStatus != CreateStatus.Ok)
        {
            return run;
        }

        run.Zones = new GenZones(run.Caps.MaxZones, run.Caps.MaxLanes);
        run.ZoneStatus = ZoneGenerator.Generate(run.Demand, run.City0.Width, run.City0.Height, run.Net, run.Zones, ref run.RngZones, out run.Zone0);
        run.Buses = new GenTransit(run.Caps.MaxBusRoutes, run.Caps.MaxBusStops, run.Caps.MaxBusStopsPerRoute);
        run.TransitStatus = TransitGenerator.Generate(run.Transit, run.City0.Width, run.City0.Height, run.Net, run.Buses, ref run.RngTransit, out run.Transit0);
        run.TotalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        return run;
    }

    /// <summary>Хэш результатов зон/маршрутов/светофоров (плюс сеть через NetworkStore.Hash()).</summary>
    public static ulong ResultHash(GenRun run)
    {
        var h = Fnv1a64.Create();
        h.Add(run.Net.Hash());
        for (int i = 0; i < run.City0.SignalPlans.Length; i++)
        {
            SignalPlanSeed s = run.City0.SignalPlans[i];
            h.Add(s.Node);
            h.Add(s.Green0);
            h.Add(s.Green1);
            h.Add(s.OffsetTicks);
        }

        for (int i = 0; i < run.Zones.ZoneCount; i++)
        {
            ZoneData z = run.Zones.Zones[i];
            h.Add(z.Center.X);
            h.Add(z.Center.Z);
            h.Add(z.Weight);
            h.Add(z.FirstSpawnLane);
            h.Add(z.SpawnLaneCount);
            h.Add(z.HubNode);
        }

        for (int i = 0; i < run.Zones.ZoneLaneCount; i++)
        {
            h.Add(run.Zones.ZoneLanes[i]);
        }

        for (int i = 0; i < run.Buses.StopCount; i++)
        {
            BusStopData s = run.Buses.Stops[i];
            h.Add(s.Lane);
            h.Add(s.S);
            h.Add((int)s.Kind);
            h.Add(s.Route);
        }

        h.Add(run.RngCity.State);
        h.Add(run.RngZones.State);
        h.Add(run.RngTransit.State);
        return h.Value;
    }
}
