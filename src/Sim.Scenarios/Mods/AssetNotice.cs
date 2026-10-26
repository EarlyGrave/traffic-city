using System.Globalization;

namespace Sim.Scenarios.Mods;

/// <summary>Что случилось с ассетом из сейва относительно установленных пакетов (modding.md §6.1).</summary>
public enum AssetNoticeState : byte
{
    /// <summary>Нет в каталоге: клиент рисует заглушку «?», симуляция идёт по параметрам из сейва.</summary>
    Missing = 1,

    /// <summary><c>ContentHash</c> другой: действуют параметры из сейва.</summary>
    Changed = 2,

    /// <summary>Новые параметры применены к новым спавнам (M5).</summary>
    Updated = 3,

    /// <summary>Установлена другая MAJOR-версия пакета; пакет загружен, сверка идёт по <c>ContentHash</c>.</summary>
    VersionChanged = 4,
}

/// <summary>
/// Событие хоста (не <c>SimEvent</c> ядра: ядро не знает каталога): вид записи манифеста, id, состояние и число затронутых
/// сущностей (машин данного типа; для пакетов 0). Сейв при этом не ломается.
/// </summary>
public readonly record struct AssetNotice(Sim.Core.Api.AssetKind Kind, string Id, AssetNoticeState State, int Count)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{State.ToString().ToLowerInvariant()} {Kind} {Id} (entities {Count})");
}
