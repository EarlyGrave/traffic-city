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
    public static void Run(World w) => RunFor(w, w.Tick + 1);

    /// <summary>Заполняет <c>Render[snapTick &amp; 1]</c> и ставит <c>SnapshotTick</c>; загрузка сейва зовёт её с <c>snapTick = Tick</c> (снапшот состояния «сейчас»).</summary>
    public static void RunFor(World w, long snapTick)
    {
        int b = (int)(snapTick & 1);
        VehicleRenderData[] buf = w.Render[b];
        int high = w.Vehicles.HighWater;
        RenderViewState rv = w.RenderView;
        if (rv.All)
        {
            w.Queue?.SynthesizeAll(snapTick);   // Q-meso: позиции очереди - синтез на начало следующего тика (главный поток)
            SnapshotJob job = w.SnapshotJob;
            job.Prepare(w, buf, null);
            if (w.Pool != null)
            {
                w.Pool.Run(job, high);
            }
            else
            {
                job.RunRange(0, 0, high);
            }

            rv.MarkWrittenAll(b);
            w.Work.SnapshotSlots += high;
        }
        else
        {
            // Вид (ADR-0019): пишутся только слоты полос вида, слоты прошлой записи вне вида обнуляются; длина спана прежняя (HighWater).
            rv.Run(w, buf, b, high);
        }

        w.Peds?.Snapshot.Run(w, snapTick);   // пешеходы: тот же вид, свой двойной буфер (pedestrians.md §10.1)

        // Общие записи - только здесь, на главном потоке, после параллельной части.
        w.SnapshotSlots[(int)(snapTick & 1)] = high;
        w.SnapshotTick = snapTick;
    }

    /// <summary>То же, что <see cref="RenderFlags"/>, но вид полосы (<paramref name="onConnector"/>) уже известен вызывающему: без повторной выборки полосы.</summary>
    internal static VehicleRenderFlags RenderFlagsFast(VehicleStore v, int i, bool onConnector)
    {
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

        if (onConnector)
        {
            f |= VehicleRenderFlags.OnConnector;
        }

        if (v.Lod.Length != 0 && (v.Lod[i] & Lod.LodBits.Meso) != 0)
        {
            f |= VehicleRenderFlags.Meso;
        }

        return f;
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

        if (v.Lod.Length != 0 && (v.Lod[i] & Lod.LodBits.Meso) != 0)
        {
            f |= VehicleRenderFlags.Meso;
        }

        return f;
    }
}

/// <summary>
/// Задание фазы 10 для <see cref="WorkerPool"/> (ADR-0017): функция слота <see cref="Slot"/> читает состояние машины <c>i</c> и сети и пишет
/// только <c>buf[i]</c> и <c>PointHint[i]</c> (кэш подсказки куска полилинии своей машины); общих кэшей и счётчиков нет.
/// </summary>
internal sealed class SnapshotJob : IRangeJob
{
    private World _w = null!;
    private VehicleRenderData[] _buf = Array.Empty<VehicleRenderData>();
    private int[]? _list;
    private Axle[] _axle = Array.Empty<Axle>();   // состояние рендера на машину (задняя ось, карман), вне хэша и сейва; одна структура = одна кэш-линия

    /// <summary>Задняя ось (двухосная модель) и боковой сдвиг кузова карманом (0 .. -ширина полосы), общий для обеих осей. Old* - значения до последнего обновления: повторный прогон в тот же тик (сейв, смена вида) идемпотентен.</summary>
    private struct Axle
    {
        public float RearX;
        public float RearZ;
        public float OldX;
        public float OldZ;
        public float BayCur;
        public float BayOld;
        public int RearGen;   // поколение + 1; 0 - нет состояния
        public int OldGen;
        public long Tick;     // w.Tick последнего обновления
    }

    /// <summary>За сколько тиков кузов сдвигается в карман и обратно (1.5 с); ширина сдвига - ширина полосы сети.</summary>
    private const int BaySlideTicks = 30;

    /// <summary><paramref name="list"/> = null: индексы диапазона - слоты; иначе слот = <c>list[k]</c> (вид, <see cref="RenderViewState"/>).</summary>
    public void Prepare(World w, VehicleRenderData[] buf, int[]? list = null)
    {
        _w = w;
        _buf = buf;
        _list = list;
        int cap = w.Vehicles.Lane.Length;
        if (_axle.Length < cap)
        {
            _axle = new Axle[cap];
        }
    }

    public void RunRange(int worker, int begin, int end)
    {
        int[]? list = _list;
        if (list == null)
        {
            for (int i = begin; i < end; i++)
            {
                Slot(i);
            }
        }
        else
        {
            for (int k = begin; k < end; k++)
            {
                Slot(list[k]);
            }
        }
    }

    /// <summary>Доли длины ТС для двухосной модели рендера: база (между осями) и свес спереди (сзади - остаток, равный переднему).</summary>
    private const float WheelBase = 0.6f;
    private const float FrontOverhang = 0.2f;

    /// <summary>Допуск (м) отклонения |F - R_prev| от базы; больше - разрыв (телепорт, сейв, долгий пропуск в виде): задняя ось ставится заново.</summary>
    private const float ResyncDistance = 4f;

    /// <summary>Тот же поиск куска, что в <see cref="LaneGeometry.EvaluatePos"/> (подсказка идёт в сейв), но без вычисления позиции.</summary>
    private static int AdvanceHint(NetworkStore net, int lane, float s, int hint)
    {
        ref readonly LaneData l = ref net.Lanes[lane];
        int first = l.FirstPoint;
        int lastPiece = l.PointCount - 2;
        float[] ps = net.LanePointS;
        float total = ps[first + lastPiece + 1];
        if (s > total)
        {
            s = total;
        }

        int h = hint;
        if (h < 0)
        {
            h = 0;
        }
        else if (h > lastPiece)
        {
            h = lastPiece;
        }

        while (h < lastPiece && s >= ps[first + h + 1])
        {
            h++;
        }

        while (h > 0 && s < ps[first + h])
        {
            h--;
        }

        return h;
    }

    /// <summary>
    /// atan2 без libm: приведение к [0, 1] одним делением и полином нечётной степени 15 (Абрамовиц-Стиган 4.4.49, ошибка около 1e-7 рад).
    /// MathF.Atan2 стоил около 35 нс на машину - почти половину фазы 10. Только для рендера (курс вне хэша и сейва); чистая float-арифметика,
    /// результат не зависит от платформы. Атан2(0, 0) = 0, как у MathF.Atan2.
    /// </summary>
    internal static float FastAtan2(float y, float x)
    {
        float ax = MathF.Abs(x);
        float ay = MathF.Abs(y);
        float mx = MathF.Max(ax, ay);
        if (mx == 0f)
        {
            return 0f;
        }

        float a = MathF.Min(ax, ay) / mx;
        float a2 = a * a;
        float r = (((((((((-0.0040540580f * a2) + 0.0218612288f) * a2) - 0.0559098861f) * a2) + 0.0964200441f) * a2 - 0.1390853351f) * a2 + 0.1994653599f) * a2 - 0.3332985605f) * a2 + 0.9999993329f;
        r *= a;
        if (ay > ax)
        {
            r = (MathF.PI * 0.5f) - r;
        }

        if (x < 0f)
        {
            r = MathF.PI - r;
        }

        return y < 0f ? -r : r;
    }

    private void Slot(int i)
    {
        World w = _w;
        VehicleRenderData[] buf = _buf;
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int lane = v.Lane[i];
        float s = v.S[i];
        bool inBay = false;
        if (lane < 0 && v.Pool.IsAlive(i) && (v.Flags[i] & (byte)VehicleFlags.InBay) != 0)
        {
            // Автобус в виртуальном кармане (ADR-0029): на полосе его нет, рисуется на остановке; смещение за правый край полосы - ниже, общим боковым сдвигом обеих осей.
            int si = Transit.BusStopRules.TargetStop(net, v, i);
            if (si >= 0)
            {
                lane = net.BusStops[si].Lane;
                s = net.BusStops[si].S;
                inBay = true;
            }
        }

        if (!v.Pool.IsAlive(i) || (uint)lane >= (uint)net.LaneCount)
        {
            if (buf[i].Flags != 0)
            {
                buf[i] = default;
            }

            ref Axle dead = ref _axle[i];
            dead.RearGen = 0;
            dead.OldGen = 0;
            dead.BayCur = 0f;
            return;
        }

        ref readonly LaneData laneData = ref net.Lanes[lane];   // одна выборка данных полосы на машину: ширина и вид (для флагов)
        float lateral = v.Lateral[i];
        float speed = v.Speed[i];
        Lod.QueueMeso? q = w.Queue;
        if (q != null && q.IsQueued(i))
        {
            s = q.ViewS[i];         // синтез очереди (RenderViewState.Gather / SynthesizeAll на главном потоке перед записью)
            speed = q.ViewSpeed[i];
        }

        // Двухосная модель (только рендер): нос F на полосе, задняя ось R тянется за ним на (свес_перед + база), курс R->F, центр = F - dir * L/2.
        float length = v.Length[i];
        // PointHint входит в сейв: обновляем его ровно как раньше (по центру кузова), иначе байтовый корпус сейвов разойдётся.
        float centerS = s - (length * 0.5f);
        int hint = v.PointHint[i];
        if (centerS >= 0f)
        {
            hint = AdvanceHint(net, lane, centerS, hint);
            v.PointHint[i] = hint;
        }

        // Только нос F привязан к полосе (s >= 0 всегда): без спецслучаев на въезде и выезде с перекрёстка.
        // Поиск носа начинается с куска центра (он на 0..1 куска позади): O(1); подсказка носа в сейв не входит.
        LaneGeometry.EvaluatePos(net.Lanes, net.LanePoints, net.LanePointS, lane, s, ref hint, out Vec2 front, out int fp);
        Vec2 pd = net.LanePoints[fp + 1] - net.LanePoints[fp];
        float plen = MathF.Sqrt((pd.X * pd.X) + (pd.Z * pd.Z));
        Vec2 pdir = plen > 1e-9f ? pd * (1f / plen) : new Vec2(1f, 0f);
        if (lateral != 0f)
        {
            // Смещение (смена полосы) применяется к передней оси; задняя догоняет сама - боковое скольжение кузова плавное.
            front = new Vec2(front.X + (pdir.Z * lateral), front.Z - (pdir.X * lateral));
        }

        // Задняя ось: своё состояние на машину (не в хэше и не в сейве), тянется за передней: R = F - norm(F - R_prev) * база.
        float wheelBase = length * (WheelBase + FrontOverhang);   // нос -> задняя ось
        int genRaw = v.Pool.GenerationOf(i);
        int gen = genRaw + 1;
        Vec2 rear;
        long tick = w.Tick;
        ref Axle st = ref _axle[i];
        if (st.Tick != tick)
        {
            // Первый прогон в этом тике: текущая задняя ось становится базой, прежняя база запоминается для повторных прогонов.
            st.OldX = st.RearX;
            st.OldZ = st.RearZ;
            st.OldGen = st.RearGen;
        }

        bool have = st.OldGen == gen;
        float laneW = laneData.Width;   // ширина полосы остановки (ADR-0030; в legacy = Options.LaneWidth); у connector'а Width = 0 - запас, чтобы скольжение не застряло
        if (!(laneW > 0f))
        {
            laneW = w.Builder.Options.LaneWidth;
        }

        float bayTarget = inBay ? -laneW : 0f;
        if (st.Tick != tick)
        {
            st.BayOld = have ? st.BayCur : bayTarget;   // новая машина / загрузка сейфа: без скольжения
        }

        float bayLat = st.BayOld;
        if (bayLat != bayTarget)
        {
            float step = laneW / BaySlideTicks;
            bayLat = bayLat < bayTarget ? MathF.Min(bayLat + step, bayTarget) : MathF.Max(bayLat - step, bayTarget);
        }

        st.BayCur = bayLat;
        Vec2 rd = default;
        float rlen = 0f;
        if (have)
        {
            rd = new Vec2(front.X - st.OldX, front.Z - st.OldZ);
            rlen = MathF.Sqrt((rd.X * rd.X) + (rd.Z * rd.Z));
            have = rlen > 0.25f * wheelBase && rlen < wheelBase + ResyncDistance;
        }

        if (have)
        {
            rear = new Vec2(front.X - (rd.X * (wheelBase / rlen)), front.Z - (rd.Z * (wheelBase / rlen)));
        }
        else
        {
            // Спавн, новое поколение слота, сейв/разрыв: по курсу куска полосы под передней осью (без поиска на траектории).
            rear = new Vec2(front.X - (pdir.X * wheelBase), front.Z - (pdir.Z * wheelBase));
        }

        st.RearX = rear.X;
        st.RearZ = rear.Z;
        st.RearGen = gen;
        st.Tick = tick;
        Vec2 ax = front - rear;
        float alen = MathF.Sqrt((ax.X * ax.X) + (ax.Z * ax.Z));
        Vec2 dir = alen > 1e-4f ? ax * (1f / alen) : pdir;
        float heading = FastAtan2(dir.Z, dir.X);
        if (bayLat != 0f)
        {
            // Карман: тот же боковой сдвиг у обеих осей (задняя ось в состоянии остаётся в системе полосы) - кузов параллелен полосе, без перекоса при заезде и «хлыста» при выезде.
            front = new Vec2(front.X + (pdir.Z * bayLat), front.Z - (pdir.X * bayLat));
        }
        // Кузов покрывает [s - L, s] вдоль курса: центр на L/2 позади носа.
        float x = front.X - (dir.X * length * 0.5f);
        float z = front.Z - (dir.Z * length * 0.5f);

        ref VehicleRenderData r = ref buf[i];
        r.X = x;
        r.Y = 0f; // этап 1: плоский мир (Node.Elevation = 0)
        r.Z = z;
        r.Heading = heading;
        r.Speed = speed;
        r.Index = i;
        r.Generation = (ushort)genRaw;
        r.Type = v.Type[i];
        r.Flags = (byte)SnapshotPhase.RenderFlagsFast(v, i, !inBay && laneData.Kind == LaneKind.Connector);
        r.LateralOffset = lateral;
    }
}

/// <summary>
/// Обнуление ранее записанных слотов буфера снапшота (вид, <see cref="RenderViewState"/>): диапазон индексов (<c>list == null</c>) или список слотов.
/// Пишет только <c>buf[slot]</c> своих элементов; обнуляет, только если <c>Flags != 0</c> (у слота с <c>Flags == 0</c> все поля уже нули).
/// </summary>
internal sealed class SnapshotClearJob : IRangeJob
{
    private VehicleRenderData[] _buf = Array.Empty<VehicleRenderData>();
    private int[]? _list;

    public void Prepare(VehicleRenderData[] buf, int[]? list)
    {
        _buf = buf;
        _list = list;
    }

    public void RunRange(int worker, int begin, int end)
    {
        VehicleRenderData[] buf = _buf;
        int[]? list = _list;
        for (int k = begin; k < end; k++)
        {
            int i = list == null ? k : list[k];
            if (buf[i].Flags != 0)
            {
                buf[i] = default;
            }
        }
    }
}
