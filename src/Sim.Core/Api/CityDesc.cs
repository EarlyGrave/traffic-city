using System;
using Sim.Core.Util;

namespace Sim.Core.Api;

// Секции сценария режима жителей (ADR-0011, ADR-0021, docs/design/zoning-demand.md §16). Режим включает DemandDesc.Model = Residents;
// остальные секции читаются только в нём. Единицы ядра уже целые (ADR-0022): мм, тики, децисекунды, Q8/Q16, проценты, дни, слоты (1/288 суток).
// Нулевая структура = секции нет. Заполненный по спеке desc даёт CityDesc.Default() (дефолты спеки), загрузчик сценариев меняет только заданные ключи.

/// <summary>Вид зоны участка и здания (<c>Lot.Zone</c>, <c>BuildingTypeDesc.Kind</c>): 1 R, 2 C, 3 I.</summary>
public enum LotZone : byte
{
    None = 0,
    Residential = 1,
    Commercial = 2,
    Industrial = 3,

    /// <summary>Гараж (parking.md §3): здание-стоянка, ставится командой <c>BuildParking</c> (K6); рост и зонирование его не трогают (<c>Lot.Zone</c> этого значения не принимает).</summary>
    Parking = 4,
}

/// <summary>Исполнитель ног (zoning-demand.md §4, §9): агенты (micro, дефолт) или агрегат (скимы и загрузка ячеек).</summary>
public enum TripExecution : byte
{
    Agents = 0,
    Aggregate = 1,
}

/// <summary>Способ поездки жителя (индекс в скиме <c>M = 3</c>).</summary>
public enum TravelMode : byte
{
    Car = 0,
    Bus = 1,
    Walk = 2,
}

/// <summary>Источник поездок пешеходов (<c>peds.source</c>, zoning-demand.md §4.1): заготовка pedestrians.md §3 или жители города (Z2).</summary>
public enum PedSource : int
{
    Preform = 0,
    City = 1,
}

/// <summary>Строка таблицы типов зданий (<c>buildings.json</c>, zoning-demand.md §2.2, §17). Строки только дописываются.</summary>
public struct BuildingTypeDesc
{
    public LotZone Kind;            // R / C / I
    public byte Density;            // 0 низкая, 1 высокая
    public ushort Reserved;
    public int Capacity;            // R: жилых мест, C/I: рабочих мест
    public int ShopCapacity;        // C: визитов в час
    public int ShiftStartSlot;      // C/I: начало смены (слот 1/288 суток)
    public int ShiftSlots;          // C/I: длительность смены, слотов
    public int ConstructDays;       // дней в Constructing
    public int AttractQ8;           // C: привлекательность для выбора цели (Q8)
    public int VisualRow;           // строка визуала для клиента (индекс, заданный хостом)

    // ---- парковка (parking.md §3, §14, K1): читается только в режиме parking.model = resource; нули - стоянки у здания нет ----
    public int ParkingCapacity;     // R/C/I: мест стоянки здания (buildings.json: parking); для Kind = Parking (гараж) - Capacity
    public ParkingAccess ParkingAccess;   // buildings.json: parking_access
    public int ParkingFee;          // buildings.json: parking_fee, деньги за постановку гостя
}

/// <summary>Календарь (<c>calendar</c>).</summary>
public struct CityCalendarDesc
{
    public int DayTicks;            // calendar.day_s * 20; дефолт 72 000
    public int StartHour;           // calendar.start_hour: игровой час начала (0..23), Tick при Create = StartHour * DayTicks / 24; дефолт 0
}

/// <summary>Участки и рост (<c>zoning</c>).</summary>
public struct CityZoningDesc
{
    public int LotFrontageMm;       // lot_frontage_m, дефолт 40 000
    public int LotDepthMm;          // lot_depth_m, дефолт 40 000
    public int NodeClearMm;         // node_clear_m, дефолт 20 000
    public bool Growth;             // рост зданий (zoning.growth)
    public int MaxNewBuildingsPerDay;   // дефолт 20
    public bool Auto;               // автозонирование (zoning.auto есть)
    public int CCenterShareQ16;     // доля центральных ячеек под C
    public int IEdgeShareQ16;       // доля крайних ячеек под I
    public int UnzonedShareQ16;     // доля участков без зоны
    public int MaxPaintM2;          // предел площади PaintZone, м2; дефолт 250 000
    public byte GrowthStep;         // zoning.growth_step: 0 = "day" (дефолт), 1 = "hour": спрос, въезд и рост зданий - на сдвиге игрового часа с лимитами /24
    public byte GridFromBounds;     // zoning.grid_from_bounds: 1 = сетка города по tools.world_bounds (иначе по рельефу), а не по начальной сети; дефолт 0
}

/// <summary>Жители (<c>residents</c>).</summary>
public struct CityResidentsDesc
{
    public int InitialFillQ16;      // initial_fill
    public int CarOwnershipQ16;     // car_ownership
    public int LabourShareQ16;      // labour_share
    public TripExecution TripExecution;
    public int SeekMaxDays;         // seek_max_days, дефолт 7
    public int EvictMaxDays;        // evict_max_days, дефолт 2
    public int WaitMaxTicks;        // wait_max_s * 20, дефолт 12 000
    public int MaxRelocatePerDayQ16;
    public int MaxJobSwitchPerDayQ16;
    public int MaxImmigrantsPerDayQ16;
    public int MinImmigrantsPerDay; // residents.min_immigrants_per_day: пол суточного въезда, cap = max(min, rate * N); дефолт 0
    public int PlanSlot;            // plan_slot, дефолт 36
    public int ShopSlots;           // shop_slots, дефолт 6
    public int HomeMinSlots;        // home_min_slots, дефолт 2
    public int MatchPerTick;        // §8.1: сопоставлений работы за тик, дефолт 4
    public int VacateDays;          // §8.4: дней до сноса заброшенного дома, дефолт 3
    public int SwitchRatioQ8;       // §8.1: switch_ratio (Q8), дефолт 1.5
    public int RBias;               // §8.2: r_bias, дефолт 0
}

/// <summary>Экономика (<c>economy</c>); деньги - целые условные единицы.</summary>
public struct CityEconomyDesc
{
    public int WagePerShift;
    public int BasketPrice;
    public int GoodsPct;
    public int ImportPct;
    public int ExportPerShift;
    public int InitialResidentBalance;
    public int InitialFirmBalance;
    public int ConstructCost;
    public int HireMarginPct;
    public int LossDaysFire;
    public int LossDaysClose;
    public int EwmaShift;
    public int CTargetPct;          // §8.3: c_target, визитов на место C к норме, %; дефолт 100

    // ---- казна города (city-economy.md §13, 4.2): ключи разрешены только при residents; city_budget = false - режим выключен, остальные поля хранятся как дефолты ----
    public bool CityBudget;                     // economy.city_budget
    public int TaxRBp;                          // tax_r_pct * 100: налог на зарплату (дефолт политики), б.п.
    public int TaxCProfitBp;                    // tax_c_profit_pct * 100
    public int TaxIProfitBp;                    // tax_i_profit_pct * 100
    public int LandTaxBpDay;                    // land_tax_bp_day: земельный налог в сутки, б.п. цены места
    public int BusFare;                         // bus_fare: плата за автобусную ногу
    public int InitialCityBalancePerResident;   // initial_city_balance_per_resident: вклад в казну на жителя
    public int InitialCityBalance;              // initial_city_balance: абсолютный вклад в казну сверх вклада на жителя; дефолт 0
    public int InterestBpDay;                   // interest_bp_day: процент на долг в сутки, б.п.
    public int CreditDays;                      // credit_days: лимит кредита в днях сглаженного дохода
    public int MaintRoadStreetPerLaneKmDay;     // maint_road_per_lane_km_day.street
    public int MaintRoadArterialPerLaneKmDay;   // maint_road_per_lane_km_day.arterial
    public int MaintBusDay;                     // maint_bus_day: на автобус в сутки
    public int MaintStopDay;                    // maint_stop_day: на остановку в сутки
    public int LvRef;                           // lv_ref: цена места при LS_ref
    public int LvSlopePerMin;                   // lv_slope_per_min: прирост цены за минуту выигрыша доступности
    public int LvMin, LvMax;                    // lv_min, lv_max: границы цены места
    public int LvShift;                         // lv_shift: сглаживание цены (>> shift в сутки)
    public int VotPct;                          // vot_pct: ценность времени, % зарплаты
    public int ResBufferDays;                   // res_buffer_days: буфер жителя в днях дохода
    public int FirmBufferDays;                  // firm_buffer_days: буфер фирмы в днях фонда оплаты
    public int PayoutShift;                     // payout_shift: доля вывода остатка (>> shift)
    public int EvictDebtDays;                   // evict_debt_days: порог долга жителя в днях дохода
    public int BudgetEwmaShift;                 // budget_ewma_shift: сглаживание дохода и расхода казны
    public int UndoRefundS;                     // undo_refund_s: окно возврата undo, с игрового времени
    public int MaxTaxBp;                        // max_tax_bp: предел ставок налогов политики
    public int MaxLandBp;                       // max_land_bp_day: предел земельного налога
    public int MaxFare;                         // max_fare: предел платы за проезд
}

/// <summary>Выбор (<c>choice</c>, ADR-0021): θ - уже в виде <c>θ64 = θ[1/дс] · 64 · 2^16</c> (zoning-demand.md §6.2), стоимости - децисекунды.</summary>
public struct CityChoiceDesc
{
    public int ThetaMode64;
    public int ThetaDest64;
    public int ThetaTime64;
    public int ThetaFreq64;
    public int BetaFreqQ8;
    public int AscFreqDs;
    public int GammaFreqDs;
    public int AscNonWorkerDs;      // §6.3, дефолт 0
    public int FreqMax;
    public int WWaitQ8;
    public int WWalkQ8;
    public int AscCarDs;
    public int AscBusDs;
    public int AscWalkDs;
    public int SdeQ8;
    public int SdlQ8;
    public int TimeStepSlots;
    public int DestCandidates;
    public int MaxWalkMm;
    public int SkimPeriods;         // дефолт 6
    public int SkimShift;           // дефолт 2
    public int SkimUpdatePerTick;   // дефолт 256
}

/// <summary>Все параметры режима жителей одним значением (общая поверхность Z1, frozen для Z2-Z8).</summary>
public struct CityDesc
{
    public CityCalendarDesc Calendar;
    public CityZoningDesc Zoning;
    public CityResidentsDesc Residents;
    public CityEconomyDesc Economy;
    public CityChoiceDesc Choice;

    /// <summary>Таблица типов зданий (индекс = <c>Building.Type</c>); пустая - <see cref="BaseBuildingTypes"/>.</summary>
    public BuildingTypeDesc[] BuildingTypes;

    /// <summary>6 типов пакета <c>base</c> (R/C/I x низкая/высокая плотность); тот же набор в <c>content/base/buildings.json</c> (тест сверяет).</summary>
    public static BuildingTypeDesc[] BaseBuildingTypes() => new[]
    {
        new BuildingTypeDesc { Kind = LotZone.Residential, Density = 0, Capacity = 8, ConstructDays = 3, VisualRow = 0, ParkingCapacity = 8 },
        new BuildingTypeDesc { Kind = LotZone.Residential, Density = 1, Capacity = 40, ConstructDays = 6, VisualRow = 1, ParkingCapacity = 20 },
        new BuildingTypeDesc { Kind = LotZone.Commercial, Density = 0, Capacity = 10, ShopCapacity = 30, ShiftStartSlot = 96, ShiftSlots = 96, ConstructDays = 3, AttractQ8 = 256, VisualRow = 2, ParkingCapacity = 10, ParkingAccess = ParkingAccess.Public },
        new BuildingTypeDesc { Kind = LotZone.Commercial, Density = 1, Capacity = 40, ShopCapacity = 120, ShiftStartSlot = 96, ShiftSlots = 96, ConstructDays = 6, AttractQ8 = 384, VisualRow = 3, ParkingCapacity = 30, ParkingAccess = ParkingAccess.Public },
        new BuildingTypeDesc { Kind = LotZone.Industrial, Density = 0, Capacity = 12, ShiftStartSlot = 90, ShiftSlots = 96, ConstructDays = 3, VisualRow = 4, ParkingCapacity = 12 },
        new BuildingTypeDesc { Kind = LotZone.Industrial, Density = 1, Capacity = 60, ShiftStartSlot = 90, ShiftSlots = 96, ConstructDays = 6, VisualRow = 5, ParkingCapacity = 40 },
    };

    /// <summary>Дефолты спеки (zoning-demand.md §16) с базовой таблицей зданий; <c>Calendar.DayTicks</c> = 72 000.</summary>
    public static CityDesc Default() => new()
    {
        Calendar = new CityCalendarDesc { DayTicks = 72_000 },
        Zoning = new CityZoningDesc
        {
            LotFrontageMm = 40_000,
            LotDepthMm = 40_000,
            NodeClearMm = 20_000,
            Growth = true,
            MaxNewBuildingsPerDay = 20,
            Auto = true,
            CCenterShareQ16 = Q16(0.15),
            IEdgeShareQ16 = Q16(0.15),
            UnzonedShareQ16 = Q16(0.2),
            MaxPaintM2 = 250_000,
        },
        Residents = new CityResidentsDesc
        {
            InitialFillQ16 = Q16(0.8),
            CarOwnershipQ16 = Q16(0.7),
            LabourShareQ16 = Q16(0.6),
            TripExecution = TripExecution.Agents,
            SeekMaxDays = 7,
            EvictMaxDays = 2,
            WaitMaxTicks = 12_000,
            MaxRelocatePerDayQ16 = Q16(0.005),
            MaxJobSwitchPerDayQ16 = Q16(0.005),
            MaxImmigrantsPerDayQ16 = Q16(0.01),
            PlanSlot = 36,
            ShopSlots = 6,
            HomeMinSlots = 2,
            MatchPerTick = 4,
            VacateDays = 3,
            SwitchRatioQ8 = 384,
            RBias = 0,
        },
        Economy = new CityEconomyDesc
        {
            WagePerShift = 60,
            BasketPrice = 30,
            GoodsPct = 40,
            ImportPct = 50,
            ExportPerShift = 42,
            InitialResidentBalance = 300,
            InitialFirmBalance = 3000,
            ConstructCost = 1000,
            HireMarginPct = 10,
            LossDaysFire = 3,
            LossDaysClose = 14,
            EwmaShift = 3,
            CTargetPct = 100,
            TaxRBp = 600,
            TaxCProfitBp = 2000,
            TaxIProfitBp = 2000,
            LandTaxBpDay = 15,
            BusFare = 2,
            InitialCityBalancePerResident = 60,
            InterestBpDay = 3,
            CreditDays = 60,
            LvRef = 200,
            LvSlopePerMin = 20,
            LvMin = 50,
            LvMax = 1000,
            LvShift = 3,
            VotPct = 50,
            ResBufferDays = 5,
            FirmBufferDays = 10,
            PayoutShift = 3,
            EvictDebtDays = 3,
            BudgetEwmaShift = 3,
            UndoRefundS = 10,
            MaxTaxBp = 5000,
            MaxLandBp = 100,
            MaxFare = 1000,
        },
        Choice = new CityChoiceDesc
        {
            ThetaMode64 = Theta64(0.01),
            ThetaDest64 = Theta64(0.008),
            ThetaTime64 = Theta64(0.01),
            ThetaFreq64 = Theta64(0.005),
            BetaFreqQ8 = 256,
            AscFreqDs = -3000,
            GammaFreqDs = 3000,
            AscNonWorkerDs = 0,
            FreqMax = 2,
            WWaitQ8 = 512,
            WWalkQ8 = 512,
            AscCarDs = 0,
            AscBusDs = 1200,
            AscWalkDs = 0,
            SdeQ8 = 128,
            SdlQ8 = 512,
            TimeStepSlots = 2,
            DestCandidates = 8,
            MaxWalkMm = 1_500_000,
            SkimPeriods = 6,
            SkimShift = 2,
            SkimUpdatePerTick = 256,
        },
        BuildingTypes = BaseBuildingTypes(),
    };

    /// <summary>Дробь в Q16 (округление к ближайшему целому; ADR-0022: <c>f64 -&gt; f32</c>, затем округление - то же делает загрузчик сценариев).</summary>
    public static int Q16(double v) => DetMath.ToIntSat(Math.Round((float)v * 65536.0, MidpointRounding.AwayFromZero));

    /// <summary>Q8 из дроби.</summary>
    public static int Q8(double v) => DetMath.ToIntSat(Math.Round((float)v * 256.0, MidpointRounding.AwayFromZero));

    /// <summary>θ [1/с] в <c>θ64 = θ[1/дс] · 64 · 2^16</c> (zoning-demand.md §6.2).</summary>
    public static int Theta64(double perSecond) => DetMath.ToIntSat(Math.Round((float)perSecond / 10.0 * 64.0 * 65536.0, MidpointRounding.AwayFromZero));
}
