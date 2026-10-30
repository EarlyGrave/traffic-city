using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 16 -> 17 (building-lifecycle.md §5, задача BL1): жизнь здания. Режим прототипа (CLAUDE.md, решение владельца 2026-10-29) - сейвы без миграций:
/// файл w16 не грузится (<see cref="LoadStatus.MigrationFailed"/>), корпус пересобирается. Окно w17 открыто до BL4.
/// </summary>
internal static class MigrationV17
{
    public static LoadStatus Run(SaveTables t) => LoadStatus.MigrationFailed;
}
