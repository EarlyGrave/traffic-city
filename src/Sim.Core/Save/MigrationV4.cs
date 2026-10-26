using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 3 -> 4 (save-format.md §4, §12; player-tools.md §10, ADR-0023): сеть с дырами. Чистая функция над <see cref="SaveTables"/> (без <c>World</c>, PRNG и времени).
/// Добавляет столбцы с дефолтами «правок не было, всё живо»: <c>NETW</c> 13-49 (<c>ToolsTouched = 0</c>, free-list'ы пулов пусты, у каждой из семи арен: нет дыр, нет потерь,
/// битовая карта лестницы нулевая), <c>CONN</c> 18-25 (арены строк и конфликтов), <c>NNOD</c> 13-14 (<c>Flags</c>, <c>TurnBan</c> = 0), <c>NSEG</c> 13-15 (<c>Flags</c>, <c>Cx</c>, <c>Cz</c> = 0),
/// <c>NBUS</c> 11 (поколение остановки = 1: занята). Поколения узлов, сегментов и маршрутов у старых сейвов уже 1. Новых блоков не создаёт. Поведение и хэш не меняются
/// (<c>ToolsTouched = 0</c>), поэтому мир из файла w1-w3 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV4
{
    public static LoadStatus Run(SaveTables t)
    {
        int netw = t.FindBlock(SaveFormat.FourCc("NETW"u8));
        int conn = t.FindBlock(SaveFormat.FourCc("CONN"u8));
        int nnod = t.FindBlock(SaveFormat.FourCc("NNOD"u8));
        int nseg = t.FindBlock(SaveFormat.FourCc("NSEG"u8));
        int nbus = t.FindBlock(SaveFormat.FourCc("NBUS"u8));
        if (netw < 0 || conn < 0 || nnod < 0 || nseg < 0 || nbus < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        if (!Scalar(t, netw, 1, out int nodes) || !Scalar(t, netw, 2, out int segs) || !Scalar(t, netw, 3, out int lanes) || !Scalar(t, netw, 4, out int lanePts) ||
            !Scalar(t, netw, 5, out int segPts) || !Scalar(t, netw, 6, out int arms) || !Scalar(t, netw, 7, out int laneNext) || !Scalar(t, netw, 9, out int zoneLanes) ||
            !Scalar(t, netw, 10, out int stops) || !Scalar(t, netw, 12, out int routeStops) || !Scalar(t, conn, 1, out int rows) || !Scalar(t, conn, 2, out int conflicts))
        {
            return LoadStatus.MigrationFailed;
        }

        bool ok = true;
        ok &= t.SetColumn(netw, 13, new byte[1]);
        for (uint id = 14; id <= 17; id++)
        {
            ok &= t.SetColumn(netw, id, new int[1]);
        }

        for (uint id = 18; id <= 21; id++)
        {
            ok &= t.SetColumn(netw, id, System.Array.Empty<int>());
        }

        ok &= Arena(t, netw, 22, lanes);
        ok &= Arena(t, netw, 26, lanePts);
        ok &= Arena(t, netw, 30, segPts);
        ok &= Arena(t, netw, 34, arms);
        ok &= Arena(t, netw, 38, laneNext);
        ok &= Arena(t, netw, 42, zoneLanes);
        ok &= Arena(t, netw, 46, routeStops);
        ok &= Arena(t, conn, 18, rows);
        ok &= Arena(t, conn, 22, conflicts);

        ok &= t.SetColumn(nnod, 13, new uint[nodes]);
        ok &= t.SetColumn(nnod, 14, new ulong[nodes]);
        ok &= t.SetColumn(nseg, 13, new byte[segs]);
        ok &= t.SetColumn(nseg, 14, new float[segs]);
        ok &= t.SetColumn(nseg, 15, new float[segs]);
        var stopGen = new int[stops];
        System.Array.Fill(stopGen, 1);
        ok &= t.SetColumn(nbus, 11, stopGen);
        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }

    private static bool Scalar(SaveTables t, int block, uint id, out int value)
    {
        value = 0;
        if (!t.TryGetColumn(block, id, out System.ReadOnlySpan<int> v) || v.Length != 1 || v[0] < 0)
        {
            return false;
        }

        value = v[0];
        return true;
    }

    /// <summary>Арена без дыр: <c>FreeCount</c> = 0, <c>Leaked</c> = 0, пустой список дыр, битовая карта <c>ceil(tail / 64)</c> слов из нулей.</summary>
    private static bool Arena(SaveTables t, int block, uint firstId, int tail) =>
        t.SetColumn(block, firstId, new int[1]) &
        t.SetColumn(block, firstId + 1, new long[1]) &
        t.SetColumn(block, firstId + 2, System.Array.Empty<ulong>()) &
        t.SetColumn(block, firstId + 3, new ulong[(tail + 63) >> 6]);
}
