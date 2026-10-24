using BenchmarkDotNet.Attributes;
using Sim.Core.Agents;
using Sim.Core.Api;

namespace Sim.Bench;

/// <summary>Вставка/удаление в полосу с 40 машинами (городской сегмент, core-api 6.4).</summary>
[MemoryDiagnoser]
public class LaneOccupancyBenchmarks
{
    private const int Cars = 40;
    private const int Lane = 0;

    private VehicleStore _store = null!;
    private LaneOccupancy _occ = null!;
    private int _probe;
    private float _mid;

    [GlobalSetup]
    public void Setup()
    {
        _store = new VehicleStore(Cars + 1);
        _occ = new LaneOccupancy(_store, 2);
        for (int k = 0; k < Cars; k++)
        {
            _store.TrySpawn(out VehicleHandle h);
            _store.S[h.Index] = 400f - (k * 10f); // по убыванию: PushTail даёт порядок
            _occ.PushTail(Lane, h.Index);
        }

        _store.TrySpawn(out VehicleHandle p);
        _probe = p.Index;
        _mid = 205;
    }

    /// <summary>Вставка в середину (поиск от хвоста, ~20 шагов) + удаление.</summary>
    [Benchmark]
    public void InsertSortedMiddle_Unlink()
    {
        _store.S[_probe] = _mid;
        _occ.InsertSorted(Lane, _probe);
        _occ.Unlink(_probe);
    }

    /// <summary>Въезд на полосу с начала (O(1)) + удаление.</summary>
    [Benchmark]
    public void PushTail_Unlink()
    {
        _store.S[_probe] = 0f;
        _occ.PushTail(Lane, _probe);
        _occ.Unlink(_probe);
    }

    /// <summary>Поиск соседей для MOBIL на полосе из 40 машин (середина).</summary>
    [Benchmark]
    public int FindNeighboursMiddle()
    {
        _occ.FindNeighbours(Lane, _mid, out int ahead, out int behind);
        return ahead + behind;
    }

    /// <summary>Смена полосы: BeginLaneChange (в пустую соседнюю полосу) + снятие ghost + возврат.</summary>
    [Benchmark]
    public void LaneChange_GhostCycle()
    {
        int car = _occ.LaneHead[Lane];
        _occ.BeginLaneChange(car, 1, -1, -1);
        _occ.GhostUnlink(car);
        _occ.Unlink(car);
        _occ.PushHead(Lane, car);
    }
}
