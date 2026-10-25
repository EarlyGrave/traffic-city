using System;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;

namespace Sim.Core.Rules;

/// <summary>
/// Фаза 5 для meso-машин (ADR-0013, sim-lod.md §2.2): кинематика первого порядка вместо IDM/MOBIL. Живёт внутри того же задания
/// <see cref="BehaviourJob"/> и подчиняется контракту ADR-0017: читает только данные, которых фаза 5 не пишет (S, Speed соседей, списки,
/// билеты ворот), пишет только слоты <c>[i]</c> (<c>MesoAllow</c>, <c>MesoNext</c>, <c>LaneChangeDir</c>, <c>Lod</c>), поэтому хэш не зависит от потоков.
/// Новую скорость фаза 5 не применяет, а кладёт в <c>MesoNext[i]</c>: <c>Speed</c> соседей читает поиск лидера этой же фазы, а применит её фаза 6.
/// <para>
/// Каждый тик: <c>MesoAllow = min(MesoAllow, зазор до непосредственного лидера - MinPhysicalGap)</c> (позиции на начало тика - I6 без чтения
/// чужих записей фазы 6). В «такт» машины (<c>Tick &amp; 3 == цвет ячейки её полосы</c> либо <c>MesoFresh</c>) - полное решение:
/// <c>vT = min(v0, (g - s0) / T)</c>, <c>v' = min(vT, v + a·τ)</c>, <c>MesoAllow = min(v'·τ, зазор лидера, зазор ворот)</c>.
/// </para>
/// </summary>
internal sealed partial class BehaviourJob
{
    /// <summary>Период решения meso, тиков (<c>τ = MesoPeriodTicks · dt</c>).</summary>
    public const int MesoPeriodTicks = 4;

    /// <summary>Объезд стоящего у обочины автобуса: лидер ближе этого зазора, м (sim-lod.md §2.2).</summary>
    public const float MesoBypassDistance = 30f;

    /// <summary>Задаёт данные meso (цвет полос и калибровочные множители) к текущему тику; вызывается после <see cref="Prepare"/>.</summary>
    public void PrepareLod(byte[] laneColor, float headwayScale, float accelScale)
    {
        _laneColor = laneColor;
        _mesoHeadwayScale = headwayScale;
        _mesoAccelScale = accelScale;
        float max = 0f;
        for (int k = 0; k < _types.Length; k++)
        {
            max = MathF.Max(max, _types[k].Length);
        }

        _maxLength = max;
    }

    private float _maxLength = 12f;                  // длина самой длинной машины (типы), м

    private void MesoVehicle(int i, ref SimWork work, ref int vehicles, ref int hops, ref int obstacleCalls)
    {
        RuleEnv e = _e;
        VehicleStore v = e.V;
        byte lodBits = _lod[i];
        if ((v.Flags[i] & (byte)VehicleFlags.AtStop) != 0)
        {
            // Автобус стоит на остановке (посадка): не двигается, решений нет.
            v.MesoAllow[i] = 0f;
            v.MesoNext[i] = -1f;
            v.LaneChangeDir[i] = 0;
            _lod[i] = (byte)(lodBits & ~Lod.LodBits.Fresh);
            return;
        }

        vehicles++;
        int lane = v.Lane[i];
        PhysicsParams p = _p;
        bool beat = (lodBits & Lod.LodBits.Fresh) != 0 || (_tick & (MesoPeriodTicks - 1)) == _laneColor[lane];
        if (!beat)
        {
            // Не такт машины. Лидер в пределах полосы только отодвигается (S не убывает), а новый лидер появляется лишь вставкой - сменой
            // полосы или спавном перед машиной (LaneOccupancy помечает такого ведомого Fresh) либо на следующей полосе у головы. Поэтому клэмп
            // остатка пути (I6) нужен только голове полосы вблизи её конца: зазор до следующей полосы не меньше (Length - S) - длина самой
            // длинной машины. Нулевой остаток клэмпить нечем.
            float left = v.MesoAllow[i];
            if (left > 0f && v.Ahead[i] == -1 && e.Net.LaneLength[lane] - v.S[i] <= left + _maxLength + p.MinPhysicalGap + 1f)
            {
                LeaderInfo near = LeaderSearch.FindFor(e, v.S, i);
                hops += near.Hops;
                if (near.Vehicle != -1 || near.Virtual)
                {
                    float cap = near.Gap - (near.Virtual ? 0f : p.MinPhysicalGap);
                    if (cap < left)
                    {
                        v.MesoAllow[i] = cap > 0f ? cap : 0f;
                    }
                }
            }

            return;
        }

        v.Accel[i] = 0f;
        ref readonly VehicleTypeDesc t = ref _types[v.Type[i]];
        LeaderInfo li = LeaderSearch.FindFor(e, v.S, i);
        hops += li.Hops;
        float leadAllow = li.Vehicle != -1 || li.Virtual ? li.Gap - (li.Virtual ? 0f : p.MinPhysicalGap) : Idm.FreeGap;
        if (leadAllow < 0f)
        {
            leadAllow = 0f;
        }

        _lod[i] = (byte)(lodBits & ~Lod.LodBits.Fresh);

        float gateGap = Idm.FreeGap;
        bool beyondDecision = false;
        if (_connectors != null)
        {
            obstacleCalls++;
            gateGap = GateRules.ObstacleGap(e.Net, v, e.Occ, _connectors, _nodeBusy, ref work, v.S, i, t.ComfortDecel, out beyondDecision, out _);
        }

        // g - зазор для целевой скорости (те же препятствия, что видит IDM); путь до следующего решения ограничивают лидер и ворота (клэмп I6).
        float g = li.Gap;
        if (gateGap < g && !beyondDecision)
        {
            g = gateGap;
        }

        if ((v.Flags[i] & (byte)VehicleFlags.IsBus) != 0)
        {
            float stopGap = BusStopRules.ObstacleGap(e.Net, v, i, t.MinGap);
            if (stopGap < g)
            {
                g = stopGap;
            }
        }

        float tau = MesoPeriodTicks * p.Dt;
        float headway = t.HeadwayTime * _mesoHeadwayScale;
        float a = t.Accel * _mesoAccelScale;
        float vi = v.Speed[i];
        float v0 = v.DesiredSpeed[i];
        float vT = v0;
        if (g < Idm.FreeGap)
        {
            float vg = (g - t.MinGap) / headway;
            vT = vg < v0 ? (vg > 0f ? vg : 0f) : v0;
        }

        float vNew = vi + (a * tau);
        if (vT < vNew)
        {
            vNew = vT;
        }

        if (vNew > t.MaxSpeed)
        {
            vNew = t.MaxSpeed;
        }

        if (vNew < 0f)
        {
            vNew = 0f;
        }

        float allow = vNew * tau;
        if (leadAllow < allow)
        {
            allow = leadAllow;
        }

        if (gateGap < allow)
        {
            allow = gateGap < 0f ? 0f : gateGap;
        }

        v.MesoAllow[i] = allow;
        v.MesoNext[i] = vNew;
        v.LaneChangeDir[i] = (sbyte)MesoLaneChange(i, in li, in t);
    }

    /// <summary>Смена полосы meso (sim-lod.md §2.2): вынужденная по маршруту либо объезд стоящего на остановке автобуса. Добровольных смен нет.</summary>
    private int MesoLaneChange(int i, in LeaderInfo li, in VehicleTypeDesc t)
    {
        RuleEnv e = _e;
        VehicleStore v = e.V;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        if (ld.Kind != LaneKind.Normal || v.GhostLane[i] != -1 || v.QueueBits.Length != 0)
        {
            return 0;   // Q-meso: смены полосы на полосе нет, выбор полосы - при въезде (sim-lod.md §13.5 п. 3)
        }

        // Как у MOBIL (intersections.md §7.1): после допуска и в зоне перед концом полосы порядок не меняется.
        if (GateRules.IsGranted(v, i) || ld.Length - v.S[i] < GateRules.NoLaneChangeZone)
        {
            return 0;
        }

        int desired = e.RouteDesiredLane(i);
        if (desired >= 0 && desired != lane)
        {
            ref readonly LaneData dl = ref e.Net.Lanes[desired];
            if (dl.Segment == ld.Segment && dl.Direction == ld.Direction)
            {
                int forcedDir = dl.LaneNo > ld.LaneNo ? 1 : -1;
                return MesoLaneChangeSafe(i, in t, forcedDir) ? forcedDir : 0;
            }
        }

        if (desired >= 0 && ld.Length - v.S[i] < Mobil.MinOvertakeRemaining)
        {
            return 0;
        }

        // Объезд: непосредственный лидер на своей полосе - стоящий на остановке автобус ближе 30 м.
        int lead = li.Vehicle;
        if (lead == -1 || li.Gap >= MesoBypassDistance || v.Lane[lead] != lane)
        {
            return 0;
        }

        const byte busAtStop = (byte)VehicleFlags.IsBus | (byte)VehicleFlags.AtStop;
        if ((v.Flags[lead] & busAtStop) != busAtStop)
        {
            return 0;
        }

        if (MesoLaneChangeSafe(i, in t, 1))
        {
            return 1;
        }

        return MesoLaneChangeSafe(i, in t, -1) ? -1 : 0;
    }

    /// <summary>
    /// Безопасность смены полосы в направлении <paramref name="dir"/>: те же геометрические условия, что у MOBIL, и зазоры на целевой полосе
    /// спереди и сзади не меньше <c>MinGap</c> (сзади ещё не меньше пути заднего за 1 с); вставка перед допущенным запрещена
    /// (intersections.md §5.1). Читает списки и позиции начала тика.
    /// </summary>
    private bool MesoLaneChangeSafe(int i, in VehicleTypeDesc t, int dir)
    {
        RuleEnv e = _e;
        VehicleStore v = e.V;
        int lane = v.Lane[i];
        ref readonly LaneData ld = ref e.Net.Lanes[lane];
        int target = dir > 0 ? ld.Left : ld.Right;
        if (target < 0 || (ld.Flags & 2) != 0)
        {
            return false;
        }

        ref readonly LaneData tl = ref e.Net.Lanes[target];
        if (tl.Kind != LaneKind.Normal || ((tl.Flags & 1) != 0 && (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0))
        {
            return false;
        }

        float si = v.S[i];
        float durSec = MathF.Max(1, t.LaneChangeDurationTicks) * _p.Dt;
        if (ld.Length - si < (v.Speed[i] * durSec) + 0.5f || si - v.Length[i] < Mobil.LaneEntryClearance)
        {
            return false;
        }

        e.Occ.FindNeighbours(target, si, out int ahead, out int behind);
        if (behind != -1 && GateRules.IsGranted(v, behind))
        {
            return false;
        }

        float front = LeaderSearch.Find(e, v.S, i, target, si, ahead, useRoute: false).Gap;
        if (front < t.MinGap || front < _p.MinPhysicalGap)
        {
            return false;
        }

        if (behind != -1)
        {
            float back = si - v.Length[i] - v.S[behind];
            if (back < t.MinGap || back < _p.MinPhysicalGap || back < v.Speed[behind])
            {
                return false;
            }
        }

        return true;
    }
}
