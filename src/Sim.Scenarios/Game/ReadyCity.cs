using System;
using Sim.Core.Api;

namespace Sim.Scenarios.Game;

/// <summary>Параметры подготовки «готового города» (progression.md §7.3).</summary>
public readonly record struct ReadyCityOptions
{
    /// <summary>Час первых суток (0..23), на котором останавливается подготовка; 6 = 06:00.</summary>
    public int StopHour { get; init; }

    /// <summary>Вызывается примерно раз в 1 000 тиков: (сделано тиков, всего тиков) - для полосы загрузки клиента. Может быть null.</summary>
    public Action<long, long>? Progress { get; init; }

    public static ReadyCityOptions Default => new() { StopHour = 6 };
}

/// <summary>
/// Подготовка «новая игра → готовый город» (docs/design/progression.md §7.3): сценарий в 06:00 первых суток.
/// Все 1.25 суток идут микро-агентами (SetTripExecution(aggregate) вступает только на границе суток и калибруется по уже прошедшим микро-суткам, поэтому
/// «сутки 0 агрегатом» невозможны - расхождение со спекой, progression.md §10 PG6): утренний пик с живыми машинами и заполненное кольцо показателей (ворота вех) ждут игрока в начале игры.
/// Результат детерминирован (seed сценария, число потоков на хэш не влияет); без файлов и времени.
/// </summary>
public static class ReadyCity
{
    private const int ProgressChunkTicks = 1000;

    /// <summary>Тик остановки подготовки для длины суток <paramref name="dayTicks"/>: одни сутки плюс <paramref name="stopHour"/>/24 суток.</summary>
    public static long TargetTick(int dayTicks, int stopHour = 6) => dayTicks + ((long)dayTicks * stopHour / 24);

    /// <summary>
    /// Создаёт мир и доводит его до 06:00 первых суток. Вызывающий владеет возвращённым <see cref="Simulation"/> (Dispose). Статус не <c>Ok</c> - <paramref name="sim"/> = null.
    /// Сценарий без города (<c>demand.model != residents</c>) - <see cref="ArgumentException"/>.
    /// </summary>
    public static CreateStatus TryPrepare(in ScenarioDesc desc, in SimRuntimeOptions options, ReadyCityOptions ready, out Simulation? sim)
    {
        CreateStatus st = Simulation.TryCreate(in desc, in options, out sim);
        if (st != CreateStatus.Ok || sim == null)
        {
            sim = null;
            return st;
        }

        try
        {
            Advance(sim, ready);
        }
        catch
        {
            sim.Dispose();
            sim = null;
            throw;
        }

        return CreateStatus.Ok;
    }

    /// <summary>То же, с настройками по умолчанию (остановка в 06:00); при ошибке создания бросает <see cref="InvalidOperationException"/>.</summary>
    public static Simulation Prepare(in ScenarioDesc desc, in SimRuntimeOptions options = default)
    {
        CreateStatus st = TryPrepare(in desc, in options, ReadyCityOptions.Default, out Simulation? sim);
        return st == CreateStatus.Ok && sim != null ? sim : throw new InvalidOperationException("ReadyCity.Prepare: Simulation.Create failed: " + st);
    }

    /// <summary>Доводит свежесозданный мир (тик 0, режим по умолчанию - микро) до цели.</summary>
    public static void Advance(Simulation sim, ReadyCityOptions ready)
    {
        int day = sim.CityDayTicks;
        if (day <= 0)
        {
            throw new ArgumentException("ReadyCity needs a scenario with demand.model = residents", nameof(sim));
        }

        if (sim.Tick != 0)
        {
            throw new ArgumentException("ReadyCity.Advance expects a fresh simulation (tick 0)", nameof(sim));
        }

        long target = TargetTick(day, ready.StopHour);
        while (sim.Tick < target)
        {
            sim.Step((int)Math.Min(ProgressChunkTicks, target - sim.Tick));
            ready.Progress?.Invoke(sim.Tick, target);
        }
    }
}
