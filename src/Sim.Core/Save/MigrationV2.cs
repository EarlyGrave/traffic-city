using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 1 -> 2 (save-format.md §4, pedestrians.md §11): первая боевая. Чистая функция над <see cref="SaveTables"/> (без <c>World</c>, PRNG и времени).
/// Добавляет столбцы с дефолтами «пешеходов нет, пассажиры - счётчик»: <c>PARM</c> (секция peds, модель пассажиров, ёмкости), <c>WRLD</c> (поток <c>Peds</c>, цель),
/// <c>CNTR</c> (счётчики пешеходов), <c>SIGS</c> (пешеходная фаза). Новых блоков не создаёт: они обязательны только при <c>PedsEnabled</c>.
/// Поведение без пешеходов не меняется, поэтому мир из файла w1 продолжается тем же хэшем (хэш пешеходов в состояние не входит).
/// </summary>
internal static class MigrationV2
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int wrld = t.FindBlock(SaveFormat.FourCc("WRLD"u8));
        int cntr = t.FindBlock(SaveFormat.FourCc("CNTR"u8));
        int sigs = t.FindBlock(SaveFormat.FourCc("SIGS"u8));
        if (parm < 0 || wrld < 0 || cntr < 0 || sigs < 0 || !t.TryGetColumn(sigs, 1, out System.ReadOnlySpan<int> count) || count.Length != 1 || count[0] < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        int signals = count[0];
        bool ok = true;

        // PARM: PedsEnabled (86), TransitPassengerModel (87) .. PedStuckStopTicks (112); флаги - u8 (86, 104), остальное - i32; всё нули.
        for (uint id = 86; id <= 112; id++)
        {
            ok &= id is 86 or 104 ? t.SetColumn(parm, id, new byte[1]) : t.SetColumn(parm, id, new int[1]);
        }

        // WRLD: PedRngState (11), PedRngInc (12), TargetPeds (13).
        ok &= t.SetColumn(wrld, 11, new ulong[1]);
        ok &= t.SetColumn(wrld, 12, new ulong[1]);
        ok &= t.SetColumn(wrld, 13, new int[1]);

        // CNTR: 96 - int, 97..117 - long.
        ok &= t.SetColumn(cntr, 96, new int[1]);
        for (uint id = 97; id <= 117; id++)
        {
            ok &= t.SetColumn(cntr, id, new long[1]);
        }

        // SIGS: XWalkLeft (39), XClearLeft (40) - по 8 на светофор, XServed (41) - по одному.
        ok &= t.SetColumn(sigs, 39, new ushort[signals * 8]);
        ok &= t.SetColumn(sigs, 40, new ushort[signals * 8]);
        ok &= t.SetColumn(sigs, 41, new byte[signals]);
        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
