using System;
using Sim.Core.Api;

namespace Sim.Host;

/// <summary>
/// Кадр симуляции для главного потока (ADR-0024 п. 3): собственные массивы, не память ядра. Владелец кадра - главный поток от
/// <see cref="SimThreadHost.TryAcquireFrame"/> до следующего вызова; поток симуляции его не трогает.
/// </summary>
public sealed class SimFrame
{
    internal VehicleRenderData[] CurArr = Array.Empty<VehicleRenderData>();
    internal VehicleRenderData[] PrevArr = Array.Empty<VehicleRenderData>();
    internal PedestrianRenderData[] PedCurArr = Array.Empty<PedestrianRenderData>();
    internal PedestrianRenderData[] PedPrevArr = Array.Empty<PedestrianRenderData>();

    /// <summary>Номер публикации (с 1); растёт на каждый опубликованный кадр.</summary>
    public long Sequence { get; internal set; }

    /// <summary>Тик, которому соответствуют <see cref="Vehicles"/>/<see cref="Pedestrians"/> (= <c>Simulation.SnapshotTick</c>).</summary>
    public long SnapshotTick { get; internal set; }

    /// <summary>Номер вида ядра (<c>Simulation.ViewVersion</c>), отражённого в кадре.</summary>
    public int ViewVersion { get; internal set; }

    public int ActiveVehicleCount { get; internal set; }

    public SimStats Stats { get; internal set; }

    /// <summary>Сколько микросекунд ушло на последний <c>Step</c> (пачка до <c>MaxStepBatch</c> тиков) и сколько в ней тиков.</summary>
    public long LastStepMicros { get; internal set; }

    public int LastStepTicks { get; internal set; }

    /// <summary>Байты, аллоцированные потоком симуляции с начала его работы (для HUD «alloc/tick»).</summary>
    public long SimThreadAllocatedBytes { get; internal set; }

    internal int VehicleCount;
    internal int PedCount;

    /// <summary>Видимые машины на <see cref="SnapshotTick"/>: компактно, только живые слоты; <c>Index</c>/<c>Generation</c> сохранены.</summary>
    public ReadOnlySpan<VehicleRenderData> Vehicles => new(CurArr, 0, VehicleCount);

    /// <summary>Те же слоты тиком раньше (по индексу совпадает с <see cref="Vehicles"/>; нули - слота не было). Интерполировать при совпадении <c>Generation</c>.</summary>
    public ReadOnlySpan<VehicleRenderData> PreviousVehicles => new(PrevArr, 0, VehicleCount);

    public ReadOnlySpan<PedestrianRenderData> Pedestrians => new(PedCurArr, 0, PedCount);

    public ReadOnlySpan<PedestrianRenderData> PreviousPedestrians => new(PedPrevArr, 0, PedCount);
}
