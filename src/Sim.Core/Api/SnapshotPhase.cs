using System;
using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Api;

/// <summary>
/// Фаза 10 (core-api 5.2, 7): заполняет <c>Render[(Tick + 1) &amp; 1]</c> из <see cref="VehicleStore"/> и ставит
/// <c>SnapshotTick = Tick + 1</c> (<c>Tick++</c> делает <see cref="PhaseList.RunTick"/> сразу после).
/// Пишет слоты [0, HighWater) (<c>SnapshotSlots</c>); индекс = слот; неживые слоты (и живые без полосы) - нули.
/// Результат побитово равен прежней реализации (позиция/курс по полилинии), но дешевле: курс из кэша
/// <c>LanePieceHeading</c>, нормировка - только при Lateral != 0, нулевой слот не перезаписывается (ADR-0010).
/// </summary>
internal static class SnapshotPhase
{
    public static void Run(World w)
    {
        long snapTick = w.Tick + 1;
        VehicleRenderData[] buf = w.Render[(int)(snapTick & 1)];
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int high = v.HighWater;
        for (int i = 0; i < high; i++)
        {
            int lane = v.Lane[i];
            if (!v.Pool.IsAlive(i) || (uint)lane >= (uint)net.LaneCount)
            {
                if (buf[i].Flags != 0)
                {
                    buf[i] = default;
                }

                continue;
            }

            float centerS = v.S[i] - (v.Length[i] * 0.5f);
            LaneGeometry.EvaluatePos(net.Lanes, net.LanePoints, net.LanePointS, lane, centerS, ref v.PointHint[i], out Vec2 pos, out int piece);
            float lateral = v.Lateral[i];
            float heading = net.LanePieceHeading[piece];
            float x = pos.X;
            float z = pos.Z;
            if (lateral != 0f)
            {
                // Смещение по нормали к курсу: нужен нормированный dir (как в LaneGeometry.Evaluate).
                Vec2 d = net.LanePoints[piece + 1] - net.LanePoints[piece];
                float len = MathF.Sqrt((d.X * d.X) + (d.Z * d.Z));
                Vec2 dir = len > 1e-9f ? d * (1f / len) : new Vec2(1f, 0f);
                x = pos.X + (dir.Z * lateral);
                z = pos.Z - (dir.X * lateral);
            }

            ref VehicleRenderData r = ref buf[i];
            r.X = x;
            r.Y = 0f; // этап 1: плоский мир (Node.Elevation = 0)
            r.Z = z;
            r.Heading = heading;
            r.Speed = v.Speed[i];
            r.Index = i;
            r.Generation = (ushort)v.Pool.GenerationOf(i);
            r.Type = v.Type[i];
            r.Flags = (byte)RenderFlags(w, i);
            r.LateralOffset = lateral;
        }

        w.SnapshotSlots[(int)(snapTick & 1)] = high;
        w.SnapshotTick = snapTick;
    }

    /// <summary>Флаги <see cref="VehicleRenderFlags"/> для живой машины со слота <paramref name="i"/>.</summary>
    public static VehicleRenderFlags RenderFlags(World w, int i)
    {
        VehicleStore v = w.Vehicles;
        var f = VehicleRenderFlags.Active;
        if (v.Accel[i] < -1f)
        {
            f |= VehicleRenderFlags.Braking;
        }

        if (v.LaneChangeDir[i] != 0 || v.GhostLane[i] >= 0)
        {
            f |= VehicleRenderFlags.ChangingLane;
        }

        var vf = (VehicleFlags)v.Flags[i];
        if ((vf & VehicleFlags.Stuck) != 0)
        {
            f |= VehicleRenderFlags.Stuck;
        }

        if ((vf & VehicleFlags.AtStop) != 0)
        {
            f |= VehicleRenderFlags.AtStop;
        }

        if ((vf & VehicleFlags.Selected) != 0)
        {
            f |= VehicleRenderFlags.Selected;
        }

        int lane = v.Lane[i];
        if ((uint)lane < (uint)w.Net.LaneCount && w.Net.Lanes[lane].Kind == LaneKind.Connector)
        {
            f |= VehicleRenderFlags.OnConnector;
        }

        return f;
    }
}
