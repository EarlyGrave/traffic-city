namespace Sim.Core.Routing;

/// <summary>
/// Производные масок доступа маршрутизации (vehicle-classes.md §2.3, ADR-0039): <c>RouteGraph.DirSegDeny</c>/<c>DenyUnion</c> и <c>Overlay.CellDeny</c>.
/// Пересчёт - после правки масок полос и улиц (<c>SetLaneDeny</c>, <c>SetStreetAccess</c>); после структурных правок сети их пересчитывают сами
/// <c>RouteGraph.Rebuild</c> и перестройки оверлея. Главный поток, вне параллельных фаз, без аллокаций. Новые маски действуют на новые запросы.
/// </summary>
internal static class AccessMasks
{
    public static void Refresh(World w)
    {
        w.Graph.RecalcDeny();
        Overlay? ov = w.Overlay;
        if (ov != null && !ov.Disabled)
        {
            ov.RecalcCellDeny();
        }
    }
}
