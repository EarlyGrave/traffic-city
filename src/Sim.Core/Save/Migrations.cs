using System;
using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Реестр миграций сейва (save-format.md §4). Шаг <c>Steps[v - 1]</c> переводит таблицы <c>world_version = v</c> в <c>v + 1</c>: чистая функция над
/// <see cref="SaveTables"/> (без <c>World</c>, PRNG и времени), переносится на Rust 1:1. Текущая <c>world_version = 13</c>; шаг <c>1 -&gt; 2</c> - <see cref="MigrationV2"/> (пешеходы, ADR-0020), <c>2 -&gt; 3</c> - <see cref="MigrationV3"/> (жители, зоны и экономика, ADR-0011), <c>3 -&gt; 4</c> - <see cref="MigrationV4"/> (сеть с дырами, ADR-0023), <c>4 -&gt; 5</c> - <see cref="MigrationV5"/> (операции дренажа инструментов, ADR-0023 T4), <c>5 -&gt; 6</c> - <see cref="MigrationV6"/> (радиус скругления построителя, ADR-0028), <c>6 -&gt; 7</c> - <see cref="MigrationV7"/> (карман автобусных остановок, ADR-0029), <c>7 -&gt; 8</c> - <see cref="MigrationV8"/> (профили дорог, ADR-0030), <c>8 -&gt; 9</c> - <see cref="MigrationV9"/> (казна города, ADR-0025), <c>9 -&gt; 10</c> - <see cref="MigrationV10"/> (парковка как ресурс ADR-0026 и землепользование в сейве), <c>10 -&gt; 11</c> - <see cref="MigrationV11"/> (прогрессия и показатели города, ADR-0034), <c>11 -&gt; 12</c> - <see cref="MigrationV12"/> (рельеф и вода, ADR-0036). С этапа 3 схема меняется только миграциями:
/// каждый шаг добавляется сюда вместе с golden-файлом исходной версии в <c>tests/SaveCorpus</c> (файлы старых версий не удаляются).
/// </summary>
internal static class Migrations
{
    /// <summary>Шаг миграции; <see cref="LoadStatus.Ok"/> - таблицы переведены на следующую версию.</summary>
    internal delegate LoadStatus Step(SaveTables tables);

    /// <summary>Шаги боевой цепочки: <c>Steps[0]</c> = <c>1 -&gt; 2</c> и т.д. Длина = <c>SaveSchema.WorldVersion - 1</c> (проверяет тест).</summary>
    internal static readonly Step[] Steps = { MigrationV2.Run, MigrationV3.Run, MigrationV4.Run, MigrationV5.Run, MigrationV6.Run, MigrationV7.Run, MigrationV8.Run, MigrationV9.Run, MigrationV10.Run, MigrationV11.Run, MigrationV12.Run, MigrationV13.Run, MigrationV14.Run, MigrationV15.Run, MigrationV16.Run };

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
