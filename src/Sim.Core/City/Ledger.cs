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
    public const int FlowCount = 23;     // FlowKind.Endowment .. ServiceUpkeep (city-economy.md §2.1, building-lifecycle.md §2.10, services.md §2.4)
    public const int LifecycleFlowCount = 22;   // виды 17..21 - в хэше режима city.lifecycle; 22 (ServiceUpkeep) - только в режиме city.services
    public const int LegacyFlowCount = 13;   // виды 0..12 (3.2): только они входят в хэш без бюджета
    public const int BudgetFlowCount = 17;   // виды 0..16 (4.2): в хэше бюджета; 17..21 - только с режимом city.lifecycle
    public const int Bp = 10_000;        // знаменатель ставок: базисные пункты

    public long External;
    public long City;
    public long Utility;   // фонд ЖКХ (building-lifecycle.md §2.10); вне режима city.lifecycle всегда 0

    public readonly long[] FlowDay = new long[FlowCount];        // за текущие сутки
    public readonly long[] FlowYesterday = new long[FlowCount];  // за прошлые сутки
    public readonly long[] FlowTotal = new long[FlowCount];      // с начала симуляции

    /// <summary>Перенос остатков <see cref="TransferPct"/> по виду потока (<c>CityBudget.Carry</c>); null - бюджета нет, сбор округляется вниз без переноса.</summary>
    public long[]? Carry;

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

    public int UtilityAccount => _maxResidents + _maxBuildings + 2;

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

        return account == ExternalAccount ? External : account == CityAccount ? City : Utility;
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

    /// <summary>
    /// Пропорциональный сбор (city-economy.md §2.1): <c>amount = base * rateBp / 10 000</c> с глобальным переносом остатка по виду потока
    /// (<c>Carry[kind] += base * rate; amount = Carry[kind] / D; Carry[kind] -= amount * D</c>); переводит <c>amount</c> со счёта <paramref name="from"/> на <paramref name="to"/>
    /// и возвращает его. Сумма за любой период точна до одного <c>D</c>. <paramref name="baseAmount"/> и <paramref name="rateBp"/> не отрицательны.
    /// </summary>
    public long TransferPct(int from, int to, long baseAmount, int rateBp, FlowKind kind)
    {
        System.Diagnostics.Debug.Assert(baseAmount >= 0 && rateBp >= 0, "TransferPct: отрицательная база или ставка");
        long product = baseAmount * rateBp;
        long amount;
        long[]? carry = Carry;
        if (carry == null)
        {
            amount = product / Bp;
        }
        else
        {
            long acc = carry[(int)kind] + product;
            amount = acc / Bp;
            carry[(int)kind] = acc - (amount * Bp);
        }

        Transfer(from, to, amount, kind);
        return amount;
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
        long s = External + City + Utility;
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
        else if (account == CityAccount)
        {
            City += delta;
        }
        else
        {
            Utility += delta;
        }
    }
}
