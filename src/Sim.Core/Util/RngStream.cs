namespace Sim.Core.Util;

/// <summary>Номера независимых подпотоков PCG32 (core-api §8). Значения фиксированы навсегда.</summary>
public enum RngStream : ulong
{
    CityGen = 1,
    Zones = 2,
    Spawn = 3,
    Behaviour = 4,
    Transit = 5,
    Routing = 6,
    Peds = 7,
    Demand = 8,   // 3.2, ADR-0011: Create города, события жителей по (tick, ключ), дневные проходы по индексу (zoning-demand.md §18)
}
