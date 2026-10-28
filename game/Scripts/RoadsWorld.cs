using System;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Host;
using TrafficCity.Tools;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Мир для инструмента «Дороги» (<see cref="IRoadsWorld"/>): привязка к узлам и сегментам по копии сети из последнего кадра (главный поток, без блокировок),
/// сетка и снап - из <see cref="WorldGrid"/>. Поиск полным перебором живых узлов и сегментов: зовётся только при сдвиге курсора, не каждый кадр.
/// </summary>
public sealed class RoadsWorld : IRoadsWorld
{
    private readonly Func<NetworkSnapshot?> _net;
    private readonly WorldGrid _grid;
    private readonly Func<CityHudData> _hud;
    private readonly Func<bool> _paused;

    public RoadsWorld(RoadCatalog catalog, Func<NetworkSnapshot?> net, WorldGrid grid, Func<CityHudData> hud, Func<bool> paused)
    {
        Catalog = catalog;
        _net = net;
        _grid = grid;
        _hud = hud;
        _paused = paused;
    }

    public RoadCatalog Catalog { get; }

    public bool SnapOn => _grid.SnapOn;

    public bool HasBudget => _hud().HasBudget;

    public long Balance => _hud().Balance;

    public bool Paused => _paused();

    public Vec2 SnapToGrid(Vec2 p)
    {
        Vector2 s = _grid.Grid.Snap(new Vector2(p.X, p.Z));
        return new Vec2(s.X, s.Y);
    }

    public bool TryNode(float x, float z, float radius, out Vec2 position)
    {
        position = default;
        NetworkSnapshot? snap = _net();
        if (snap == null)
        {
            return false;
        }

        NetworkView v = snap.View;
        float best = radius * radius;
        bool found = false;
        for (int i = 0; i < v.Nodes.Length; i++)
        {
            if (!v.IsNodeAlive(i))
            {
                continue;
            }

            Vec2 p = v.Nodes[i].Position;
            float dx = p.X - x;
            float dz = p.Z - z;
            float d2 = (dx * dx) + (dz * dz);
            if (d2 <= best)
            {
                best = d2;
                position = p;
                found = true;
            }
        }

        return found;
    }

    public bool TrySegment(float x, float z, float radius, out SegmentHit hit)
    {
        hit = default;
        NetworkSnapshot? snap = _net();
        if (snap == null)
        {
            return false;
        }

        NetworkView v = snap.View;
        float best = radius;
        int seg = -1;
        for (int i = 0; i < v.Segments.Length; i++)
        {
            if (!v.IsSegmentAlive(i))
            {
                continue;
            }

            Vec2 a = v.Nodes[v.Segments[i].NodeA].Position;
            Vec2 b = v.Nodes[v.Segments[i].NodeB].Position;
            float d = PointSegmentDistance(x, z, a, b);
            if (d <= best)
            {
                best = d;
                seg = i;
            }
        }

        return seg >= 0 && TryHit(v, seg, out hit);
    }

    /// <summary>Сегмент по индексу (инспектор сегмента, UI3): handle, концы, профиль и ширина - как у наведения курсором.</summary>
    public bool TryHit(int seg, out SegmentHit hit)
    {
        hit = default;
        NetworkSnapshot? snap = _net();
        return snap != null && TryHit(snap.View, seg, out hit);
    }

    private static bool TryHit(NetworkView v, int seg, out SegmentHit hit)
    {
        hit = default;
        if (!v.IsSegmentAlive(seg))
        {
            return false;
        }

        SegmentData s = v.Segments[seg];
        float width = s.Profile >= 0 && s.Profile < v.RoadProfiles.Length
            ? v.RoadProfiles[s.Profile].WidthMm / 1000f
            : ((s.LanesAB + s.LanesBA) * 3.5f) + 4f;
        hit = new SegmentHit(
            v.SegmentRef(seg),
            v.Nodes[s.NodeA].Position,
            v.Nodes[s.NodeB].Position,
            s.Profile,
            (s.Flags & SegmentFlags.Mirrored) != 0,
            s.Kind,
            s.LanesAB,
            s.LanesBA,
            width);
        return true;
    }

    private static float PointSegmentDistance(float px, float pz, Vec2 a, Vec2 b)
    {
        float abx = b.X - a.X;
        float abz = b.Z - a.Z;
        float len2 = (abx * abx) + (abz * abz);
        float t = len2 < 1e-6f ? 0f : Math.Clamp((((px - a.X) * abx) + ((pz - a.Z) * abz)) / len2, 0f, 1f);
        float dx = a.X + (abx * t) - px;
        float dz = a.Z + (abz * t) - pz;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
