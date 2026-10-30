using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Здания (zoning-demand.md §2.3): SoA с ёмкостью <c>max_buildings</c> и <see cref="HandlePool"/> (index + generation, LIFO free-list, ADR-0023 §3).
/// Мёртвые слоты в сейве - канон (ссылки -1, остальное 0). Накопители «сегодня/вчера» лежат парами: индекс <c>(i &lt;&lt; 1) | (day &amp; 1)</c>.
/// </summary>
internal sealed class BuildingStore
{
    public readonly HandlePool Pool;

    public readonly int[] Lot;
    public readonly int[] Type;
    public readonly byte[] State;          // BuildingState
    public readonly long[] StateTick;
    public readonly int[] Occupants;       // R: жителей; C/I: работников
    public readonly int[] Jobs;            // C/I: открытые места (<= Capacity)
    public readonly int[] HeadRes;         // голова списка жителей (CityResidents.NextHome) или -1
    public readonly int[] HeadWork;        // голова списка работников (CityResidents.NextWork) или -1
    public readonly long[] Account;
    public readonly long[] RevenueEwma;
    public readonly long[] ProfitEwma;
    public readonly long[] PayrollEwma;    // сглаженный фонд оплаты фирмы (city-economy.md §2.2, §7.2): буфер дивидендов; без бюджета 0
    public readonly int[] VisitsDay;       // [2 * Capacity]
    public readonly long[] RevenueDay;     // [2 * Capacity]
    public readonly long[] CostDay;        // [2 * Capacity]
    public readonly short[] LossDays;
    public readonly short[] VacantDays;

    // ---- жизнь здания (building-lifecycle.md §2.2, §3): только в режиме city.lifecycle, иначе нули ----
    public readonly ushort[] Cond;         // состояние, б.п. (10 000 = 100 %)
    public readonly ushort[] Ceil;         // потолок состояния, б.п.
    public readonly int[] BornDay;         // игровые сутки создания
    public readonly byte[] LifeFlags;      // BuildingLifeFlags

    /// <summary>Версия данных для клиента (<c>BuildingsVersion</c>): растёт при создании, смене состояния и сносе.</summary>
    public int Version;

    public BuildingStore(int capacity)
    {
        Pool = new HandlePool(capacity);
        Lot = new int[capacity];
        Type = new int[capacity];
        State = new byte[capacity];
        StateTick = new long[capacity];
        Occupants = new int[capacity];
        Jobs = new int[capacity];
        HeadRes = new int[capacity];
        HeadWork = new int[capacity];
        Account = new long[capacity];
        RevenueEwma = new long[capacity];
        ProfitEwma = new long[capacity];
        PayrollEwma = new long[capacity];
        VisitsDay = new int[2 * capacity];
        RevenueDay = new long[2 * capacity];
        CostDay = new long[2 * capacity];
        LossDays = new short[capacity];
        VacantDays = new short[capacity];
        Cond = new ushort[capacity];
        Ceil = new ushort[capacity];
        BornDay = new int[capacity];
        LifeFlags = new byte[capacity];
        for (int i = 0; i < capacity; i++)
        {
            ResetSlot(i);
        }
    }

    public int Capacity => Pool.Capacity;

    public int HighWater => Pool.HighWater;

    public int AliveCount => Pool.AliveCount;

    public bool IsAlive(int index) => Pool.IsAlive(index);

    /// <summary>Создаёт здание на участке. false - ёмкость исчерпана (вызывающий считает отказ).</summary>
    public bool TryCreate(int lot, int type, BuildingState state, long tick, out int index)
    {
        if (!Pool.TryAllocate(out index, out _))
        {
            index = -1;
            return false;
        }

        ResetSlot(index);
        Lot[index] = lot;
        Type[index] = type;
        State[index] = (byte)state;
        StateTick[index] = tick;
        Version++;
        return true;
    }

    /// <summary>Освобождает слот (списки жителей и работников к этому моменту пусты).</summary>
    public void Destroy(int index)
    {
        ResetSlot(index);
        Pool.Free(index);
        Version++;
    }

    private void ResetSlot(int i)
    {
        Lot[i] = -1;
        Type[i] = 0;
        State[i] = 0;
        StateTick[i] = 0;
        Occupants[i] = 0;
        Jobs[i] = 0;
        HeadRes[i] = -1;
        HeadWork[i] = -1;
        Account[i] = 0;
        RevenueEwma[i] = 0;
        ProfitEwma[i] = 0;
        PayrollEwma[i] = 0;
        VisitsDay[2 * i] = 0;
        VisitsDay[(2 * i) + 1] = 0;
        RevenueDay[2 * i] = 0;
        RevenueDay[(2 * i) + 1] = 0;
        CostDay[2 * i] = 0;
        CostDay[(2 * i) + 1] = 0;
        LossDays[i] = 0;
        VacantDays[i] = 0;
        Cond[i] = 0;
        Ceil[i] = 0;
        BornDay[i] = 0;
        LifeFlags[i] = 0;
    }
}
