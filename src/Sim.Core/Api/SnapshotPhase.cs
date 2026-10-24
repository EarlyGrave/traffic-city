using System;
using Sim.Core.Agents;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Api;

/// <summary>
/// Фаза 10 (core-api 5.2, 7): заполняет <c>Render[(Tick + 1) &amp; 1]</c> из <see cref="VehicleStore"/> и ставит
/// <c>SnapshotTick = Tick + 1</c> (<c>Tick++</c> делает <see cref="PhaseList.RunTick"/> сразу после).
/// Массив имеет длину MaxVehicles; индекс = слот; неживые слоты (и живые без полосы) - нули.
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
                buf[i] = default;
                continue;
            }

            float centerS = v.S[i] - (v.Length[i] * 0.5f);
            LaneGeometry.Evaluate(net.Lanes, net.LanePoints, net.LanePointS, lane, centerS, ref v.PointHint[i], out Vec2 pos, out Vec2 dir);
            float lateral = v.Lateral[i];
            float x = pos.X + (dir.Z * lateral);
            float z = pos.Z - (dir.X * lateral);
            ref VehicleRenderData r = ref buf[i];
            r.X = x;
            r.Y = 0f; // этап 1: плоский мир (Node.Elevation = 0)
            r.Z = z;
            r.Heading = MathF.Atan2(dir.Z, dir.X);
            r.Speed = v.Speed[i];
            r.Index = i;
            r.Generation = (ushort)v.Pool.GenerationOf(i);
            r.Type = v.Type[i];
            r.Flags = (byte)RenderFlags(w, i);
            r.LateralOffset = lateral;
        }

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
