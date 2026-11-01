using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 15 -> 16 (vehicle-classes.md §5, ADR-0039, задача VC1): классы ТС. Чистая функция над <see cref="SaveTables"/>.
/// Окно версии открыто до VC3 (как <c>w9</c> E1-E8). VC2 добавил столбцы <c>NSEG 17 Deny</c> и <c>NLAN 20 Deny</c> в схему w16 без кода миграции
/// (режим прототипа: файлы v15 с полосами <c>BusOnly</c> не пройдут проверку зеркала <c>BusOnly</c> = (<c>Deny == 0x1D</c>)); VC3 - порог <c>RulesEmergencyRedSpeed</c> = 4.17 м/с и два счётчика (0). В VC1 столбцов не добавляется: <c>PARM</c> типов <c>Category</c> шире
/// только по допустимым значениям (0-4), бит <c>VEHC.Flags</c> 128 проверяет валидатор - поведение мира из файла v15 прежнее, хэш тот же.
/// </summary>
internal static class MigrationV16
{
    public static LoadStatus Run(SaveTables t) => t == null ? LoadStatus.MigrationFailed : LoadStatus.Ok;
}
