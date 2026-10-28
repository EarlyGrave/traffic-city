using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;

namespace Sim.Headless.Bench;

/// <summary>
/// Разбор застрявших машин на конец окна замера (<c>bench --stuck-report</c>, N3/S3): каждая stuck-машина идёт по цепочке <c>Ahead</c> к голове своей полосы,
/// голова классифицируется по виду полосы, отказу ворот, остановке автобуса и т.п.; печатается гистограмма и первые головы с подробностями.
/// Только чтение внутренностей мира (InternalsVisibleTo), ядро не затрагивает.
/// </summary>
internal static class StuckReport
{
    public static void Write(Simulation sim, TextWriter w)
    {
        World world = sim.World;
        VehicleStore v = world.Vehicles;
        NetworkStore net = world.Net;
        var hist = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var heads = new Dictionary<int, int>();
        int stuck = 0;
        for (int i = 0; i < v.HighWater; i++)
        {
            if (!v.Pool.IsAlive(i) || (v.Flags[i] & (byte)VehicleFlags.Stuck) == 0)
            {
                continue;
            }

            stuck++;
            int h = i;
            for (int hop = 0; hop < 10000 && v.Ahead[h] >= 0; hop++)
            {
                h = v.Ahead[h];
            }

            heads[h] = heads.GetValueOrDefault(h) + 1;
            string key = Classify(world, h, i == h);
            hist[key] = hist.GetValueOrDefault(key) + 1;
        }

        w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stuck-report: tick {sim.Tick}, stuck {stuck}, distinct heads {heads.Count}"));
        foreach (KeyValuePair<string, int> kv in hist.OrderByDescending(x => x.Value))
        {
            w.WriteLine($"  {kv.Value,4}  {kv.Key}");
        }

        int shown = 0;
        foreach (KeyValuePair<int, int> kv in heads.OrderByDescending(x => x.Value))
        {
            if (shown++ >= 12)
            {
                break;
            }

            int h = kv.Key;
            int lane = v.Lane[h];
            string li = (uint)lane < (uint)net.LaneCount
                ? $"lane {lane} {net.Lanes[lane].Kind} len={net.Lanes[lane].Length:F1} node={net.Lanes[lane].Node}"
                : "no lane";
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  head {h} (x{kv.Value}): {li} s={v.S[h]:F1} v={v.Speed[h]:F2} gate={v.GateState[h]} deny={(GateDeny)world.Gate.DenyReason[h]} wait={v.WaitTicks[h]} still={v.StillTicks[h]} dwell={v.DwellTicks[h]} flags={(VehicleFlags)v.Flags[h]} nextConn={v.NextConn[h]}"));
        }
    }

    private static string Classify(World world, int h, bool isHead)
    {
        VehicleStore v = world.Vehicles;
        NetworkStore net = world.Net;
        int lane = v.Lane[h];
        string kind = (uint)lane < (uint)net.LaneCount ? net.Lanes[lane].Kind.ToString() : "NoLane";
        var f = (VehicleFlags)v.Flags[h];
        string who = (f & VehicleFlags.InBay) != 0 ? "bus-in-bay" : (f & VehicleFlags.IsBus) != 0 ? ((f & VehicleFlags.AtStop) != 0 ? "bus-at-stop" : "bus") : "car";
        string deny = ((GateDeny)world.Gate.DenyReason[h]).ToString();
        string gate = v.GateState[h] == 0 ? "nogate" : $"gate{v.GateState[h]}";
        string moving = v.Speed[h] > 0.3f ? "moving" : "still";
        return $"{(isHead ? "HEAD" : "queue")} {who} {kind} {gate} deny={deny} {moving}";
    }
}
