namespace Sim.Core.Api;

// Парковка как ресурс (ADR-0026, docs/design/parking.md §14). Единицы ядра целые (ADR-0022): мм, тики, децисекунды на деньги, Q16. Нулевая структура = режима нет:
// World.Parking == null, ни одна ветка парковки не исполняется, состояние, хэш и поведение побитово прежние (PK1). Режим включается только при demand.model = residents.

/// <summary>Режим обочины стороны сегмента (parking.md §3): нет мест, бесплатно, платно.</summary>
public enum CurbMode : byte
{
    Off = 0,
    Free = 1,
    Paid = 2,
}

/// <summary>Доступ к стоянке здания (<c>buildings.json: parking_access</c>): только жильцы и работники/посетители этого здания либо любые гости.</summary>
public enum ParkingAccess : byte
{
    Private = 0,
    Public = 1,
}

/// <summary>Параметры секции сценария <c>parking</c> (parking.md §14).</summary>
public struct ParkingDesc
{
    // Порядок полей важен для теста раскладки (LayoutTests): bool в Marshal.SizeOf занимает 4 байта, поэтому за каждым bool идёт int (выравнивание даёт тот же размер, что у Unsafe.SizeOf).
    public bool Enabled;                 // parking.model = "resource"
    public int WalkMaxMm;                // walk_max_m: окрестность участка, мм
    public bool AccessWalkAgents;        // parking.access_walk = "agents" (K4); иначе таймер
    public int HomeWalkMaxMm;            // home_walk_max_m: максимум ходьбы до домашнего места, мм
    public int MaxCandidates;            // max_candidates: объектов в окрестности участка (1..64)
    public int WalkSpeedMmS;             // walk_speed_mps: мм/с
    public int FeeDsPerMoney;            // fee_ds_per_money: децисекунд за единицу платы
    public int RetryTicks;               // retry_s: повтор заявки без мест, тики
    public int SearchRetryTicks;         // search_retry_s: повтор поиска после потери резерва, тики
    public int SearchMaxTicks;           // search_max_s: предел поиска, тики
    public int HomeRetryPerDayQ16;       // home_retry_per_day: доля жителей Overflow-дома за сутки, Q16
    public int CurbSlotMm;               // curb.slot_m: длина места, мм
    public int CurbStopClearMm;          // curb.stop_clear_m: запретная зона у остановки, мм
    public CurbMode CurbStreet;          // curb.street
    public CurbMode CurbArterial;        // curb.arterial
    public int CurbFee;                  // curb.fee: плата за гостя на платной обочине
    public bool CurbPermits;             // curb.permits: обочину можно закрепить как домашнее место
    public int GarageFee;                // garage.fee
    public bool GaragePermits;           // garage.permits (последним: хвостовой bool дополняется до выравнивания структуры так же, как в Marshal)

    /// <summary>Дефолты спеки (parking.md §14) при включённом режиме.</summary>
    public static ParkingDesc Default() => new()
    {
        Enabled = true,
        AccessWalkAgents = false,
        WalkMaxMm = 400_000,
        HomeWalkMaxMm = 300_000,
        MaxCandidates = 16,
        WalkSpeedMmS = 1300,
        FeeDsPerMoney = 60,
        RetryTicks = 600,
        SearchRetryTicks = 200,
        SearchMaxTicks = 6000,
        HomeRetryPerDayQ16 = CityDesc.Q16(0.02),
        CurbSlotMm = 6000,
        CurbStopClearMm = 15_000,
        CurbStreet = CurbMode.Free,
        CurbArterial = CurbMode.Off,
        CurbFee = 0,
        CurbPermits = true,
        GarageFee = 0,
        GaragePermits = false,
    };

    /// <summary>Пределы параметров (без деления на ноль и переполнений в целочисленных формулах).</summary>
    public readonly bool IsValid() =>
        WalkMaxMm is >= 0 and <= 10_000_000 && HomeWalkMaxMm is >= 0 and <= 10_000_000 && MaxCandidates is >= 1 and <= 64 &&
        WalkSpeedMmS is >= 100 and <= 10_000 && FeeDsPerMoney is >= 0 and <= 100_000 && RetryTicks is >= 1 and <= 1_000_000 &&
        SearchRetryTicks is >= 1 and <= 1_000_000 && SearchMaxTicks is >= 1 and <= 10_000_000 && HomeRetryPerDayQ16 is >= 0 and <= 65536 &&
        CurbSlotMm is >= 1000 and <= 100_000 && CurbStopClearMm is >= 0 and <= 1_000_000 && CurbStreet <= CurbMode.Paid && CurbArterial <= CurbMode.Paid &&
        CurbFee >= 0 && GarageFee >= 0;
}
