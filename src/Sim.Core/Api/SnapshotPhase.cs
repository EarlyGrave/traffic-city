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

    /// <summary>
    /// Курс, непрерывный по <paramref name="s"/>: в середине куска полилинии - курс куска, на вершине - среднее соседних (кратчайший угол).
    /// У коннектора концы стыкуются с соседними полосами: в s = 0 курс последнего куска FromLane, в s = Length - курс первого куска ToLane.
    /// У обычной полосы на её концах курс куска держится (соседа не знаем). Без тригонометрии, без аллокаций; только для рендера (вне хэша).
    /// </summary>
    internal static float SmoothHeading(NetworkStore net, int lane, int piece, float s)
    {
        ref readonly LaneData l = ref net.Lanes[lane];
        int first = l.FirstPoint;
        int last = first + l.PointCount - 2;
        float sa = net.LanePointS[piece];
        float mid = (sa + net.LanePointS[piece + 1]) * 0.5f;
        float h = net.LanePieceHeading[piece];
        float h0;
        float h1;
        float t;
        if (s >= mid)
        {
            float end;
            if (piece < last)
            {
                end = (net.LanePointS[piece + 1] + net.LanePointS[piece + 2]) * 0.5f;
                h1 = net.LanePieceHeading[piece + 1];
            }
            else if (l.Kind == LaneKind.Connector && (uint)l.ToLane < (uint)net.LaneCount)
            {
                end = net.LanePointS[piece + 1];
                h1 = net.LanePieceHeading[net.Lanes[l.ToLane].FirstPoint];
            }
            else
            {
                return h;
            }

            h0 = h;
            t = end - mid > 1e-6f ? (s - mid) / (end - mid) : 1f;
        }
        else
        {
            float start;
            if (piece > first)
            {
                start = (net.LanePointS[piece - 1] + sa) * 0.5f;
                h0 = net.LanePieceHeading[piece - 1];
            }
            else if (l.Kind == LaneKind.Connector && (uint)l.FromLane < (uint)net.LaneCount)
            {
                start = 0f;
                ref readonly LaneData f = ref net.Lanes[l.FromLane];
                h0 = net.LanePieceHeading[f.FirstPoint + f.PointCount - 2];
            }
            else
            {
                return h;
            }

            h1 = h;
            t = mid - start > 1e-6f ? (s - start) / (mid - start) : 1f;
        }

        if (t < 0f)
        {
            t = 0f;
        }
        else if (t > 1f)
        {
            t = 1f;
        }

        float diff = h1 - h0;
        if (diff > MathF.PI)
        {
            diff -= 2f * MathF.PI;
        }
        else if (diff < -MathF.PI)
        {
            diff += 2f * MathF.PI;
        }

        return h0 + (diff * t);
    }

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

    /// <summary><paramref name="list"/> = null: индексы диапазона - слоты; иначе слот = <c>list[k]</c> (вид, <see cref="RenderViewState"/>).</summary>
    public void Prepare(World w, VehicleRenderData[] buf, int[]? list = null)
    {
        _w = w;
        _buf = buf;
        _list = list;
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

    private void Slot(int i)
    {
        World w = _w;
        VehicleRenderData[] buf = _buf;
        VehicleStore v = w.Vehicles;
        NetworkStore net = w.Net;
        int lane = v.Lane[i];
        if (!v.Pool.IsAlive(i) || (uint)lane >= (uint)net.LaneCount)
        {
            if (buf[i].Flags != 0)
            {
                buf[i] = default;
            }

            return;
        }

        float s = v.S[i];
        float speed = v.Speed[i];
        Lod.QueueMeso? q = w.Queue;
        if (q != null && q.IsQueued(i))
        {
            s = q.ViewS[i];         // синтез очереди (RenderViewState.Gather / SynthesizeAll на главном потоке перед записью)
            speed = q.ViewSpeed[i];
        }

        float centerS = s - (v.Length[i] * 0.5f);
        Vec2 pos;
        int piece;
        bool smooth = false;
        if (centerS < 0f && net.Lanes[lane].Kind == LaneKind.Connector && (uint)net.Lanes[lane].FromLane < (uint)net.LaneCount)
        {
            // Въезд на коннектор: хвост кузова ещё на подъездной полосе - берём точку на ней (точная непрерывность).
            int from = net.Lanes[lane].FromLane;
            float fromS = net.Lanes[from].Length + centerS;
            LaneGeometry.EvaluatePos(net.Lanes, net.LanePoints, net.LanePointS, from, fromS, ref v.PointHint[i], out pos, out piece);
        }
        else if (centerS < 0f)
        {
            // Центр кузова ещё «позади» начала полосы (сразу после перехода): продлеваем первый отрезок назад по прямой,
            // иначе EvaluatePos прижмёт s к 0 и центр рывком уйдёт вперёд на Length/2.
            piece = net.Lanes[lane].FirstPoint;
            Vec2 p0 = net.LanePoints[piece];
            Vec2 d0 = net.LanePoints[piece + 1] - p0;
            float len0 = MathF.Sqrt((d0.X * d0.X) + (d0.Z * d0.Z));
            Vec2 dir0 = len0 > 1e-9f ? d0 * (1f / len0) : new Vec2(1f, 0f);
            pos = p0 + (dir0 * centerS);
        }
        else
        {
            LaneGeometry.EvaluatePos(net.Lanes, net.LanePoints, net.LanePointS, lane, centerS, ref v.PointHint[i], out pos, out piece);
            smooth = true;
        }

        float lateral = v.Lateral[i];
        float heading = smooth ? SnapshotPhase.SmoothHeading(net, lane, piece, centerS) : net.LanePieceHeading[piece];
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
        r.Speed = speed;
        r.Index = i;
        r.Generation = (ushort)v.Pool.GenerationOf(i);
        r.Type = v.Type[i];
        r.Flags = (byte)SnapshotPhase.RenderFlags(_w, i);
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
