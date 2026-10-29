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

    // оверлеи по подписке (HostOverlays); длина - в *Count, ёмкость растёт редко
    internal byte[] LaneSignalsArr = Array.Empty<byte>();
    internal byte[] CrossingStatesArr = Array.Empty<byte>();
    internal ushort[] BusWaitingArr = Array.Empty<ushort>();
    internal SignalState[] SignalStatesArr = Array.Empty<SignalState>();
    internal uint[] NodeThroughputArr = Array.Empty<uint>();
    internal SegmentLoad[] SegmentLoadsArr = Array.Empty<SegmentLoad>();
    internal ushort[] PedEdgeCountsArr = Array.Empty<ushort>();
    internal RouteStep[] RouteStepsArr = Array.Empty<RouteStep>();

    internal int VehicleCount;
    internal int PedCount;
    internal int LaneSignalsCount;
    internal int CrossingStatesCount;
    internal int BusWaitingCount;
    internal int SignalStatesCount;
    internal int NodeThroughputCount;
    internal int SegmentLoadsCount;
    internal int PedEdgeCountsCount;
    internal int RouteStepsCount;

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

    /// <summary>Есть ли в мире пешеходы (секция <c>peds</c>) и их счётчики на тике кадра.</summary>
    public bool HasPeds { get; internal set; }

    public int ActivePedCount { get; internal set; }

    public int PedTarget { get; internal set; }

    /// <summary>Версии статики на тике кадра: при смене клиент пересобирает слои (сеть - из <see cref="Network"/>, остальное - под <c>AcquireExclusive</c>).</summary>
    public int LandUseVersion { get; internal set; }

    public int LotsVersion { get; internal set; }

    public int BuildingsVersion { get; internal set; }

    /// <summary>Копия сети на момент последней смены её версии (общая для кадров, неизменяемая); версия = <see cref="NetworkVersion"/>.</summary>
    public NetworkSnapshot Network { get; internal set; } = null!;

    public int NetworkVersion => Network.Version;

    /// <summary>
    /// Копия рельефа (terrain.md §10.1): null, если в мире нет рельефа. Пока <c>Terrain.Version</c> не менялась, это тот же объект, что в прошлом кадре;
    /// после изменения - новый снимок, делящий чистые чанки со старым. Неизменяема, главный поток держит ссылку сколько угодно.
    /// </summary>
    public TerrainSnapshot? Terrain { get; internal set; }

    /// <summary>Подписки, с которыми собран кадр (<see cref="HostOverlays"/>): оверлей вне подписки в кадре пуст.</summary>
    public HostOverlays Overlays { get; internal set; }

    /// <summary>Кадр содержит свежую копию тяжёлого оверлея (раз в <see cref="SimThreadHost.HeavyOverlayTicks"/> тиков либо после новой подписки).</summary>
    public bool SegmentLoadsFresh { get; internal set; }

    public bool PedEdgeCountsFresh { get; internal set; }

    /// <summary>Окно маршрута выбранной машины: <see cref="RouteVehicle"/> (Null - нет), <see cref="RouteCursor"/>, шаги в <see cref="RouteSteps"/>.</summary>
    public VehicleHandle RouteVehicle { get; internal set; }

    public int RouteCursor { get; internal set; }

    /// <summary>Пешеход, выбранный клиентом (<see cref="SimThreadHost.SetSelectedPed"/>) на момент сборки кадра, и его <see cref="PedInfo"/> (<see cref="SelectedPedValid"/>).</summary>
    public PedHandle SelectedPed { get; internal set; }

    public bool SelectedPedValid { get; internal set; }

    public PedInfo SelectedPedInfo { get; internal set; }

    /// <summary>Машина в инспекторе (<see cref="SimThreadHost.SetSelectedVehicle"/>) на момент сборки кадра и её <see cref="VehicleInfo"/> (<see cref="SelectedVehicleValid"/>).</summary>
    public VehicleHandle SelectedVehicle { get; internal set; }

    public bool SelectedVehicleValid { get; internal set; }

    public VehicleInfo SelectedVehicleInfo { get; internal set; }

    /// <summary>Метрики ожидания на светофорах (подписка <see cref="HostOverlays.SignalMetrics"/>); null, пока нет подписки.</summary>
    public SignalMetrics? SignalMetrics { get; internal set; }

    /// <summary>Видимые машины на <see cref="SnapshotTick"/>: компактно, только живые слоты; <c>Index</c>/<c>Generation</c> сохранены.</summary>
    public ReadOnlySpan<VehicleRenderData> Vehicles => new(CurArr, 0, VehicleCount);

    /// <summary>Те же слоты тиком раньше (по индексу совпадает с <see cref="Vehicles"/>; нули - слота не было). Интерполировать при совпадении <c>Generation</c>.</summary>
    public ReadOnlySpan<VehicleRenderData> PreviousVehicles => new(PrevArr, 0, VehicleCount);

    public ReadOnlySpan<PedestrianRenderData> Pedestrians => new(PedCurArr, 0, PedCount);

    public ReadOnlySpan<PedestrianRenderData> PreviousPedestrians => new(PedPrevArr, 0, PedCount);

    public ReadOnlySpan<byte> LaneSignals => new(LaneSignalsArr, 0, LaneSignalsCount);

    public ReadOnlySpan<byte> CrossingStates => new(CrossingStatesArr, 0, CrossingStatesCount);

    public ReadOnlySpan<ushort> BusStopWaiting => new(BusWaitingArr, 0, BusWaitingCount);

    public ReadOnlySpan<SignalState> SignalStates => new(SignalStatesArr, 0, SignalStatesCount);

    public ReadOnlySpan<uint> NodeThroughput => new(NodeThroughputArr, 0, NodeThroughputCount);

    /// <summary>Нагрузка сегментов; осмысленна при <see cref="SegmentLoadsFresh"/>.</summary>
    public ReadOnlySpan<SegmentLoad> SegmentLoads => new(SegmentLoadsArr, 0, SegmentLoadsCount);

    /// <summary>Пешеходов на рёбрах; осмысленна при <see cref="PedEdgeCountsFresh"/>.</summary>
    public ReadOnlySpan<ushort> PedEdgeCounts => new(PedEdgeCountsArr, 0, PedEdgeCountsCount);

    public ReadOnlySpan<RouteStep> RouteSteps => new(RouteStepsArr, 0, RouteStepsCount);
}
