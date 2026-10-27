using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 5 -> 6 (save-format.md §4, §12; ADR-0028): радиус скругления построителя сети. Чистая функция над <see cref="SaveTables"/>.
/// Добавляет в <c>PARM</c> столбец 185 <c>NetCornerRadius = 0</c> - «фиксированный отступ 7 м», с которым построена сеть файла: дороги, построенные после загрузки,
/// получают ту же геометрию, что и до ADR-0028. Новых блоков не создаёт; поведение и хэш не меняются, мир из файла w1-w5 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV6
{
    private static readonly float[] _off = { 0f };

    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        if (parm < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        return t.SetColumn(parm, 185, _off) ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
