using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 18 -> 19 (age-groups.md §5, задача RA1): возрастные группы (PARM 316-323, RESD 51). Режим прототипа (CLAUDE.md,
/// решение владельца 2026-10-29) - сейвы без миграций: файл w18 не грузится (<see cref="LoadStatus.MigrationFailed"/>), корпус пересобирается.
/// </summary>
internal static class MigrationV19
{
    public static LoadStatus Run(SaveTables t) => LoadStatus.MigrationFailed;
}
