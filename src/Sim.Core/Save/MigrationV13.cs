using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 12 -> 13 (save-format.md §4, §12; new-game-scratch.md §2.2, задача Y4): параметры старта с нуля. Чистая функция над <see cref="SaveTables"/>.
/// <c>PARM</c> 256 (<c>MinImmigrantsPerDay</c>) = 0, 257 (<c>GrowthHourly</c>) = 0, 258 (<c>InitialCityBalance</c>) = 0: поведение мира из файла v12 прежнее, хэш тот же.
/// </summary>
internal static class MigrationV13
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        if (parm < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        bool ok = t.SetColumn(parm, 256, new int[1]);
        ok &= t.SetColumn(parm, 257, new byte[1]);
        ok &= t.SetColumn(parm, 258, new int[1]);
        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
