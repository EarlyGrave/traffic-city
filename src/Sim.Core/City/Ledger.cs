using System;
using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Деньги города как проводки (zoning-demand.md §7.1): сумма балансов всех счетов равна 0 в каждый тик (CZ2). Счета: жители <c>[0, max_residents)</c>, здания
/// <c>[max_residents, +max_buildings)</c>, <c>External</c>, <c>City</c>. Балансы жителей и зданий лежат в их SoA, здесь - остальное состояние (<c>External</c>, <c>City</c>,
/// суммы потоков за сегодня/вчера/всего) и единственный способ менять баланс: <see cref="Transfer"/>. Z1 - состояние и проводка; правила потоков (§7.2-§7.4) - Z4.
/// </summary>
internal sealed class Ledger
{
    public const int FlowCount = 13;     // FlowKind.Endowment .. Subsidy

    public long External;
    public long City;

    public readonly long[] FlowDay = new long[FlowCount];        // за текущие сутки
    public readonly long[] FlowYesterday = new long[FlowCount];  // за прошлые сутки
    public readonly long[] FlowTotal = new long[FlowCount];      // с начала симуляции

    private readonly CityResidents _residents;
    private readonly BuildingStore _buildings;
    private readonly int _maxResidents;
    private readonly int _maxBuildings;

    public Ledger(CityResidents residents, BuildingStore buildings)
    {
        _residents = residents;
        _buildings = buildings;
        _maxResidents = residents.Capacity;
        _maxBuildings = buildings.Capacity;
    }

    public int ExternalAccount => _maxResidents + _maxBuildings;

    public int CityAccount => _maxResidents + _maxBuildings + 1;

    public static int AccountOfResident(int resident) => resident;

    public int AccountOfBuilding(int building) => _maxResidents + building;

    public long Balance(int account)
    {
        if (account < _maxResidents)
        {
            return _residents.Account[account];
        }

        if (account < _maxResidents + _maxBuildings)
        {
            return _buildings.Account[account - _maxResidents];
        }

        return account == ExternalAccount ? External : City;
    }

    /// <summary>Переводит <paramref name="amount"/> со счёта <paramref name="from"/> на <paramref name="to"/> и пишет сумму потока. Отрицательные балансы разрешены.</summary>
    public void Transfer(int from, int to, long amount, FlowKind kind)
    {
        if (amount == 0)
        {
            return;
        }

        Add(from, -amount);
        Add(to, amount);
        FlowDay[(int)kind] += amount;
        FlowTotal[(int)kind] += amount;
    }

    /// <summary>Граница суток: «сегодня» становится «вчера».</summary>
    public void RollDay()
    {
        Array.Copy(FlowDay, FlowYesterday, FlowCount);
        Array.Clear(FlowDay);
    }

    /// <summary>Σ балансов всех счетов (обязана быть 0). O(жителей + зданий).</summary>
    public long SumAll()
    {
        long s = External + City;
        for (int i = 0; i < _residents.HighWater; i++)
        {
            s += _residents.Account[i];
        }

        for (int i = 0; i < _buildings.HighWater; i++)
        {
            s += _buildings.Account[i];
        }

        return s;
    }

    private void Add(int account, long delta)
    {
        if (account < _maxResidents)
        {
            _residents.Account[account] += delta;
        }
        else if (account < _maxResidents + _maxBuildings)
        {
            _buildings.Account[account - _maxResidents] += delta;
        }
        else if (account == ExternalAccount)
        {
            External += delta;
        }
        else
        {
            City += delta;
        }
    }
}
