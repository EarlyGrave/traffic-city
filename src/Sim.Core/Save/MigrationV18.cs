using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 17 -> 18 (services.md §5, задача SR1): службы (блоки SVCR, SERV; очередь и бригады ЖКХ ушли из LIFE). Режим прототипа (CLAUDE.md,
/// решение владельца 2026-10-29) - сейвы без миграций: файл w17 не грузится (<see cref="LoadStatus.MigrationFailed"/>), корпус пересобирается. Окно w18 открыто до SR5.
/// </summary>
internal static class MigrationV18
{
    public static LoadStatus Run(SaveTables t) => LoadStatus.MigrationFailed;
}
