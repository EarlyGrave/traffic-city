using System;
using Sim.Core.Api;
using Sim.Host;
using TrafficCity.Tools;

namespace TrafficCity;

/// <summary>
/// Мир инструмента «Зоны» (<see cref="IZonesWorld"/>): копия участков и сетка из последнего кадра хоста. Участки читаются из ядра только под эксклюзивным доступом
/// (ADR-0024 п. 5), поэтому копия обновляется <see cref="Refresh"/> при смене <c>LotsVersion</c> (редкое событие: покраска, дорога), а не каждый кадр.
/// </summary>
public sealed class ZonesWorld : IZonesWorld
{
    private readonly Func<SimFrame?> _frame;
    private readonly Func<SimThreadHost?> _host;
    private readonly Func<bool> _hasCity;
    private readonly Func<bool> _paused;
    private LotRenderData[] _lots = Array.Empty<LotRenderData>();
    private int _count;
    private int _version = -1;

    public ZonesWorld(Func<SimFrame?> frame, Func<SimThreadHost?> host, Func<bool> hasCity, Func<bool> paused, long maxPaintM2)
    {
        _frame = frame;
        _host = host;
        _hasCity = hasCity;
        _paused = paused;
        MaxPaintM2 = maxPaintM2 > 0 ? maxPaintM2 : 250_000;
    }

    public bool HasCity => _hasCity();

    public bool Paused => _paused();

    public long MaxPaintM2 { get; }

    public float CellM => (_frame()?.Network.View.ModuleMm ?? 0) * 0.001f;

    public float GridOriginX => _frame()?.Network.View.GridOriginX ?? 0f;

    public float GridOriginZ => _frame()?.Network.View.GridOriginZ ?? 0f;

    /// <summary>Подтянуть копию участков, если версия в кадре новее (главный поток, раз в кадр, пока инструмент активен).</summary>
    public void Refresh()
    {
        SimFrame? f = _frame();
        SimThreadHost? host = _host();
        if (f == null || host == null || !HasCity || f.LotsVersion == _version)
        {
            return;
        }

        using ExclusiveAccess ex = host.AcquireExclusive();
        Simulation? sim = ex.Simulation;
        if (sim == null)
        {
            return;
        }

        int n = sim.LotCount;
        if (_lots.Length < n)
        {
            _lots = new LotRenderData[Math.Max(n, _lots.Length * 2)];
        }

        _count = sim.GetLots(_lots.AsSpan(0, n));
        _version = sim.LotsVersion;
    }

    public bool TryGetLots(out ReadOnlySpan<LotRenderData> lots, out int lotsVersion)
    {
        lots = _lots.AsSpan(0, _count);
        lotsVersion = _version;
        return _version >= 0;
    }
}
