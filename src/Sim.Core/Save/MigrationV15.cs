using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 14 -> 15 (save-format.md §4, §12; scenarios-mode.md §2.3, задача YS1): цели сценария. Чистая функция над <see cref="SaveTables"/>.
/// Секции <c>goals</c> в старых сейвах нет: блока <c>GOAL</c> нет, столбцов не добавляется - поведение мира из файла v14 прежнее, хэш тот же.
/// </summary>
internal static class MigrationV15
{
    public static LoadStatus Run(SaveTables t) => t == null ? LoadStatus.MigrationFailed : LoadStatus.Ok;
}
