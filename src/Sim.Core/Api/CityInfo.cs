using System;
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

/// <summary>Вид потока денег (zoning-demand.md §7.1, city-economy.md §2.1). Числа лежат в сейве; новые - только в конец. Всего <see cref="Simulation.FlowKindCount"/> = 17.</summary>
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
    Interest = 13,      // 4.2 (city-economy.md §2.1): проценты по долгу City -> External
    Dividend = 14,      // фирма -> External: остаток сверх буфера
    Leakage = 15,       // житель -> External: траты вне города сверх буфера
    Refund = 16,        // External -> City: возврат за снос, понижение, undo
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

/// <summary>
/// Участок для клиента: 28 байт (zoning-demand.md §13, RP10b). Индекс = номер участка. Клетки мировой сетки (<c>module_m</c>, <see cref="NetworkView.ModuleMm"/>):
/// <c>CellX/CellZ</c> - передний левый угол, <c>CellsF x CellsD</c> - фасад x глубина, <c>Orient</c> - ось глубины (0..3: +x, -x, +z, -z). Legacy: <c>Orient = 255</c>, клетки 0.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 28)]
public struct LotRenderData
{
    public const byte OrientLegacy = 255;

    public float X, Z;           // 0..7
    public float Heading;        // 8..11
    public byte Zone;            // 12     LotZone
    public byte Density;         // 13
    public byte Flags;           // 14     LotRenderFlags
    public byte Orient;          // 15     0..3 ось глубины +x, -x, +z, -z; 255 - legacy (без привязки к сетке)
    public byte CellsF;          // 16     ширина по фасаду, клеток
    public byte CellsD;          // 17     глубина, клеток
    public ushort R0;            // 18..19
    public int CellX, CellZ;     // 20..27 передний левый угол, клетки сетки

    /// <summary>Участок на мировой сетке (не legacy).</summary>
    public readonly bool OnGrid => Orient <= 3;

    /// <summary>Единичная ось глубины (от дороги наружу) по <see cref="Orient"/>; для legacy (0, 0).</summary>
    public readonly void DepthDir(out int dirX, out int dirZ)
    {
        switch (Orient)
        {
            case 0: dirX = 1; dirZ = 0; break;
            case 1: dirX = -1; dirZ = 0; break;
            case 2: dirX = 0; dirZ = 1; break;
            case 3: dirX = 0; dirZ = -1; break;
            default: dirX = 0; dirZ = 0; break;
        }
    }

    /// <summary>Границы клеток участка (включительно). Та же формула, что в ядре (<c>LotBuilder.CellBounds</c>); legacy - false.</summary>
    public readonly bool TryGetCellBounds(out int x0, out int z0, out int x1, out int z1)
    {
        x0 = z0 = x1 = z1 = 0;
        if (Orient > 3 || CellsF == 0 || CellsD == 0)
        {
            return false;
        }

        DepthDir(out int dx, out int dz);
        int lx = dz;      // «влево» от оси глубины: (dz, -dx)
        int lz = -dx;
        int f = CellsF - 1;
        int d = CellsD - 1;
        int bx = CellX + (dx * d) - (lx * f);
        int bz = CellZ + (dz * d) - (lz * f);
        x0 = Math.Min(CellX, bx);
        x1 = Math.Max(CellX, bx);
        z0 = Math.Min(CellZ, bz);
        z1 = Math.Max(CellZ, bz);
        return true;
    }
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

/// <summary>Биты <see cref="BudgetPolicy.Flags"/> (city-economy.md §2.2).</summary>
public static class BudgetPolicyFlags
{
    /// <summary>Бюджет включён. У города с бюджетом бит обязан стоять в каждой политике (выключить нельзя); у города без бюджета - запрос <c>Enable</c> (E2).</summary>
    public const ushort Enable = 1;
}

/// <summary>
/// Политика бюджета (city-economy.md §2.2): 16 байт. Ставки - базисные пункты (10 000 = 100%), <c>Fare</c> - деньги за автобусную ногу. Действующая политика меняется
/// только на границе суток; команда <see cref="CommandKind.SetBudgetPolicy"/> кладёт её в ожидающую.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2, Size = 16)]
public struct BudgetPolicy
{
    public ushort TaxRBp;          // 0..1   налог на зарплату жителей
    public ushort TaxCBp;          // 2..3   налог на прибыль коммерческих фирм
    public ushort TaxIBp;          // 4..5   налог на прибыль промышленных фирм
    public ushort LandTaxBpDay;    // 6..7   земельный налог в сутки от цены места
    public ushort Fare;            // 8..9   плата за автобусную ногу
    public ushort Flags;           // 10..11 BudgetPolicyFlags
    public ushort R0, R1;          // 12..15 резерв, обязаны быть 0
}

/// <summary>Состояние бюджета города для клиента (<see cref="Simulation.GetCityBudget"/>, city-economy.md §10). Потоки по видам за вчера - <see cref="Simulation.CopyFlowTotals"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CityBudgetInfo
{
    public long Balance;                 // баланс казны (City)
    public long IncomeEwma;              // сглаженный доход за сутки
    public long ExpenseEwma;             // сглаженный расход за сутки
    public long CreditLimit;             // credit_days * IncomeEwma
    public long MaintRoadDay;            // содержание дорог в сутки (производное)
    public long MaintBusDay;             // содержание автобусов в сутки (производное)
    public long FareEvadedYesterday;     // ног без оплаты (не хватило на проезд) за вчера
    public long DebtEvictionsYesterday;  // выселений за долг за вчера
    public int DebtDays;                 // дней подряд с City < 0
    public int OverLimitDays;            // дней подряд за кредитным лимитом
    public byte Insolvent;               // 1 - за кредитным лимитом
    public byte HasPending;              // 1 - есть ожидающая политика
    public ushort R0;
    public BudgetPolicy Policy;          // действующая
    public BudgetPolicy PendingPolicy;   // ожидающая границы суток (если HasPending)
}
