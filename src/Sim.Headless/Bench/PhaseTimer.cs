using System;
using System.Diagnostics;

namespace Sim.Headless.Bench;

/// <summary>Статистика длительности одной фазы за окно замера, мс.</summary>
internal readonly record struct PhaseStat(double P50, double P95, double Mean);

/// <summary>
/// Зонд фаз для <c>bench --phases</c>: подписывается на <c>Simulation.OnPhaseEnd</c> и копит длительность каждой из 10 фаз
/// на каждый тик в заранее выделенные буферы (в окне замера аллокаций нет). Ядро время не читает - его меряет только хост.
/// При пешеходах ядро шлёт ещё номера 11-13 (подфазы 2d, 3b, 4p) и метку 14 (время 4a отнести к «gate»): они дают три добавочные фазы
/// <see cref="PedNames"/>; без пешеходов этих вызовов нет.
/// </summary>
internal sealed class PhaseTimer
{
    public const int PhaseCount = 10;
    public const int PedPhaseCount = 3;
    public const int TotalCount = PhaseCount + PedPhaseCount;
    private const int GateLapId = 14;

    /// <summary>Имена фаз по core-api 7 (индекс = номер фазы - 1).</summary>
    public static readonly string[] Names =
    {
        "commands", "spawn", "route", "gate", "behaviour", "integrate", "transition", "arrival", "metrics", "snapshot",
        "ped_spawn", "ped_route", "ped_events",
    };

    /// <summary>Имена добавочных фаз пешеходов (подмножество <see cref="Names"/>).</summary>
    public static readonly string[] PedNames = { "ped_spawn", "ped_route", "ped_events" };

    private readonly double[][] _ms;
    private readonly int _capacity;
    private int _tick;
    private long _last;

    public PhaseTimer(int ticks)
    {
        _capacity = ticks;
        _ms = new double[TotalCount][];
        for (int i = 0; i < TotalCount; i++)
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
        if ((uint)_tick < (uint)_capacity && (uint)(phase - 1) < TotalCount)
        {
            _ms[phase - 1][_tick] = (now - _last) * 1000.0 / Stopwatch.Frequency;
        }
        else if ((uint)_tick < (uint)_capacity && phase == GateLapId)
        {
            _ms[3][_tick] += (now - _last) * 1000.0 / Stopwatch.Frequency;   // 4a идёт в «gate», как без пешеходов
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
        var result = new PhaseStat[TotalCount];
        for (int p = 0; p < TotalCount; p++)
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
