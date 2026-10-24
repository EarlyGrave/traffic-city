using System;
using System.Diagnostics;

namespace Sim.Headless.Bench;

/// <summary>Статистика длительности одной фазы за окно замера, мс.</summary>
internal readonly record struct PhaseStat(double P50, double P95, double Mean);

/// <summary>
/// Зонд фаз для <c>bench --phases</c>: подписывается на <c>Simulation.OnPhaseEnd</c> и копит длительность каждой из 10 фаз
/// на каждый тик в заранее выделенные буферы (в окне замера аллокаций нет). Ядро время не читает - его меряет только хост.
/// </summary>
internal sealed class PhaseTimer
{
    public const int PhaseCount = 10;

    /// <summary>Имена фаз по core-api 7 (индекс = номер фазы - 1).</summary>
    public static readonly string[] Names =
    {
        "commands", "spawn", "route", "gate", "behaviour", "integrate", "transition", "arrival", "metrics", "snapshot",
    };

    private readonly double[][] _ms;
    private readonly int _capacity;
    private int _tick;
    private long _last;

    public PhaseTimer(int ticks)
    {
        _capacity = ticks;
        _ms = new double[PhaseCount][];
        for (int i = 0; i < PhaseCount; i++)
        {
            _ms[i] = new double[ticks];
        }
    }

    /// <summary>Вызывается перед <c>Step(1)</c>: начало отсчёта первой фазы тика.</summary>
    public void BeginTick(long timestamp) => _last = timestamp;

    /// <summary>Зонд: длительность фазы = от конца предыдущей (или начала тика) до этого вызова.</summary>
    public void OnPhaseEnd(int phase)
    {
        long now = Stopwatch.GetTimestamp();
        if ((uint)_tick < (uint)_capacity && (uint)(phase - 1) < PhaseCount)
        {
            _ms[phase - 1][_tick] = (now - _last) * 1000.0 / Stopwatch.Frequency;
        }

        if (phase == PhaseCount)
        {
            _tick++;
        }

        _last = Stopwatch.GetTimestamp();
    }

    public PhaseStat[] Summarize()
    {
        int n = Math.Min(_tick, _capacity);
        var result = new PhaseStat[PhaseCount];
        for (int p = 0; p < PhaseCount; p++)
        {
            double[] a = new double[n];
            Array.Copy(_ms[p], a, n);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += a[i];
            }

            Array.Sort(a);
            result[p] = n == 0
                ? default
                : new PhaseStat(a[Math.Clamp((int)Math.Ceiling(0.50 * n) - 1, 0, n - 1)], a[Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1)], sum / n);
        }

        return result;
    }
}
