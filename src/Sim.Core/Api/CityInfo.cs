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

    /// <summary>Переселенец едет от внешнего подключения к дому (new-game-scratch.md §2.3). Только в API: в ядре - флаг жителя поверх <c>WaitDepart/InLeg</c>.</summary>
    Arriving = 5,
}

/// <summary>Какое поле доступности отдаёт <see cref="Simulation.GetAccessibility"/> (zoning-demand.md §6.7).</summary>
public enum AccessKind : byte
{
    Shops = 0,      // LS_C: logsum до C-целей
    Jobs = 1,       // LS_jobs: logsum до рабочих мест
}

/// <summary>Вид потока денег (zoning-demand.md §7.1, city-economy.md §2.1). Числа лежат в сейве; новые - только в конец. Всего <see cref="Simulation.FlowKindCount"/> = 22.</summary>
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
    UtilityFee = 17,    // житель/фирма -> Utility: квартплата (building-lifecycle.md §2.10, режим city.lifecycle)
    Repair = 18,        // Utility -> External: ремонт бригадой ЖКХ
    CrewWages = 19,     // Utility -> External: зарплата бригад
    Restoration = 20,   // City -> External: капремонт (резерв BL4)
    UtilitySubsidy = 21, // City -> Utility: субсидия ЖКХ из казны (флаг политики UtilitySubsidy)
    ServiceUpkeep = 22,  // fund -> External: суточный расход станции службы (services.md §2.4, режим city.services)
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
    public byte R0;              // 17     состояние Cond, % (режим city.lifecycle; без режима 0)
    public ushort R1;            // 18..19 биты BuildingRenderFlags (режим city.lifecycle; без режима 0)
    public int Gen;              // 20..23 поколение слота
}

/// <summary>Биты <see cref="BuildingRenderData.R1"/> (building-lifecycle.md §3).</summary>
public static class BuildingRenderFlags
{
    public const ushort Monument = 1;      // резерв (памятник отложен, §2.8)
    public const ushort Requested = 2;     // заявка ЖКХ (BL3)
    public const ushort Demolishing = 4;   // сносится: удаление на ближайшей границе суток
    public const ushort Boost = 8;         // резерв (буст отложен, §2.7)
}

/// <summary>Биты <see cref="LotRenderData.Flags"/>.</summary>
public static class LotRenderFlags
{
    public const byte Lost = 1;          // дорога снесена
    public const byte Painted = 2;       // зону задал игрок
    public const byte HasBuilding = 4;
    public const byte Annex = 16;        // пристройка службы (services.md §2.8, SR5): участок принадлежит станции
    public const byte Abandoned = 8;     // здание участка заброшено (<c>BuildingState.Abandoned</c>); производное, в сейве не хранится
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

/// <summary>Состояние ремонта здания (<see cref="BuildingLifeInfo.RepairState"/>, building-lifecycle.md §2.10).</summary>
public enum RepairState : byte
{
    None = 0,       // заявки нет
    Queued = 1,     // заявка в очереди ЖКХ
    CrewEnRoute = 2,// бригада едет к зданию
    Working = 3,    // бригада ремонтирует
}

/// <summary>Жизнь здания для панели клиента (<see cref="Simulation.TryGetBuildingLife"/>, building-lifecycle.md §3, §6): состояние, потолок, возраст, значимость, признаки, ремонт и цена капремонта.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct BuildingLifeInfo
{
    public int CondBp, CeilBp;          // состояние и потолок, б.п. (10 000 = 100 %)
    public int AgeDays;                 // возраст, игровые сутки
    public int Signif;                  // накопитель значимости
    public int SignifLevel;             // уровень 0-5
    public ushort Flags;                // BuildingRenderFlags (Requested, Demolishing; Monument/Boost - резерв)
    public RepairState RepairState;
    public byte HarmStage;              // стадия вреда заброшки: 0 - нет (не заброшено), 1-3 = H1-H3 (§2.4)
    public byte CanRestore;             // 1 - RestoreBuilding сейчас пройдёт проверки (цена в RestorePrice; казна не проверяется)
    public long RestorePrice;           // цена капремонта сейчас (0, если CanRestore = 0)
    public long RepairPrice;            // цена ремонта ЖКХ до потолка из фонда (для справки)
    public long DemolishPrice;          // цена сноса сейчас
}

/// <summary>Фонд и работа ЖКХ для панели бюджета и значка «фонд пуст» (<see cref="Simulation.GetUtilityInfo"/>, building-lifecycle.md §2.10).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct UtilityInfo
{
    public long Fund;                   // баланс фонда Utility
    public long RepairsDone;            // ремонтов за всё время
    public long RepairDelayTicksTotal;  // Σ задержек заявка-ремонт, тики
    public long AvgDelayTicks;          // средняя задержка ремонта, тики (0 - ремонтов ещё не было)
    public long RepairsPerDayX100;      // ремонтов в сутки в среднем с начала игры, ×100
    public int Tariff;                  // действующий тариф (BudgetPolicy.R0)
    public int PendingTariff;           // ожидающий (= Tariff, если ожидающей политики нет)
    public int MaxTariff;               // предел тарифа
    public int QueueLength;             // заявок в очереди
    public int Depots;                  // работающих депо
    public int Crews;                   // бригад всего
    public int CrewsBusy;               // бригад в выезде (не в депо)
    public byte Subsidy;                // 1 - субсидия из казны включена (действующая политика)
    public byte PendingSubsidy;         // 1 - в ожидающей политике
    public byte FundEmpty;              // 1 - фонд <= 0 (ремонт ждёт денег или идёт на субсидии)
    public byte R0;
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

    /// <summary>Субсидия ЖКХ из казны (building-lifecycle.md §2.10, только режим <c>city.lifecycle</c>): дефицит фонда <c>Utility</c> покрывает <c>City</c>.</summary>
    public const ushort UtilitySubsidy = 2;
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
    public ushort R0, R1;          // 12..15 R0 - тариф ЖКХ (сотые в сутки на жителя, только режим city.lifecycle, иначе 0); R1 - резерв, 0
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

/// <summary>Работа записи службы (<see cref="Simulation.GetServiceStats"/>, services.md §2.3-§2.6): вызовы, отклик, машины, койки и приём, ползунок и расход.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ServiceStats
{
    public long CallsQueued;            // вызовов поставлено за всё время
    public long CallsDone;              // прибытий к цели
    public long CallsLate;              // опозданий (дольше deadline_s или некому ехать)
    public long RespTicksTotal;         // Σ отклика, тики
    public long AvgRespTicks;           // средний отклик, тики (0 - вызовов не было)
    public long CostToday;              // расход записи за текущие сутки (SvcCostDay)
    public long CostYesterday;          // за прошлые сутки
    public int Stations;                // работающих станций
    public int UnitsOnLine;             // машин на линии (не снятых, OffDuty не в счёте)
    public int UnitsBusy;               // в выезде (к цели, на месте, назад)
    public int UnitsMax;                // слотов машин всего (гараж)
    public int Waiting;                 // вызовов в кольцах (обычное + срочное)
    public int BedsUsed;                // SR3: занято коек стационара (станции записи)
    public int Beds;                    // коек всего (beds' с апгрейдами и ползунком)
    public int IcuUsed;                 // занято мест реанимации (сверх мест - тоже в счёте)
    public int Icu;                     // мест реанимации (icu')
    public int VisitsToday;             // записей на приём за текущие сутки (VisitsDay станций)
    public byte Pct;                    // действующий ползунок, %
    public byte PctPending;             // ожидающий (с границы суток)
}

/// <summary>Станция службы (<see cref="Simulation.GetServiceStation"/>, services.md §2.8, SR5): запись, уровень, апгрейды, пристройки, машины и цена следующего шага.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ServiceStationInfo
{
    public long NextLevelCost;          // build_cost следующего уровня без выкупа (0 - уровня нет)
    public int Svc;                     // индекс записи
    public int Level;                   // 1..LevelCount
    public int LevelCount;
    public int UpgradeCount;            // апгрейдов у текущего уровня
    public int Annexes;                 // участков-пристроек
    public int UnitsMax;                // слотов машин
    public int Duty;                    // машин на линии по ползунку
    public byte UpgradeMask;            // включённые апгрейды (бит = номер в списке уровня)
}

/// <summary>Вызов для маркера на карте (<see cref="Simulation.GetServiceCalls"/>, services.md §7 C3): цель, вид, запись, ждёт или едет.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ServiceCallInfo
{
    public float X, Z;                  // центр участка цели, м
    public int Building;                // слот здания цели
    public int AgeTicks;                // тиков с постановки вызова
    public byte Kind;                   // ServiceCallKind
    public byte Svc;                    // индекс записи
    public byte EnRoute;                // 0 - ждёт в кольце, 1 - машина в пути к цели, 2 - на месте
    public byte Urgent;                 // 1 - срочное кольцо (или вызов срочного вида)
}

/// <summary>Горящее здание для маркера на карте (<see cref="Simulation.GetFires"/>, services.md §7 C3, §2.7).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FireInfo
{
    public float X, Z;                  // центр участка, м
    public int Building;                // слот здания
    public int AgeTicks;                // тиков с начала огня
    public ushort CondStart;            // Cond в начале, б.п.
    public ushort Cond;                 // Cond сейчас, б.п.
    public byte Smoldering;             // 1 - тлеет (smolder_s), 0 - открытый огонь
}
