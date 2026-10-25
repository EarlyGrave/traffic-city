using System;
using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Реестр миграций сейва (save-format.md §4). Шаг <c>Steps[v - 1]</c> переводит таблицы <c>world_version = v</c> в <c>v + 1</c>: чистая функция над
/// <see cref="SaveTables"/> (без <c>World</c>, PRNG и времени), переносится на Rust 1:1. Сейчас <c>world_version = 1</c> - единственная и текущая, шагов нет.
/// До этапа 3 схема меняется без миграций: корпус <c>tests/SaveCorpus</c> перегенерируется (решение владельца 2026-10-25); строгая совместимость
/// включается с этапа 3 - тогда каждый шаг добавляется сюда вместе с golden-файлом исходной версии.
/// </summary>
internal static class Migrations
{
    /// <summary>Шаг миграции; <see cref="LoadStatus.Ok"/> - таблицы переведены на следующую версию.</summary>
    internal delegate LoadStatus Step(SaveTables tables);

    /// <summary>Шаги боевой цепочки: <c>Steps[0]</c> = <c>1 -&gt; 2</c> и т.д. Длина = <c>SaveSchema.WorldVersion - 1</c> (проверяет тест).</summary>
    internal static readonly Step[] Steps = Array.Empty<Step>();

    /// <summary>
    /// Применяет шаги <c>from .. to - 1</c> из <paramref name="steps"/>. Сейв новее целевой версии - <see cref="LoadStatus.TooNew"/> (вниз не мигрируем);
    /// нет шага или шаг вернул не <c>Ok</c> - <see cref="LoadStatus.MigrationFailed"/>. На успехе <c>Header.WorldVersion</c> таблиц равна <paramref name="to"/>.
    /// </summary>
    internal static LoadStatus Apply(SaveTables tables, int from, int to, Step[] steps, out int applied)
    {
        applied = 0;
        if (from > to)
        {
            return LoadStatus.TooNew;
        }

        if (from < 1 || to - 1 > steps.Length)
        {
            return LoadStatus.MigrationFailed;
        }

        for (int v = from; v < to; v++)
        {
            if (steps[v - 1](tables) != LoadStatus.Ok)
            {
                return LoadStatus.MigrationFailed;
            }

            applied++;
        }

        tables.Header.WorldVersion = to;
        return LoadStatus.Ok;
    }
}
