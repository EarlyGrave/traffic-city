using System;
using Godot;
using Sim.Core.Api;

namespace TrafficCity;

/// <summary>
/// Пешеходы: один MultiMesh из капсул (плейсхолдер). Позиция интерполируется Previous -> Current по доле тика,
/// пара кадров сопоставляется по (Index, Generation). Цвет по <see cref="PedState"/>; выбранный - белый.
/// Счётчики по состояниям считаются по текущему снапшоту (то есть только по пешеходам внутри вида).
/// </summary>
public sealed class PedMesh
{
    private const float Radius = 0.28f;
    private const float Height = 1.7f;

    private static readonly Color _walking = new(0.95f, 0.95f, 0.85f);
    private static readonly Color _atCrossing = new(1f, 0.85f, 0.2f);
    private static readonly Color _crossing = new(1f, 0.5f, 0.1f);
    private static readonly Color _atStop = new(0.8f, 0.4f, 1f);
    private static readonly Color _passenger = new(0.45f, 0.8f, 1f);
    private static readonly Color _selected = new(1f, 1f, 1f);

    private readonly BoxBatch _batch;
    private readonly int[] _stateCounts = new int[6];

    public PedMesh()
    {
        _batch = new BoxBatch("Pedestrians", new CapsuleMesh { Radius = Radius, Height = Height, RadialSegments = 6, Rings = 2 }, false);
    }

    public MultiMeshInstance3D Node => _batch.Node;

    /// <summary>Активных пешеходов в виде (по последнему <see cref="Update"/>).</summary>
    public int Active { get; private set; }

    /// <summary>Пешеходов в состоянии <paramref name="state"/> в виде.</summary>
    public int CountOf(PedState state) => _stateCounts[(int)state];

    /// <summary>Перезаписать инстансы. <paramref name="scale"/> раздувает капсулы на дальнем плане; (selIndex, selGen) - выбранный пешеход (-1 - нет).</summary>
    public void Update(Simulation sim, float alpha, float scale, int selIndex, ushort selGen)
    {
        ReadOnlySpan<PedestrianRenderData> cur = sim.Pedestrians;
        ReadOnlySpan<PedestrianRenderData> prev = sim.PreviousPedestrians;
        Array.Clear(_stateCounts);
        int active = 0;
        for (int i = 0; i < cur.Length; i++)
        {
            if ((cur[i].Flags & PedRenderFlags.Active) != 0)
            {
                active++;
            }
        }

        Active = active;
        _batch.Begin(active);
        float a = Math.Clamp(alpha, 0f, 1f);
        for (int i = 0; i < cur.Length; i++)
        {
            ref readonly PedestrianRenderData c = ref cur[i];
            if ((c.Flags & PedRenderFlags.Active) == 0)
            {
                continue;
            }

            float x = c.X;
            float z = c.Z;
            float heading = c.Heading;
            if (i < prev.Length)
            {
                ref readonly PedestrianRenderData p = ref prev[i];
                if ((p.Flags & PedRenderFlags.Active) != 0 && p.Index == c.Index && p.Generation == c.Generation)
                {
                    x = p.X + ((c.X - p.X) * a);
                    z = p.Z + ((c.Z - p.Z) * a);
                    heading = LerpAngle(p.Heading, c.Heading, a);
                }
            }

            if (c.State < _stateCounts.Length)
            {
                _stateCounts[c.State]++;
            }

            bool sel = c.Index == selIndex && c.Generation == selGen;
            float s = sel ? scale * 1.6f : scale;
            _batch.Add(x, c.Y + (Height * 0.5f * s) + 0.05f, z, heading, ColorOf(in c, sel), s);
        }

        _batch.End();
    }

    private static Color ColorOf(in PedestrianRenderData c, bool selected)
    {
        if (selected || (c.Flags & PedRenderFlags.Selected) != 0)
        {
            return _selected;
        }

        return (PedState)c.State switch
        {
            PedState.AtCrossing => _atCrossing,
            PedState.Crossing => _crossing,
            PedState.AtStop => _atStop,
            _ => (c.Flags & PedRenderFlags.Passenger) != 0 ? _passenger : _walking,
        };
    }

    private static float LerpAngle(float from, float to, float t)
    {
        float d = to - from;
        while (d > MathF.PI)
        {
            d -= 2f * MathF.PI;
        }

        while (d < -MathF.PI)
        {
            d += 2f * MathF.PI;
        }

        return from + (d * t);
    }
}
