using System;
using System.Collections.Generic;
using System.Text.Json;
using Sim.Core.Api;

namespace Sim.Scenarios;

// Секция outside (new-game-scratch.md §2.1, ADR-0037): до 4 узлов явной сети у края карты - въезд переселенцев и внешние рабочие места. Включает zoning.grid_from_bounds.
public static partial class ScenarioLoader
{
    private const string OutsideKey = "outside";
    private const float OutsideEdgeM = 50f;

    private static void ParseOutside(JsonObjectReader? r, bool residents, in NetworkDesc network, in ToolsDesc tools, bool terrain, ref CityDesc town)
    {
        if (r == null)
        {
            return;
        }

        if (!residents)
        {
            throw new ScenarioException($"{r.Path}: allowed only with demand.model = \"residents\"");
        }

        if (!tools.HasWorldBounds && !terrain)
        {
            throw new ScenarioException($"{r.Path}: requires tools.world_bounds (or terrain): the city grid is taken from the world bounds");
        }

        CityOutsideDesc o = default;
        o.JobsDecayQ16 = CityDesc.Q16(r.Num("outside_jobs_decay", 0.10, min: 0, max: 1));
        o.ArrivalByCar = (byte)(r.Bool("arrival_by_car", true) ? 1 : 0);
        JsonElement[] conns = r.ReqArray("connections");
        if (conns.Length < 1 || conns.Length > CityOutsideDesc.Max)
        {
            throw new ScenarioException($"{r.ChildPath("connections")}: 1..{CityOutsideDesc.Max} connections expected");
        }

        int nodeCount = network.Nodes?.Length ?? 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        double share = 0;
        int shareQ16 = 0;
        for (int k = 0; k < conns.Length; k++)
        {
            string path = JsonObjectReader.Index(r.ChildPath("connections"), k);
            var c = new JsonObjectReader(conns[k], path);
            string id = c.Str("id");
            if (!ids.Add(id))
            {
                throw new ScenarioException($"{path}.id: duplicate connection id \"{id}\"");
            }

            int node = c.Int("node", min: 0);
            if (node >= nodeCount)
            {
                throw new ScenarioException($"{path}.node: node {node} is not in $.network.nodes (explicit network required)");
            }

            NodeDesc nd = network.Nodes![node];
            if (tools.HasWorldBounds && !NearEdge(nd.X, nd.Z, tools))
            {
                throw new ScenarioException($"{path}.node: node {node} is farther than {OutsideEdgeM} m from tools.world_bounds edge");
            }

            double s = c.Num("immigrant_share", 1.0 / conns.Length, min: 0, max: 1);
            share += s;
            int q = k == conns.Length - 1 ? 65536 - shareQ16 : CityDesc.Q16(s);
            shareQ16 += q;
            o.Set(k, new OutsideConnDesc
            {
                Node = node,
                Jobs = c.Int("jobs", 0, min: 0, max: 10_000_000),
                JobCostTicks = Ticks(c.Num("job_cost_s", 1800, min: 0, max: 86_400)),
                ShareQ16 = q,
            });
            c.Finish();
        }

        if (Math.Abs(share - 1.0) > 0.001)
        {
            throw new ScenarioException($"{r.ChildPath("connections")}: immigrant_share must sum to 1 (got {share})");
        }

        for (int a = 0; a < conns.Length; a++)
        {
            for (int b = a + 1; b < conns.Length; b++)
            {
                if (o.Get(a).Node == o.Get(b).Node)
                {
                    throw new ScenarioException($"{r.ChildPath("connections")}: node {o.Get(a).Node} is used twice");
                }
            }
        }

        r.Finish();
        o.Count = conns.Length;
        town.Outside = o;
        town.Zoning.GridFromBounds = 1;   // §2.1: сетка города по границам мира, не по bbox стартовой дороги
    }

    private static bool NearEdge(float x, float z, in ToolsDesc t) =>
        x - t.BoundsMinX <= OutsideEdgeM || t.BoundsMaxX - x <= OutsideEdgeM || z - t.BoundsMinZ <= OutsideEdgeM || t.BoundsMaxZ - z <= OutsideEdgeM;
}
