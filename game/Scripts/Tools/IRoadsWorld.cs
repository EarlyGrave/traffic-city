using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity.Tools;

/// <summary>Сегмент под курсором: handle для команды, концы оси (центры узлов), профиль и ширина для подсветки.</summary>
public readonly record struct SegmentHit(EntityRef Ref, Vec2 A, Vec2 B, int Profile, bool Mirrored, RoadKind Kind, byte LanesAB, byte LanesBA, float WidthM);

/// <summary>
/// Что инструменту «Дороги» нужно от мира (реализует хост клиента): привязка к сетке и узлам, поиск сегмента, состояние денег и паузы.
/// Тонкая граница, чтобы сам инструмент оставался чистым C# и проверялся юнит-тестами с подставным миром.
/// </summary>
public interface IRoadsWorld
{
    RoadCatalog Catalog { get; }

    /// <summary>Снап к мировой сетке включён (<c>G</c>).</summary>
    bool SnapOn { get; }

    /// <summary>Ближайший узел сетки <c>module_m</c> (RP10, <c>GridSnap.Snap</c>).</summary>
    Vec2 SnapToGrid(Vec2 p);

    /// <summary>Живой узел сети в радиусе <paramref name="radius"/> от точки (ui-spec §5.1 п. 3: 6 м).</summary>
    bool TryNode(float x, float z, float radius, out Vec2 position);

    /// <summary>Ближайший живой сегмент, ось которого не дальше <paramref name="radius"/> от точки.</summary>
    bool TrySegment(float x, float z, float radius, out SegmentHit hit);

    /// <summary>Есть экономика (деньги в верхней полосе): цену показываем только тогда (ui-spec §6.2).</summary>
    bool HasBudget { get; }

    long Balance { get; }

    /// <summary>Время стоит: команды встанут в очередь и применятся после снятия паузы.</summary>
    bool Paused { get; }
}
