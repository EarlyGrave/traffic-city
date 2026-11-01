using Sim.Core.Api;

namespace Sim.Core.Network;

/// <summary>
/// Правка полос сегмента целиком (T5, player-tools.md §4.5): применение <see cref="NetworkBuilder.ReshapeSegment"/> с пересчётом производных и участков вдоль дороги, проверка «можно ли
/// сейчас» на занятом сегменте и финал операции <see cref="ToolOpKind.ShrinkLanes"/> (фаза 1а). Занятый сегмент: машины уцелевших полос переносятся (<see cref="Relocate"/>), убираемые полосы
/// сначала опустошаются (<c>Closed | Draining</c>), затем закрываются и пустеют обречённые connector'ы концов - и только тогда блок полос заменяется.
/// </summary>
internal static class ReshapeEdit
{
    /// <summary>
    /// Готов ли сегмент к правке сейчас: на убираемых полосах нет машин, и перестройка узлов концов не уничтожит занятый connector. <paramref name="remaining"/> - сколько ещё ждать
    /// (машины на убираемых полосах + занятые обречённые connector'ы). <paramref name="closeDoomed"/> - закрыть обречённые connector'ы для въезда, когда полосы уже пусты (фаза 1а).
    /// Не-Ok - правка невозможна в нынешней сети (лимит узла, ёмкость).
    /// </summary>
    public static NetworkStatus Ready(World w, int seg, int lanesAB, int lanesBA, RoadKind kind, bool closeDoomed, out int remaining)
    {
        NetworkBuilder b = w.Builder;
        remaining = b.VanishingOccupancy(w, seg, lanesAB, lanesBA);
        ref readonly SegmentData sd = ref w.Net.Segments[seg];
        bool swap = sd.LanesAB != lanesAB || sd.LanesBA != lanesBA;
        if (swap && !b.SwapFits(seg, lanesAB, lanesBA))
        {
            return NetworkStatus.LanesFull;
        }

        NetworkStatus st = b.PlanReshapeEnds(w, seg, lanesAB, lanesBA, kind, closeDoomed && remaining == 0, out int busy);
        remaining += busy;
        return st;
    }

    /// <summary>Применяет правку (предусловия проверены: <see cref="Ready"/> с нулём) и пересчитывает граф, оверлей, пешеходов и участки вдоль дороги.</summary>
    public static NetworkStatus Apply(World w, int seg, ReshapeMode mode, int profile, bool mirrored, int lanesAB, int lanesBA, float speed, RoadKind kind, uint tag)
    {
        NetworkStatus st = w.Builder.ReshapeSegment(w, seg, mode, profile, mirrored, lanesAB, lanesBA, speed, kind, tag);
        if (mode == ReshapeMode.Speed)
        {
            NetworkEdit.RefreshDerived(w, pedsChanged: false);
            return st;
        }

        NetworkEdit.RefreshDerived(w);
        if (w.City != null)
        {
            int lost = w.City.LotGen.OnRoadsChanged(w, w.City);
            if (lost > 0)
            {
                w.Emit(SimEventKind.ZoneLotsLost, tag, seg, lost);
            }

            w.City.LotGen.RestoreDerived(w);
        }

        return st;
    }

    /// <summary>
    /// Финал <see cref="ToolOpKind.ShrinkLanes"/> записи <paramref name="k"/> (фаза 1а): 0 - правка сделана (запись удалена), иначе сколько ещё ждать (машины на убираемых полосах и на
    /// обречённых connector'ах; 1 - если правка сейчас невозможна по другой причине, например ёмкость).
    /// </summary>
    public static int TryFinish(World w, int k)
    {
        ToolOps ops = w.Ops;
        int seg = ops.TargetIndex[k];
        ToolOps.UnpackReshape(ops.TargetGen[k], out int lanesAB, out int lanesBA, out RoadKind kind, out int profile, out bool mirrored);
        NetworkStatus st = Ready(w, seg, lanesAB, lanesBA, kind, closeDoomed: true, out int remaining);
        if (st != NetworkStatus.Ok)
        {
            return System.Math.Max(1, remaining);
        }

        if (remaining > 0)
        {
            return remaining;
        }

        float speed = w.Net.Segments[seg].SpeedLimit;
        ReshapeMode mode = w.Builder.PlanReshape(seg, profile, mirrored, lanesAB, lanesBA, speed, kind);
        int opId = ops.Id[k];
        long ticks = w.Tick - ops.StartTick[k];
        ops.RemoveAt(k);
        NetworkStatus done = Apply(w, seg, mode, profile, mirrored, lanesAB, lanesBA, speed, kind, 0);
        System.Diagnostics.Debug.Assert(done == NetworkStatus.Ok, "правка полос после проверки готовности");
        w.Work.ToolOpsFinished++;
        if (ticks > w.Work.DrainTicksMax)
        {
            w.Work.DrainTicksMax = ticks;
        }

        w.Emit(SimEventKind.ToolOpFinished, 0, opId, (int)CommandKind.UpgradeRoad);
        return 0;
    }
}
