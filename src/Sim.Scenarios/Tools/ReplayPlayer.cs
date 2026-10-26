using System;
using Sim.Core.Api;

namespace Sim.Scenarios.Tools;

/// <summary>Итог ре-сима журнала.</summary>
public sealed class ReplayResult
{
    /// <summary>Ок = ре-сим прошёл (проверьте <see cref="Diverged"/>); иначе журнал не применим к этому миру (<see cref="Error"/>).</summary>
    public ReplayStatus Status { get; set; }

    public string Error { get; set; } = string.Empty;

    public long EndTick { get; set; }

    public ulong EndHash { get; set; }

    public int CommandsApplied { get; set; }

    public int CheckpointsChecked { get; set; }

    /// <summary>Первое расхождение контрольного хэша (только при <c>check</c>).</summary>
    public bool Diverged { get; set; }

    /// <summary>Последний тик, на котором хэш сошёлся (-1: не сошёлся уже базовый хэш).</summary>
    public long LastGoodTick { get; set; } = -1;

    /// <summary>Тик расхождения: ближайшая контрольная точка после ошибки; точность - <see cref="ReplayLog.Interval"/> тиков (для точного - писать журнал с малым интервалом).</summary>
    public long BadTick { get; set; }

    public ulong Expected { get; set; }

    public ulong Actual { get; set; }
}

/// <summary>Ре-сим по журналу (player-tools.md §9): мир уже создан на <see cref="ReplayLog.StartTick"/> (сценарий/сейв), хост ставит команды и сверяет хэши.</summary>
public static class ReplayPlayer
{
    /// <param name="sim">Мир на тике <c>&gt;= log.StartTick</c> (сейв позже начала журнала допустим: команды раньше пропускаются).</param>
    /// <param name="log">Журнал.</param>
    /// <param name="until">Тик остановки; отрицательный - до конца записи (<see cref="ReplayLog.EndTick"/>).</param>
    /// <param name="check">Сверять контрольные хэши (останов на первом расхождении).</param>
    /// <param name="trace">Необязательный вывод строк по контрольным точкам.</param>
    public static ReplayResult Run(Simulation sim, ReplayLog log, long until = -1, bool check = true, Action<string>? trace = null)
    {
        var r = new ReplayResult { Status = ReplayStatus.Ok };
        if (sim.Tick < log.StartTick)
        {
            r.Status = ReplayStatus.BadCheckpoints;
            r.Error = $"world is at tick {sim.Tick}, before the log start {log.StartTick}";
            return r;
        }

        int cp = 0;
        while (cp < log.Checkpoints.Count && log.Checkpoints[cp].Tick < sim.Tick)
        {
            cp++;
        }

        ulong h0 = sim.ComputeStateHash();
        if (check)
        {
            ulong? want = sim.Tick == log.StartTick ? log.BaseHash : cp < log.Checkpoints.Count && log.Checkpoints[cp].Tick == sim.Tick ? log.Checkpoints[cp].Hash : null;
            if (want.HasValue)
            {
                r.CheckpointsChecked++;
                trace?.Invoke($"start tick={sim.Tick} expected={want.Value:x16} actual={h0:x16} {(want.Value == h0 ? "ok" : "MISMATCH")}");
                if (want.Value != h0)
                {
                    Diverge(r, -1, sim.Tick, want.Value, h0);
                    r.EndTick = sim.Tick;
                    r.EndHash = h0;
                    return r;
                }

                r.LastGoodTick = sim.Tick;
            }
        }

        int n = log.Commands.Count;
        int ci = 0;
        while (ci < n && log.CommandTicks[ci] < sim.Tick)
        {
            ci++;
        }

        long target = until >= 0 ? until : Math.Max(log.EndTick, n > 0 ? log.CommandTicks[n - 1] : 0);
        while (sim.Tick < target)
        {
            while (ci < n && log.CommandTicks[ci] <= sim.Tick)
            {
                Command c = log.Commands[ci];
                if (!sim.Enqueue(in c))
                {
                    r.Status = ReplayStatus.BadCommandOrder;
                    r.Error = $"command #{ci} (tick {log.CommandTicks[ci]}, kind {c.Kind}) was refused by the queue; the log does not fit this world";
                    r.EndTick = sim.Tick;
                    r.EndHash = sim.ComputeStateHash();
                    return r;
                }

                ci++;
                r.CommandsApplied++;
            }

            long stop = target;
            if (ci < n)
            {
                stop = Math.Min(stop, log.CommandTicks[ci]);
            }

            if (cp < log.Checkpoints.Count)
            {
                stop = Math.Min(stop, log.Checkpoints[cp].Tick);
            }

            sim.Step((int)(stop - sim.Tick));
            if (check && cp < log.Checkpoints.Count && log.Checkpoints[cp].Tick == sim.Tick)
            {
                ulong actual = sim.ComputeStateHash();
                ulong expected = log.Checkpoints[cp].Hash;
                r.CheckpointsChecked++;
                trace?.Invoke($"checkpoint tick={sim.Tick} expected={expected:x16} actual={actual:x16} {(expected == actual ? "ok" : "MISMATCH")}");
                if (expected != actual)
                {
                    Diverge(r, r.LastGoodTick, sim.Tick, expected, actual);
                    r.EndTick = sim.Tick;
                    r.EndHash = actual;
                    return r;
                }

                r.LastGoodTick = sim.Tick;
            }

            while (cp < log.Checkpoints.Count && log.Checkpoints[cp].Tick <= sim.Tick)
            {
                cp++;
            }
        }

        r.EndTick = sim.Tick;
        r.EndHash = sim.ComputeStateHash();
        return r;
    }

    private static void Diverge(ReplayResult r, long lastGood, long bad, ulong expected, ulong actual)
    {
        r.Diverged = true;
        r.LastGoodTick = lastGood;
        r.BadTick = bad;
        r.Expected = expected;
        r.Actual = actual;
    }
}
