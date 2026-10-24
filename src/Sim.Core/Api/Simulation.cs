using System;
using Sim.Core.Metrics;
using Sim.Core.Network;

namespace Sim.Core.Api;

/// <summary>
/// Фасад ядра (core-api 3): единственная публичная точка входа. Однопоточный; снапшоты (спаны) валидны до следующего
/// <see cref="Step"/>. После <see cref="Dispose"/> любой вызов - no-op (спаны пусты, запросы возвращают false).
/// </summary>
public sealed class Simulation : IDisposable
{
    public const float Dt = 0.05f;
    public const int TicksPerSecond = 20;

    private readonly World _w;
    private bool _disposed;

    private Simulation(World world)
    {
        _w = world;
    }

    // ---- жизненный цикл ----

    /// <summary>Валидирует desc, выделяет ВСЕ буферы, строит сеть. Единственное место с исключениями.</summary>
    /// <exception cref="ArgumentException">desc не прошёл валидацию (см. <see cref="CreateStatus"/>).</exception>
    public static Simulation Create(in ScenarioDesc desc)
    {
        CreateStatus st = TryCreate(in desc, out Simulation? sim);
        if (st != CreateStatus.Ok || sim == null)
        {
            throw new ArgumentException($"ScenarioDesc rejected: {st}", nameof(desc));
        }

        return sim;
    }

    /// <summary>То же без исключений (FFI, хост с кодом ошибки).</summary>
    public static CreateStatus TryCreate(in ScenarioDesc desc, out Simulation? sim)
    {
        sim = null;
        CreateStatus st = WorldBuilder.Create(in desc, out World? w);
        if (st == CreateStatus.Ok && w != null)
        {
            sim = new Simulation(w);
        }

        return st;
    }

    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>Внутренний доступ для тестов и бенчей (InternalsVisibleTo).</summary>
    internal World World => _w;

    // ---- управление ----

    /// <summary>Число выполненных тиков с момента Create.</summary>
    public long Tick => _disposed ? 0 : _w.Tick;

    /// <summary>Выполняет §7 <paramref name="ticks"/> раз. <c>ticks &lt; 1</c> - no-op. События накапливаются за весь вызов.</summary>
    public void Step(int ticks)
    {
        if (_disposed || ticks < 1)
        {
            return;
        }

        World w = _w;
        w.EventCount = 0;
        w.EventsOverflowed = false;
        for (int t = 0; t < ticks; t++)
        {
            PhaseList.RunTick(w);
        }
    }

    /// <summary>false = очередь полна (команда отброшена, <c>CommandsDropped++</c>) либо симуляция освобождена.</summary>
    public bool Enqueue(in Command cmd)
    {
        if (_disposed)
        {
            return false;
        }

        if (_w.Commands.TryEnqueue(in cmd))
        {
            return true;
        }

        _w.Counters.CommandsDropped++;
        return false;
    }

    public int PendingCommands => _disposed ? 0 : _w.Commands.Count;

    // ---- снапшоты ----

    /// <summary>Тик, которому соответствует <see cref="Vehicles"/> (после каждого Step равен <see cref="Tick"/>).</summary>
    public long SnapshotTick => _disposed ? 0 : _w.SnapshotTick;

    /// <summary>Длина = HighWater пула на SnapshotTick (до первого Step пусто), индекс = слот; неживые слоты - нули.</summary>
    public ReadOnlySpan<VehicleRenderData> Vehicles => _disposed ? default : SnapshotSpan((int)(_w.SnapshotTick & 1));

    /// <summary>Состояние на SnapshotTick - 1 (после первого Step пусто); длина не больше <see cref="Vehicles"/>.</summary>
    public ReadOnlySpan<VehicleRenderData> PreviousVehicles => _disposed ? default : SnapshotSpan((int)((_w.SnapshotTick + 1) & 1));

    private ReadOnlySpan<VehicleRenderData> SnapshotSpan(int buffer) => new(_w.Render[buffer], 0, _w.SnapshotSlots[buffer]);

    public int ActiveVehicleCount => _disposed ? 0 : _w.Vehicles.AliveCount;

    public ReadOnlySpan<SegmentLoad> SegmentLoads => _disposed ? default : _w.SegmentLoads;

    public ReadOnlySpan<SignalState> SignalStates =>
        _disposed ? default : new ReadOnlySpan<SignalState>(_w.SignalStates, 0, _w.SignalCount);

    public ReadOnlySpan<byte> LaneSignals => _disposed ? default : _w.LaneSignals;

    /// <summary>Окно маршрута выбранной машины (срез массива окон; впереди ≤ 8 сегментов, хвост плана наружу не отдаётся); пуст, если ничего не выбрано или маршрута нет.</summary>
    public RouteView SelectedRoute
    {
        get
        {
            if (_disposed || !_w.Vehicles.IsAlive(_w.Selected))
            {
                return default;
            }

            int i = _w.Selected.Index;
            int route = _w.Vehicles.Route[i];
            if (route < 0)
            {
                return default;
            }

            var rh = new RouteHandle(route, _w.Vehicles.RouteGen[i]);
            ReadOnlySpan<RouteStep> steps = _w.Routes.GetWindow(rh);
            return steps.IsEmpty ? default : new RouteView(_w.Selected, steps, _w.Vehicles.RouteCursor[i]);
        }
    }

    public NetworkView Network => _disposed ? default : _w.Net.GetView();

    /// <summary>События последнего Step (буфер очищается в его начале).</summary>
    public ReadOnlySpan<SimEvent> Events => _disposed ? default : new ReadOnlySpan<SimEvent>(_w.Events, 0, _w.EventCount);

    // ---- метрики ----

    public ref readonly SimCounters Counters
    {
        get
        {
            // После Dispose счётчики остаются читаемыми (мир не освобождается явно, GC соберёт).
            return ref _w.Counters;
        }
    }

    public void GetStats(out SimStats stats)
    {
        ref readonly SimCounters c = ref _w.Counters;
        stats = default;
        if (c.VehicleTicksTotal > 0)
        {
            stats.AvgSpeedKmh = (float)(c.DistanceTotalMm / 1000.0 / (c.VehicleTicksTotal * (double)Dt) * 3.6);
        }

        if (c.TripsCompleted > 0)
        {
            stats.AvgTripTimeS = (float)(c.TripTimeTicksTotal * (double)Dt / c.TripsCompleted);
        }

        if (c.Ticks > 0)
        {
            stats.TripsCompletedPerMin = (float)(c.TripsCompleted / (c.Ticks * (double)Dt / 60.0));
        }

        stats.StuckVehicles = c.StuckVehicles;
        stats.GridlockFlag = c.GridlockFlag;
        if (c.BusHeadwaySamples > 0)
        {
            stats.BusHeadwayDeviationS = (float)(c.BusHeadwayDeviationTicksTotal * (double)Dt / c.BusHeadwaySamples);
        }

        if (c.VehicleTicksTotal > 0)
        {
            // Жёстких клэмпов на машино-минуту.
            stats.HardClampsPerVehicleMin = (float)(c.HardClamps / (c.VehicleTicksTotal * (double)Dt / 60.0));
        }
    }

    /// <summary>Проехавших узел машин (всего), длина MaxNodes.</summary>
    public ReadOnlySpan<uint> NodeThroughput => _disposed ? default : _w.NodeThroughput;

    public ReadOnlySpan<BusRouteStats> BusRouteStats => _disposed ? default : _w.BusRouteStats;

    // ---- детерминизм ----

    /// <summary>FNV-1a 64 по состоянию (core-api 9). O(N), не вызывать каждый тик в проде.</summary>
    public ulong ComputeStateHash() => _disposed ? 0UL : StateHash.Compute(_w);

    // ---- запросы ----

    public bool IsAlive(VehicleHandle h) => !_disposed && _w.Vehicles.IsAlive(h);

    public bool TryGetVehicleInfo(VehicleHandle h, out VehicleInfo info)
    {
        info = default;
        if (_disposed || !_w.Vehicles.IsAlive(h))
        {
            return false;
        }

        Agents.VehicleStore v = _w.Vehicles;
        int i = h.Index;
        info = new VehicleInfo
        {
            Lane = v.Lane[i],
            S = v.S[i],
            Speed = v.Speed[i],
            Accel = v.Accel[i],
            RouteCursor = v.RouteCursor[i],
            DestZone = v.DestZone[i],
            Flags = (byte)SnapshotPhase.RenderFlags(_w, i),
            Type = v.Type[i],
            SpawnTick = v.SpawnTick[i],
        };
        return true;
    }

    /// <summary>Ближайшая к точке (в радиусе) живая машина по текущему снапшоту; O(N) по живым.</summary>
    public bool TryPickVehicle(float x, float z, float radius, out VehicleHandle h)
    {
        h = VehicleHandle.Null;
        if (_disposed || !(radius >= 0f))
        {
            return false;
        }

        ReadOnlySpan<VehicleRenderData> snap = Vehicles;
        float best = radius * radius;
        int bestIndex = -1;
        for (int i = 0; i < snap.Length; i++)
        {
            if ((snap[i].Flags & (byte)VehicleRenderFlags.Active) == 0)
            {
                continue;
            }

            float dx = snap[i].X - x;
            float dz = snap[i].Z - z;
            float d = (dx * dx) + (dz * dz);
            if (d <= best)
            {
                best = d;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        h = _w.Vehicles.HandleOf(bestIndex);
        return true;
    }
}
