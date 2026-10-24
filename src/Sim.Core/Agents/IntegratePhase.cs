using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;

namespace Sim.Core.Agents;

/// <summary>
/// Фаза 6 (core-api §7): интеграция. <c>v' = clamp(v + a*dt, 0, MaxSpeed)</c>, <c>s' = s + v'*dt</c>, затем жёсткий клэмп I6 (§7.6):
/// <c>s' &lt;= S_old[лидер] - L_лидер - MinPhysicalGap</c> по позициям на начало тика: зазоры (<see cref="VehicleStore.ClampLeadGap"/>,
/// <see cref="VehicleStore.ClampGateGap"/>) посчитала фаза 5 (она никого не двигает), фаза 6 - потоковый проход без чтения чужих машин
/// (ADR-0016, D2), поэтому результат не зависит от порядка обхода. Лидер - сосед по списку основной полосы и (во время смены полосы)
/// по списку ghost-полосы, иначе хвост следующей полосы; у полосы без продолжения - конец полосы. Пишет S, Speed, StillTicks.
/// Анимацию <c>Lateral</c> ведёт фаза 7 (она владеет счётчиком манёвра).
/// Перекрёстки (intersections.md §7, при переданном <see cref="ConnectorStore"/>): тот же жёсткий клэмп по виртуальным препятствиям
/// ворот - стоп-линия без допуска (машина не пересекает её без допуска ни при каких IDM-параметрах) и конфликтная точка, занятая
/// машиной с меньшим билетом (I6 на пересекающихся connector'ах); зазоры берутся из фазы 5.
/// </summary>
internal static class IntegratePhase
{
    /// <summary>Биты <see cref="VehicleStore.ClampFlags"/>.</summary>
    public const byte ClampLeaderPresent = 1;
    public const byte ClampLeaderVirtual = 2;

    public static void Run(World w) =>
        Run(w.Net, w.Vehicles, w.Types, PhysicsParams.From(w), ref w.Counters);

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        ReadOnlySpan<VehicleTypeDesc> types,
        in PhysicsParams p,
        ref SimCounters c)
    {
        int hw = v.HighWater;
        float dt = p.Dt;
        for (int i = 0; i < hw; i++)
        {
            if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
            {
                continue;
            }

            ref readonly VehicleTypeDesc t = ref types[v.Type[i]];
            float s = v.S[i];
            float vNew = v.Speed[i] + (v.Accel[i] * dt);
            if (vNew < 0f)
            {
                vNew = 0f;
            }
            else if (vNew > t.MaxSpeed)
            {
                vNew = t.MaxSpeed;
            }

            float sNew = s + (vNew * dt);
            byte flags = v.ClampFlags[i];
            if ((flags & ClampLeaderPresent) != 0)
            {
                float sMax = s + v.ClampLeadGap[i] - ((flags & ClampLeaderVirtual) != 0 ? 0f : p.MinPhysicalGap);
                if (sNew > sMax)
                {
                    sNew = sMax > s ? sMax : s;
                    vNew = (sNew - s) / dt;
                    c.HardClamps++;
                }
            }

            float gateGap = v.ClampGateGap[i];
            if (gateGap < Idm.FreeGap)
            {
                float sMax = s + gateGap;
                if (sNew > sMax)
                {
                    sNew = sMax > s ? sMax : s;
                    vNew = (sNew - s) / dt;
                    c.HardClamps++;
                }
            }

            v.S[i] = sNew;
            v.Speed[i] = vNew;
            if (vNew < p.StuckSpeedThreshold && !BusStopRules.IsServing(net, v, i))
            {
                if (v.StillTicks[i] < ushort.MaxValue)
                {
                    v.StillTicks[i]++;
                }
            }
            else
            {
                v.StillTicks[i] = 0;
            }
        }
    }
}
