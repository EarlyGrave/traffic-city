using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 13 -> 14 (save-format.md §4, §12; new-game-scratch.md §2.1, задача Y3): внешние подключения. Чистая функция над <see cref="SaveTables"/>.
/// <c>PARM</c> 259-277 = 0 (секции <c>outside</c> нет), блока <c>OUTS</c> нет: поведение мира из файла v13 прежнее, хэш тот же.
/// </summary>
internal static class MigrationV14
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        if (parm < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        bool ok = t.SetColumn(parm, 259, new int[1]);
        ok &= t.SetColumn(parm, 260, new int[1]);
        ok &= t.SetColumn(parm, 261, new byte[1]);
        for (uint id = 262; id <= 277; id++)
        {
            ok &= t.SetColumn(parm, id, new int[1]);
        }

        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
