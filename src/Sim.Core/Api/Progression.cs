using System.Runtime.InteropServices;

namespace Sim.Core.Api;

// Прогрессия и показатели города (progression.md §2, §4, ADR-0034; задачи PG1-PG4). Плоские POD без строк: id вех - у загрузчика и клиента.
// Номера перечислений закреплены (сейв, Rust-порт): новые значения - только в конец.

/// <summary>Признак, который открывает веха (progression.md §4.2). Номер = бит маски <c>u64</c>.</summary>
public enum Feature : byte
{
    RoadsBasic = 0,         // всегда открыт: BuildRoad прямой (профиль - по битсету)
    ArterialUpgrade = 1,    // UpgradeRoad со сменой профиля/полос/типа
    Curves = 2,             // BuildRoad с флагом Curve
    SignalsFixed = 3,       // SetNodeControl -> Fixed/FlashYellow/Off, SetSignalPlan
    SignalsAdaptive = 4,    // SetNodeControl -> Adaptive
    TurnRules = 5,          // SetTurnBan, SetArmControl
    BusLines = 6,           // CreateBusRoute, InsertBusStop, RemoveBusStop, RemoveBusRoute, SetBusRouteParams
    BusLanes = 7,           // SetLaneFlags с битом BusOnly
    Zoning = 8,             // PaintZone
    Taxes = 9,              // SetBudgetPolicy со сменой ставок/платы
    ParkingFees = 10,       // будущая команда K5
}

/// <summary>Показатель ворот вехи (progression.md §2.1, §3.2). Значения ворот и показателей - целые ×10: минуты ×10, проценты ×10 (‰).</summary>
public enum KpiKind : byte
{
    None = 0,
    TravelMin = 1,          // K1: среднее время в пути, игровые минуты ×10
    IdlePct = 2,            // K3: простой машин, ‰
    BusSharePct = 3,        // K2: доля автобуса, ‰ ног
    SatisfactionPct = 4,    // K4: довольство, ‰
    WorkMin = 5,            // K1a: время до работы, игровые минуты ×10
}

/// <summary>Вид случая недовольства и жалобы (progression.md §6.1). PG2 считает случаи в KPI, PG4 выпускает жалобы.</summary>
public enum ComplaintKind : byte
{
    None = 0,
    LongWait = 1,           // машина простояла непрерывно >= 60 с симуляции
    NoParking = 2,          // первый отказ места на ногу
    LongCommute = 3,        // нога к работе дольше порога
    TourCancelled = 4,      // тур снят по дедлайну WaitDepart
    LongStopWait = 5,       // посадка после долгого ожидания на остановке
    BuildingWorn = 6,       // дом ветшает (building-lifecycle.md §2.2, режим city.lifecycle)
    // 7 - HighRent (BL3, квартплата выше порога)
    HeritageLost = 8,       // снесено значимое здание (§2.5)
    Blight = 9,             // вред заброшки в квартале (§2.4, стадии H2-H3)
}

/// <summary>Флаги вехи (<see cref="MilestoneDesc.Flags"/>).</summary>
public static class MilestoneFlags
{
    public const byte GatesAny = 1;     // ворота связаны «или» (иначе «и»)
}

/// <summary>Ворота вехи: показатель, направление и порог (×10, единицы <see cref="KpiKind"/>). 8 Б. <c>Kpi = None</c> - ворот нет.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public struct KpiGate
{
    public KpiKind Kpi;
    public byte IsMin;      // 1 - значение >= Value (min), 0 - значение <= Value (max)
    public short R;
    public int Value;
}

/// <summary>
/// Веха (progression.md §4.1): порог населения, ворота, открываемые признаки и профили (диапазон <see cref="ProfileFirst"/>..+<see cref="ProfileCount"/> в общем массиве
/// <see cref="ProgressionDesc.ProfileIdx"/>), грант казне, бонус кредитных дней, область карты (-1 - не меняет). 48 Б.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 48)]
public struct MilestoneDesc
{
    public int Population;
    public int Grant;
    public short CreditDays;
    public sbyte Area;
    public byte Flags;      // MilestoneFlags
    public KpiGate G0, G1;
    public ulong Features;  // маска открываемых признаков (бит = Feature)
    public int ProfileFirst, ProfileCount;
}

/// <summary>Область карты прогрессии, м (мировые координаты X/Z). Точка внутри при <c>Min &lt;= p &lt;= Max</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct AreaRect
{
    public float MinX, MinZ, MaxX, MaxZ;
}

/// <summary>
/// Секция <c>progression</c> сценария (progression.md §4.1). <see cref="Enabled"/> = false (нулевая структура) - прогрессии нет: всё открыто, хэш и сейв побитово прежние.
/// Допустима только при <c>demand.model = "residents"</c>. Профили - индексы каталога <c>road_profiles</c> (0-based).
/// </summary>
public struct ProgressionDesc
{
    public bool Enabled;
    public ulong LockedFeatures;           // закрытые на старте признаки (бит = Feature); RoadsBasic закрыть нельзя
    public int[] LockedProfiles;           // закрытые на старте профили (индексы каталога)
    public int MinLegs;                    // ворота без данных: ног за окно меньше - веха не берётся
    public AreaRect[] Areas;               // 0 - стартовая; <= 8; пусто - областей нет
    public MilestoneDesc[] Milestones;     // по порядку, пороги населения строго растут; <= 32
    public int[] ProfileIdx;               // общий массив профилей, открываемых вехами
}

/// <summary>Состояние прогрессии для клиента (<see cref="Simulation.GetProgression"/>, progression.md §4.5).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct ProgressionInfo
{
    public ulong Features;                 // открытые признаки сейчас (бит = Feature)
    public int Reached;                    // взято вех (следующая - индекс Reached)
    public int MilestoneCount;
    public int AreaStage;                  // наибольшая открытая область
    public int CreditDaysBonus;            // бонус кредитных дней от вех
    public int NextPopulation;             // порог населения следующей вехи; -1 - вехи кончились
    public int NextGateMask;               // биты 0/1: ворота G0/G1 следующей вехи сейчас не выполнены (или данных мало)
    public int NextGateValue0, NextGateValue1;   // текущее значение KPI ворот G0/G1 (×10; -1 - нет данных или ворот)
    public int R0;
}

/// <summary>Веха для клиента (<see cref="Simulation.GetMilestone"/>): копия описания и тик взятия (-1 - не взята).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MilestoneInfo
{
    public MilestoneDesc Desc;
    public long ReachedTick;
}

/// <summary>
/// Один игровой час показателей (progression.md §2.3): закрытые ноги и Σ их длительностей (дс, от заявки до прибытия), дельты счётчиков движения машин, случаи недовольства.
/// Целые, без float. 88 Б.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
public struct KpiHour
{
    public int LegsCar, LegsBus, LegsWalk, LegsWork;
    public long DurCarDs, DurBusDs, DurWalkDs, DurWorkDs;
    public long VehTicks, MovingTicks;
    public int IncLongWait, IncNoParking, IncLongCommute, IncTourCancelled, IncLongStopWait;
    public int IncBuildingWorn;     // «дом ветшает» (building-lifecycle.md §2.2; только в режиме city.lifecycle, иначе 0); был резерв R

    public readonly int LegsAll => LegsCar + LegsBus + LegsWalk;

    public readonly int Incidents => IncLongWait + IncNoParking + IncLongCommute + IncTourCancelled + IncLongStopWait + IncBuildingWorn;
}

/// <summary>
/// Показатели города (<see cref="Simulation.GetCityKpi"/>, progression.md §2.1): окно - последние полные игровые часы кольца (до 24) плюс текущий неполный час.
/// Время - игровые минуты ×10, доли - ‰; -1 - данных нет (ни одной ноги, ни одного машино-тика, нет казны).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CityKpiInfo
{
    public long Balance;                   // казна (City)
    public long TrendPerDay;               // IncomeEwma - ExpenseEwma (0 без казны)
    public int HoursFilled;                // полных часов в кольце (0..24); меньше 24 - «собираем данные»
    public int CurrentHourTicks;           // тиков текущего неполного часа
    public int LegsAll, LegsCar, LegsBus, LegsWalk, LegsWork;
    public int TravelMinX10;               // K1 всего
    public int TravelCarMinX10, TravelBusMinX10, TravelWalkMinX10;
    public int WorkMinX10;                 // K1a
    public int BusSharePermille;           // K2
    public int WalkSharePermille;
    public int IdlePermille;               // K3
    public int SatisfactionPermille;       // K4
    public int IncLongWait, IncNoParking, IncLongCommute, IncTourCancelled, IncLongStopWait;
    public int StuckNow;                   // K3a
    public int Gridlock;                   // K3a: 0/1
    public int DaysToLimit;                // K5: ceil((City + CreditLimit) / (Expense - Income)) при отрицательном тренде; -1 - «—» (тренд >= 0 или казны нет)
    public int HasBudget;                  // 1 - режим city_budget
    public int Population;                 // K6
    public int DaysFilled;                 // значений в суточном кольце баланса (0..32)
    public int IncBuildingWorn;            // «дом ветшает» (building-lifecycle.md §2.2)
}

/// <summary>
/// Жалоба жителя (PG4, progression.md §6.2): тик, порядковый номер, вид, счёт случаев за час и место (<see cref="EventPlace"/>, общий формат с событиями EV1). 40 Б.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 40)]
public struct ComplaintInfo
{
    public long Tick;
    public int Seq;
    public ComplaintKind Kind;
    public byte R0;
    public ushort Count;
    public EventPlace Place;
}
