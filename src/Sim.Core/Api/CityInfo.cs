using System.Runtime.InteropServices;

namespace Sim.Core.Api;

// Публичные POD-типы чтения города (zoning-demand.md §13). Раскладка фиксирована: клиент и Rust-порт читают их как есть.

/// <summary>Состояние здания (<c>Building.State</c>, zoning-demand.md §2.3). Числа лежат в сейве.</summary>
public enum BuildingState : byte
{
    Constructing = 0,
    Active = 1,
    Abandoned = 2,
}

/// <summary>Состояние жителя (zoning-demand.md §3.3). Числа лежат в сейве.</summary>
public enum ResidentState : byte
{
    AtHome = 0,
    AtWork = 1,
    AtShop = 2,
    WaitDepart = 3,
    InLeg = 4,
}

/// <summary>Какое поле доступности отдаёт <see cref="Simulation.GetAccessibility"/> (zoning-demand.md §6.7).</summary>
public enum AccessKind : byte
{
    Shops = 0,      // LS_C: logsum до C-целей
    Jobs = 1,       // LS_jobs: logsum до рабочих мест
}

/// <summary>Вид потока денег (zoning-demand.md §7.1). Числа лежат в сейве; новые - только в конец.</summary>
public enum FlowKind : byte
{
    Endowment = 0,
    Wage = 1,
    Purchase = 2,
    Goods = 3,
    Import = 4,
    Export = 5,
    Emigration = 6,
    Construction = 7,
    Tax = 8,            // резерв 4.2
    Rent = 9,
    Fare = 10,
    Maintenance = 11,
    Subsidy = 12,
}

/// <summary>Здание для клиента: 24 байта (zoning-demand.md §13). Индекс в массиве = слот пула зданий; мёртвый слот - <c>Kind = 0</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
public struct BuildingRenderData
{
    public float X, Z;           // 0..7   центр участка, м
    public float Heading;        // 8..11  рад: фасадом к дороге
    public ushort Type;          // 12..13 строка таблицы типов
    public byte Kind;            // 14     LotZone
    public byte State;           // 15     BuildingState
    public byte FillPct;         // 16     заполнение 0..100 (жители / работники к вместимости); Constructing - прогресс стройки
    public byte R0;              // 17
    public ushort R1;            // 18..19
    public int Gen;              // 20..23 поколение слота
}

/// <summary>Биты <see cref="LotRenderData.Flags"/>.</summary>
public static class LotRenderFlags
{
    public const byte Lost = 1;          // дорога снесена
    public const byte Painted = 2;       // зону задал игрок
    public const byte HasBuilding = 4;
}

/// <summary>Участок для клиента: 16 байт (zoning-demand.md §13). Индекс = номер участка.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct LotRenderData
{
    public float X, Z;           // 0..7
    public float Heading;        // 8..11
    public byte Zone;            // 12     LotZone
    public byte Density;         // 13
    public byte Flags;           // 14     LotRenderFlags
    public byte R0;              // 15
}

/// <summary>Публичная копия полей жителя (инспектор).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ResidentInfo
{
    public int Home, Work;       // индексы зданий или -1
    public ResidentState State;
    public byte Flags;           // бит0 HasCar, бит1 Labour, бит2 Evicted, бит3 Leaving
    public byte Mode;            // TravelMode текущей ноги (если InLeg)
    public byte R0;
    public int Agent;            // индекс машины или пешехода в пути (-1: нет)
    public int AgentGen;
    public long Balance;
    public long StateTick;
    public long EvTick;
}

/// <summary>Публичная копия полей здания (инспектор).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct BuildingInfo
{
    public int Lot, Type;
    public LotZone Kind;
    public BuildingState State;
    public ushort R0;
    public int Occupants, Jobs, Capacity;
    public long Balance;
    public long ProfitEwma;
    public long StateTick;
}

/// <summary>Сводка города (<see cref="Simulation.GetCityStats"/>): состояние на конец последнего тика; «вчера» - накопители прошлых суток.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CityStats
{
    public long Day;
    public int Population;            // живых жителей
    public int Labour;                // в рабочей силе
    public int Employed;              // из них с работой
    public int Seeking;               // ищущих работу
    public int Vacancies;             // открытых мест (Jobs - Occupants по Active C/I)
    public int FreeHousing;           // свободных жилых мест (Capacity - Occupants по Active R)
    public int LotsTotal, LotsZoned, LotsLost;
    public int BuildingsR, BuildingsC, BuildingsI;                 // живых зданий по видам
    public int Constructing, Active, Abandoned;                    // по состояниям
    public int DemandR, DemandC, DemandI;                          // сглаженный спрос, как показывается клиенту
    public int ToursYesterday;
    public int LegsYesterdayCar, LegsYesterdayBus, LegsYesterdayWalk;
    public int AvgCostCarDs, AvgCostBusDs, AvgCostWalkDs;          // средняя обобщённая стоимость ног вчера, децисекунды (0 - нет данных)
    public long BalanceResidents, BalanceFirms, BalanceExternal, BalanceCity;   // Σ = 0 (CZ2)
    public long ResStuck;
    public int ExecMode;              // TripExecution
    public int R0;
}
