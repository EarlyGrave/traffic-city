using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules;
using Sim.Core.Rules.Intersections;
using Sim.Core.Transit;
using Sim.Core.Util;

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

    // Задание для перегрузки без World (тесты, микробенчмарки): один экземпляр на поток, без аллокаций после первого вызова.
    [ThreadStatic]
    private static IntegrateJob? _single;

    /// <summary>Готовит предвыделенное задание фазы 6 к текущему тику (для <see cref="Run(World)"/> и тестов порядка обхода).</summary>
    public static IntegrateJob Prepare(World w)
    {
        IntegrateJob job = w.Integrate;
        job.Prepare(w.Net, w.Vehicles, w.Types, PhysicsParams.From(w), w.Gate.SignalMask, w.Masks);
        return job;
    }

    public static void Run(World w)
    {
        IntegrateJob job = Prepare(w);
        int hw = w.Vehicles.HighWater;
        if (w.Pool != null)
        {
            w.Pool.Run(job, hw);
        }
        else
        {
            job.RunRange(0, 0, hw);
        }

        job.Collect(ref w.Counters);
    }

    public static void Run(
        NetworkStore net,
        VehicleStore v,
        VehicleTypeDesc[] types,
        in PhysicsParams p,
        ref SimCounters c)
    {
        IntegrateJob job = _single ??= new IntegrateJob(1);
        job.Prepare(net, v, types, p);
        job.RunRange(0, 0, v.HighWater);
        job.Collect(ref c);
    }
}

/// <summary>
/// Задание фазы 6 для <see cref="WorkerPool"/> (ADR-0017): функция машины <see cref="Vehicle"/> читает позиции начала тика и зазоры фазы 5
/// и пишет только <c>S/Speed/StillTicks[i]</c>; <c>HardClamps</c> копится целым по воркеру (шаг <see cref="Stride"/> - без false sharing)
/// и сводится в <see cref="Collect"/> после фазы.
/// </summary>
internal sealed partial class IntegrateJob : IRangeJob
{
    private const int Stride = 16;

    private readonly long[] _clamps;
    private NetworkStore _net = null!;
    private VehicleStore _v = null!;
    private VehicleTypeDesc[] _types = Array.Empty<VehicleTypeDesc>();
    private PhysicsParams _p;
    private byte[] _lod = Array.Empty<byte>();   // VehicleStore.Lod; длина 0 - LOD выключен
    private ulong[] _signalMask = Array.Empty<ulong>();   // GateScratch.SignalMask; длина 0 - метка StoppedBeforeSignal не ведётся
    private TickMasks? _masks;                            // маски фаз 7-8; null - не ведутся (перегрузка без World)
    private float[] _laneLength = Array.Empty<float>();    // NetworkStore.LaneLength

    public IntegrateJob(int workers)
    {
        _clamps = new long[Stride * Math.Max(1, workers)];
    }

    public void Prepare(NetworkStore net, VehicleStore v, VehicleTypeDesc[] types, in PhysicsParams p, ulong[]? signalMask = null, TickMasks? masks = null)
    {
        _signalMask = signalMask ?? Array.Empty<ulong>();
        _masks = masks;
        masks?.Begin(v.HighWater);
        _laneLength = net.LaneLength;
        _net = net;
        _v = v;
        _types = types;
        _p = p;
        _lod = v.Lod;
    }

    public void RunRange(int worker, int begin, int end)
    {
        long clamps = 0;
        long mesoClamps = 0;
        for (int i = begin; i < end; i++)
        {
            clamps += Vehicle(i, ref mesoClamps);
        }

        _clamps[worker * Stride] += clamps;
        _clamps[(worker * Stride) + 1] += mesoClamps;
    }

    /// <summary>Складывает <c>HardClamps</c> и <c>MesoClamps</c> воркеров в <paramref name="c"/> и обнуляет их.</summary>
    public void Collect(ref SimCounters c)
    {
        for (int k = 0; k < _clamps.Length; k += Stride)
        {
            c.HardClamps += _clamps[k];
            c.MesoClamps += _clamps[k + 1];
            _clamps[k] = 0;
            _clamps[k + 1] = 0;
        }
    }

    /// <summary>
    /// Метка «ехала &lt; 1 м/с на подходе к светофору» (<see cref="GateRules.StoppedBeforeSignal"/>, диагностика вне хэша): её ставила фаза 4 при проходе
    /// по всем машинам по скорости на начало тика; с активными множествами (ADR-0019, A2) фаза 4 видит только голову полосы, остальным метку ставит
    /// фаза 6 по скорости конца тика - та же скорость, что фаза 4 прочитает в следующем тике (смена полосы и вставка - через <see cref="LaneOccupancy.Linked"/>).
    /// Пишет только слот машины <paramref name="i"/>.
    /// </summary>
    private void MarkStoppedBeforeSignal(int i)
    {
        byte gs = _v.GateState[i];
        if ((gs & (GateRules.Granted | GateRules.StoppedBeforeSignal)) != 0)
        {
            return;   // допущена (метка не нужна) либо уже помечена
        }

        ulong[] mask = _signalMask;
        int lane = _v.Lane[i];
        if (mask.Length != 0 && ((mask[lane >> 6] >> (lane & 63)) & 1UL) != 0UL)
        {
            _v.GateState[i] = (byte)(gs | GateRules.StoppedBeforeSignal);
        }
    }

    /// <summary>Интегрирует машину <paramref name="i"/>; возвращает число сработавших клэмпов I6 (0..2).</summary>
    private int Vehicle(int i, ref long mesoClamps)
    {
        VehicleStore v = _v;
        if (!v.Pool.IsAlive(i) || v.Lane[i] < 0)
        {
            return 0;
        }

        int clamps = Move(i, ref mesoClamps);
        TickMasks? m = _masks;
        if (m != null)
        {
            MarkCandidates(m, i);
        }

        return clamps;
    }

    /// <summary>
    /// Маски фаз 7 и 8 (ADR-0019, A3) по состоянию конца фазы 6: фаза 7 нужна машине со сменой полосы в процессе, с намерением смены или у конца полосы
    /// (<c>S &gt;= LaneLength</c>) - остальным она ничего не делает; фаза 8 - прибывшей на полосу назначения и не автобусу. Пишет только слово маски своего чанка.
    /// </summary>
    private void MarkCandidates(TickMasks m, int i)
    {
        VehicleStore v = _v;
        int lane = v.Lane[i];
        float s = v.S[i];
        if (v.GhostLane[i] != -1 || v.LaneChangeDir[i] != 0 || s >= _laneLength[lane])
        {
            m.Trans[i >> 6] |= 1UL << (i & 63);
        }

        int dest = v.DestLane[i];
        if (dest == lane && s >= v.DestS[i] && (v.Flags[i] & (byte)VehicleFlags.IsBus) == 0)
        {
            m.Arrive[i >> 6] |= 1UL << (i & 63);
        }
    }

    /// <summary>Интегрирует машину <paramref name="i"/>, живую и на полосе; возвращает число сработавших клэмпов I6 (0..2).</summary>
    private int Move(int i, ref long mesoClamps)
    {
        VehicleStore v = _v;
        PhysicsParams p = _p;
        int clamps = 0;
        float dt = p.Dt;

        if (_lod.Length != 0 && (_lod[i] & Lod.LodBits.Meso) != 0)
        {
            MesoVehicle(i, ref mesoClamps);
            return 0;
        }

        ref readonly VehicleTypeDesc t = ref _types[v.Type[i]];
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
        if ((flags & IntegratePhase.ClampLeaderPresent) != 0)
        {
            float sMax = s + v.ClampLeadGap[i] - ((flags & IntegratePhase.ClampLeaderVirtual) != 0 ? 0f : p.MinPhysicalGap);
            if (sNew > sMax)
            {
                sNew = sMax > s ? sMax : s;
                vNew = (sNew - s) / dt;
                clamps++;
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
                clamps++;
            }
        }

        v.S[i] = sNew;
        v.Speed[i] = vNew;
        if (vNew < 1f)
        {
            MarkStoppedBeforeSignal(i);
        }

        if (vNew < p.StuckSpeedThreshold && !BusStopRules.IsServing(_net, v, i))
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

        return clamps;
    }

    /// <summary>
    /// Движение meso-машины (sim-lod.md §2.3): скорость, назначенная фазой 5 в такт машины (<c>MesoNext</c>), затем путь за тик
    /// <c>d = min(Speed·dt, MesoAllow)</c>; остаток пути списывается. Клэмп I6 - через <c>MesoAllow</c> (зазоры начала тика посчитала фаза 5),
    /// поэтому фаза читает только слоты машины <c>i</c>. Сократили путь - <c>Speed = d / dt</c>, <c>MesoClamps++</c>.
    /// </summary>
    private void MesoVehicle(int i, ref long mesoClamps)
    {
        VehicleStore v = _v;
        float dt = _p.Dt;
        float next = v.MesoNext[i];
        if (next >= 0f)
        {
            v.Speed[i] = next;
            v.MesoNext[i] = -1f;
        }

        float speed = v.Speed[i];
        float want = speed * dt;
        float allow = v.MesoAllow[i];
        float d = want;
        if (d > allow)
        {
            d = allow;
            if (want - d > 1e-3f)
            {
                mesoClamps++; // округление пути за такт (4 · v·dt против v·τ) клэмпом не считается
            }

            speed = d / dt;
            v.Speed[i] = speed;
        }

        v.S[i] += d;
        v.MesoAllow[i] = allow - d;
        if (speed < 1f)
        {
            MarkStoppedBeforeSignal(i);
        }

        if (speed < _p.StuckSpeedThreshold && !BusStopRules.IsServing(_net, v, i))
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
