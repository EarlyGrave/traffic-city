using System;
using Sim.Core.Peds;

namespace Sim.Core.Api;

// Пешеходы (ADR-0020, pedestrians.md §10): снапшот, запросы, цели. Без секции peds все спаны пусты, запросы возвращают false.
public sealed partial class Simulation
{
    /// <summary>Есть ли в мире пешеходы (секция <c>peds</c>).</summary>
    public bool HasPeds => !_disposed && _w.Peds != null;

    /// <summary>Пешеходы на <see cref="SnapshotTick"/>: длина = HighWater пула пешеходов, индекс = слот; пешеход вне вида, <c>PendingPath</c>, <c>OnBus</c> и мёртвый слот - <c>Flags = 0</c>.</summary>
    public ReadOnlySpan<PedestrianRenderData> Pedestrians => PedSpan((int)(_w.SnapshotTick & 1));

    /// <summary>Пешеходы на <see cref="SnapshotTick"/> - 1 (после первого Step пусто).</summary>
    public ReadOnlySpan<PedestrianRenderData> PreviousPedestrians => PedSpan((int)((_w.SnapshotTick + 1) & 1));

    private ReadOnlySpan<PedestrianRenderData> PedSpan(int buffer)
    {
        PedWorld? pw = _disposed ? null : _w.Peds;
        return pw == null ? default : new ReadOnlySpan<PedestrianRenderData>(pw.Snapshot.Buf[buffer], 0, pw.Snapshot.Slots[buffer]);
    }

    /// <summary>Состояния переходов: биты 0-1 - <c>Free/Claimed/Busy</c>, биты 2-3 - сигнал пешеходам (0 DontWalk, 1 Walk, 2 Clear). Индекс - слот рукава, затем переходы на перегоне.</summary>
    public ReadOnlySpan<byte> CrossingStates
    {
        get
        {
            PedWorld? pw = _disposed ? null : _w.Peds;
            return pw == null ? default : new ReadOnlySpan<byte>(pw.X.CrossingSignals, 0, Math.Min(PedWorld.XCount(_w), pw.X.CrossingSignals.Length));
        }
    }

    /// <summary>Пешеходов на каждом ребре сети пешеходов (насыщение 65535): heatmap «вдали»; индекс = ребро по порядку <c>NetworkView.Sidewalks</c> + <c>Crossings</c>.</summary>
    public ReadOnlySpan<ushort> PedEdgeCounts
    {
        get
        {
            PedWorld? pw = _disposed ? null : _w.Peds;
            return pw == null ? default : new ReadOnlySpan<ushort>(pw.P.EdgeCount, 0, pw.Net.EdgeCount);
        }
    }

    /// <summary>Ожидающих на остановках (режим <c>agents</c>), по остановкам.</summary>
    public ReadOnlySpan<ushort> BusStopWaiting
    {
        get
        {
            PedWorld? pw = _disposed ? null : _w.Peds;
            return pw == null ? default : new ReadOnlySpan<ushort>(pw.StopWaiting, 0, _w.Net.BusStopCount);
        }
    }

    /// <summary>Живых пешеходов (активных поездок).</summary>
    public int ActivePedCount => _disposed || _w.Peds == null ? 0 : _w.Peds.P.AliveCount;

    /// <summary>Жителей-заготовок в мире.</summary>
    public int ResidentCount => _disposed || _w.Peds == null ? 0 : _w.Peds.R.Count;

    /// <summary>Корзин гистограмм ожидания у переходов и на остановках: по 1 с, последняя - «255 с и больше».</summary>
    public const int PedWaitBuckets = PedWorld.HistBuckets;

    /// <summary>
    /// Копирует накопительные гистограммы ожидания (с начала симуляции) у переходов и на остановках в <paramref name="cross"/> и <paramref name="stop"/>
    /// (длина не меньше <see cref="PedWaitBuckets"/>); хост берёт разность и считает p95 (pedestrians.md §9). Без пешеходов - нули.
    /// </summary>
    public void CopyPedWaitHistograms(long[] cross, long[] stop)
    {
        PedWorld? pw = _disposed ? null : _w.Peds;
        if (pw == null)
        {
            Array.Clear(cross, 0, Math.Min(cross.Length, PedWaitBuckets));
            Array.Clear(stop, 0, Math.Min(stop.Length, PedWaitBuckets));
            return;
        }

        Array.Copy(pw.CrossWaitHist, cross, PedWaitBuckets);
        Array.Copy(pw.StopWaitHist, stop, PedWaitBuckets);
    }

    /// <summary>Цель активных пешеходов (<c>SetPedTarget</c>); 0 без секции <c>peds</c>.</summary>
    public int PedTarget => _disposed || _w.Peds == null ? 0 : _w.TargetPeds;

    public bool IsAlive(PedHandle h) => !_disposed && _w.Peds != null && _w.Peds.P.IsAlive(h);

    /// <summary>Публичная копия полей пешехода; false - handle устарел или пешеходов нет.</summary>
    public bool TryGetPedInfo(PedHandle h, out PedInfo info)
    {
        info = default;
        PedWorld? pw = _disposed ? null : _w.Peds;
        if (pw == null || !pw.P.IsAlive(h))
        {
            return false;
        }

        PedStore ps = pw.P;
        int p = h.Index;
        var st = (PedState)ps.State[p];
        var pos = default(PedestrianRenderData);
        PedSnapshot.Slot(_w, pw, p, _w.SnapshotTick, ref pos);
        bool waiting = st is PedState.PendingPath or PedState.AtCrossing or PedState.AtStop;
        info = new PedInfo
        {
            Resident = ps.Resident[p],
            State = st,
            Leg = (PedLeg)ps.Leg[p],
            Edge = ps.Edge[p] & PedStore.StepMask,
            WaitTicks = waiting ? (int)Math.Min(int.MaxValue, Math.Max(0L, _w.SnapshotTick - ps.T0[p])) : 0,
            TripTicks = (int)Math.Min(int.MaxValue, Math.Max(0L, _w.SnapshotTick - ps.TripTick[p])),
            StepsLeft = Math.Max(0, ps.PathCount[p] - ps.PathCursor[p] - 1),
            X = pos.X,
            Z = pos.Z,
        };
        return true;
    }

    /// <summary>Ближайший к точке (в радиусе) активный пешеход по текущему снапшоту; O(N) по слотам вида.</summary>
    public bool TryPickPedestrian(float x, float z, float radius, out PedHandle h)
    {
        h = PedHandle.Null;
        PedWorld? pw = _disposed ? null : _w.Peds;
        if (pw == null || !(radius >= 0f))
        {
            return false;
        }

        ReadOnlySpan<PedestrianRenderData> snap = Pedestrians;
        float best = radius * radius;
        int bestIndex = -1;
        for (int i = 0; i < snap.Length; i++)
        {
            if ((snap[i].Flags & PedRenderFlags.Active) == 0)
            {
                continue;
            }

            float dx = snap[i].X - x;
            float dz = snap[i].Z - z;
            float d = (dx * dx) + (dz * dz);
            if (d <= best)
            {
                best = d;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        h = pw.P.HandleOf(bestIndex);
        return true;
    }
}
