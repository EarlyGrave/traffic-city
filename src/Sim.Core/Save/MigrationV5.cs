using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 4 -> 5 (save-format.md §4, §12; player-tools.md §4.3, §10; ADR-0023 «Реализация T4»): операции дренажа инструментов. Чистая функция над
/// <see cref="SaveTables"/> (без <c>World</c>, PRNG и времени). Добавляет в <c>NETW</c> столбцы 50-60 («операций нет»): <c>ToolOpCount = 0</c>, <c>ToolOpNextId = 1</c> и пустые
/// массивы полей операций. Новых блоков не создаёт. Поведение и хэш не меняются (операций нет, <c>ToolsTouched</c> прежний), поэтому мир из файла w1-w4 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV5
{
    private static readonly int[] _nextId = { 1 };

    public static LoadStatus Run(SaveTables t)
    {
        int netw = t.FindBlock(SaveFormat.FourCc("NETW"u8));
        if (netw < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        bool ok = true;
        ok &= t.SetColumn(netw, 50, new int[1]);
        ok &= t.SetColumn(netw, 51, _nextId);
        ok &= t.SetColumn(netw, 52, System.Array.Empty<int>());
        ok &= t.SetColumn(netw, 53, System.Array.Empty<byte>());
        ok &= t.SetColumn(netw, 54, System.Array.Empty<int>());
        ok &= t.SetColumn(netw, 55, System.Array.Empty<int>());
        ok &= t.SetColumn(netw, 56, System.Array.Empty<byte>());
        ok &= t.SetColumn(netw, 57, System.Array.Empty<long>());
        ok &= t.SetColumn(netw, 58, System.Array.Empty<long>());
        ok &= t.SetColumn(netw, 59, System.Array.Empty<long>());
        ok &= t.SetColumn(netw, 60, System.Array.Empty<int>());
        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
