using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>Состояние бригады (<see cref="CrewStore.State"/>, building-lifecycle.md §2.10, services.md §2.3).</summary>
internal static class CrewState
{
    public const byte Free = 0;      // слот свободен
    public const byte Idle = 1;      // на станции, ждёт вызов
    public const byte ToSite = 2;    // едет к цели
    public const byte Working = 3;   // работает на месте до DueTick
    public const byte Return = 4;    // едет назад на станцию
    public const byte OffDuty = 5;   // на станции, не на линии (ползунок бюджета ниже 100 %, services.md §2.4; только в режиме city.services)
}

/// <summary>
/// Вызов в кольце записи (services.md §2.3, <c>CallStore</c>): вид, запись, цель (здание + поколение), ссылка (житель или пожар, SR3/SR4; -1 - нет), тик вызова.
/// Заявка ЖКХ BL - вызов <c>repair</c> записи <c>utility</c> без ссылки.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct ServiceCall
{
    public int Target;
    public int TargetGen;
    public int Ref;
    public int RefGen;
    public long Tick;
    public byte Kind;   // ServiceCallKind
    public byte Svc;    // индекс записи
}

/// <summary>
/// Скомпилированные записи служб (services.md §2.2): таблицы сценария (<c>city.services</c>) либо одна встроенная запись <c>utility</c> (без секции - ЖКХ как в BL).
/// Производное по типам зданий - запись и уровень типа (вместо полей типа <c>ServiceRec/ServiceLevel</c> спеки: в сейв и хэш не входит, считается при создании).
/// </summary>
internal sealed class ServiceTable
{
    public readonly bool Explicit;                // режим city.services
    public readonly ServiceRecordDesc[] Records;
    public readonly ServiceLevelDesc[] Levels;
    public readonly ServiceUpgradeDesc[] Upgrades;
    public readonly ServiceCallDesc[] Calls;
    public readonly ServiceVisitDesc[] Visits;

    /// <summary>Запись ЖКХ (вызов <c>repair</c>, фонд <c>utility</c>) или -1.</summary>
    public readonly int Utility;

    private readonly sbyte[] _recOfType;
    private readonly byte[] _levelOfType;

    public ServiceTable(in CityDesc cfg, BuildingTypes types)
    {
        ref readonly CityServicesDesc s = ref cfg.Services;
        Explicit = s.Enabled;
        if (Explicit)
        {
            Records = s.Records ?? Array.Empty<ServiceRecordDesc>();
            Levels = s.Levels ?? Array.Empty<ServiceLevelDesc>();
            Calls = s.Calls ?? Array.Empty<ServiceCallDesc>();
        }
        else
        {
            int depot = -1;
            for (int i = 0; i < types.Count && depot < 0; i++)
            {
                depot = types.Rows[i].ServiceKind == ServiceKind.Utility ? i : -1;
            }

            short veh = depot >= 0 ? types.Rows[depot].VehicleType : CityDesc.BaseUtilityVehicle;
            CityServicesDesc.BaseRecords(depot, veh, out Records, out Levels, out Calls);
        }

        Upgrades = (Explicit ? s.Upgrades : null) ?? Array.Empty<ServiceUpgradeDesc>();
        Visits = (Explicit ? s.Visits : null) ?? Array.Empty<ServiceVisitDesc>();
        Utility = -1;
        for (int r = 0; r < Records.Length && Utility < 0; r++)
        {
            Utility = Records[r].Fund == ServiceFund.Utility && HasCall(r, ServiceCallKind.Repair) ? r : -1;
        }

        _recOfType = new sbyte[types.Count];
        _levelOfType = new byte[types.Count];
        Array.Fill(_recOfType, (sbyte)-1);
        for (int r = 0; r < Records.Length; r++)
        {
            for (int k = 0; k < Records[r].LevelCount; k++)
            {
                int t = Levels[Records[r].FirstLevel + k].Type;
                if ((uint)t < (uint)types.Count && _recOfType[t] < 0)
                {
                    _recOfType[t] = (sbyte)r;
                    _levelOfType[t] = (byte)(k + 1);
                }
            }
        }

        for (int t = 0; t < types.Count; t++)
        {
            if (_recOfType[t] < 0 && types.Rows[t].ServiceKind == ServiceKind.Utility && Utility >= 0)
            {
                _recOfType[t] = (sbyte)Utility;   // депо BL вне записей: уровень 1 записи ЖКХ
                _levelOfType[t] = 1;
            }
        }
    }

    public int Count => Records.Length;

    /// <summary>
    /// Пределы секции <c>city.services</c> и таблиц записей (services.md §2.2, §6) при <c>Create</c> и загрузке: ёмкости, диапазоны записей в границах таблиц, уровни 1..4,
    /// апгрейды 0..8, виды вызовов и поводов, <c>on_call</c> у <c>repair</c> нельзя, <c>deadline_s</c> 0 или не меньше <c>onsite_s</c>, тип уровня - индекс типа здания.
    /// </summary>
    public static bool IsValid(in CityServicesDesc s, int typeCount, int vehicleTypeCount)
    {
        bool ok = s.MaxServiceSites is >= 1 and <= 65_536 && s.MaxCalls is >= 0 and <= 1 << 20 && s.MaxFires is >= 0 and <= 65_536;
        ok &= s.DisorderIllPct is >= 0 and <= 1_000 && s.FireRiskPct is >= 0 and <= 1_000 && s.SmolderS is >= 0 and <= 86_400 && s.AnnexBuyPct is >= 0 and <= 1_000;
        ok &= s.IllLightPpm is >= 0 and <= 1_000_000 && s.IllInpatientPpm is >= 0 and <= 1_000_000 && s.IllCriticalPpm is >= 0 and <= 1_000_000;
        ok &= s.IllDaysLight is >= 1 and <= 31 && s.IllDaysInpatient is >= 1 and <= 31 && s.IllDaysCritical is >= 1 and <= 31;
        if (!ok)
        {
            return false;
        }

        ServiceRecordDesc[] rec = s.Records ?? Array.Empty<ServiceRecordDesc>();
        ServiceLevelDesc[] lv = s.Levels ?? Array.Empty<ServiceLevelDesc>();
        ServiceUpgradeDesc[] up = s.Upgrades ?? Array.Empty<ServiceUpgradeDesc>();
        ServiceCallDesc[] calls = s.Calls ?? Array.Empty<ServiceCallDesc>();
        ServiceVisitDesc[] vis = s.Visits ?? Array.Empty<ServiceVisitDesc>();
        if (rec.Length > CityServicesDesc.MaxRecords || lv.Length > CityServicesDesc.MaxRecords * CityServicesDesc.MaxLevels ||
            up.Length > CityServicesDesc.MaxRecords * CityServicesDesc.MaxLevels * CityServicesDesc.MaxUpgrades || calls.Length > CityServicesDesc.MaxRecords * 8 || vis.Length > CityServicesDesc.MaxRecords * 8)
        {
            return false;
        }

        for (int r = 0; r < rec.Length; r++)
        {
            ref readonly ServiceRecordDesc d = ref rec[r];
            ok &= d.Fund is ServiceFund.City or ServiceFund.Utility && d.LevelCount is >= 1 and <= CityServicesDesc.MaxLevels && Range(d.FirstLevel, d.LevelCount, lv.Length);
            ok &= Range(d.FirstCall, d.CallCount, calls.Length) && Range(d.FirstVisit, d.VisitCount, vis.Length);
        }

        for (int i = 0; ok && i < lv.Length; i++)
        {
            ref readonly ServiceLevelDesc l = ref lv[i];
            ok &= (uint)l.Type < (uint)typeCount && l.LotMinF >= 1 && l.LotMinD >= 1 && l.BuildCost >= 0 && l.UpkeepDay >= 0 && l.CapacityDay >= 0 && l.Beds >= 0 && l.Icu >= 0;
            ok &= l.OpenSlot is >= 0 and <= CityWorld.SlotsPerDay && l.CloseSlot is >= 0 and <= CityWorld.SlotsPerDay && l.StaySlots is >= 0 and <= CityWorld.SlotsPerDay;
            ok &= l.DispatchVehicle >= -1 && l.DispatchVehicle < vehicleTypeCount && l.Units is >= 0 and <= 64 && l.PerUnitDay >= 0;
            ok &= l.UpgradeCount is >= 0 and <= CityServicesDesc.MaxUpgrades && Range(l.FirstUpgrade, l.UpgradeCount, up.Length);
        }

        for (int i = 0; ok && i < up.Length; i++)
        {
            ok &= up[i].Cost >= 0 && up[i].UpkeepDay >= 0 && up[i].Effect is >= ServiceUpgradeEffect.Units and <= ServiceUpgradeEffect.OnsitePct && up[i].Value >= 0;
        }

        for (int i = 0; ok && i < calls.Length; i++)
        {
            ref readonly ServiceCallDesc c = ref calls[i];
            ok &= c.Kind is >= ServiceCallKind.Repair and <= ServiceCallKind.Fire && c.OnsiteS is >= 0 and <= 86_400 && c.DeadlineS >= 0 && (c.DeadlineS == 0 || c.DeadlineS >= c.OnsiteS);
            ok &= c.PerCall >= 0 && (c.Flags & ~(ServiceCallFlags.Urgent | ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn)) == 0;
            ok &= c.Kind != ServiceCallKind.Repair || (c.Flags & (ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn)) == 0;   // ЖКХ приоритета не получает (§2.3)
        }

        for (int i = 0; ok && i < vis.Length; i++)
        {
            ok &= vis[i].Reason is ServiceVisitReason.Light or ServiceVisitReason.Inpatient && vis[i].PerVisit >= 0 && vis[i].MaxCostS >= 0;
        }

        return ok;
    }

    private static bool Range(int first, int count, int length) => first >= 0 && count >= 0 && (long)first + count <= length;

    /// <summary>Запись типа здания или -1.</summary>
    public int RecOf(int type) => (uint)type < (uint)_recOfType.Length ? _recOfType[type] : -1;

    /// <summary>Уровень (1..4) типа здания в его записи; 0 - тип не служба.</summary>
    public int LevelOf(int type) => (uint)type < (uint)_levelOfType.Length ? _levelOfType[type] : 0;

    /// <summary>У записи есть вызов вида <paramref name="kind"/>.</summary>
    public bool HasCall(int rec, ServiceCallKind kind)
    {
        ref readonly ServiceRecordDesc r = ref Records[rec];
        for (int i = 0; i < r.CallCount; i++)
        {
            if (Calls[r.FirstCall + i].Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>У записи есть вызов с флагом срочности <paramref name="urgent"/>.</summary>
    public bool HasCalls(int rec, bool urgent)
    {
        ref readonly ServiceRecordDesc r = ref Records[rec];
        for (int i = 0; i < r.CallCount; i++)
        {
            if (((Calls[r.FirstCall + i].Flags & ServiceCallFlags.Urgent) != 0) == urgent)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Индекс вызова вида <paramref name="kind"/> записи в <see cref="Calls"/> или -1.</summary>
    public int CallOf(int rec, int kind)
    {
        if ((uint)rec >= (uint)Records.Length)
        {
            return -1;
        }

        ref readonly ServiceRecordDesc r = ref Records[rec];
        for (int i = 0; i < r.CallCount; i++)
        {
            if ((int)Calls[r.FirstCall + i].Kind == kind)
            {
                return r.FirstCall + i;
            }
        }

        return -1;
    }

    /// <summary>Индекс повода визита <paramref name="reason"/> записи в <see cref="Visits"/> или -1.</summary>
    public int VisitOf(int rec, ServiceVisitReason reason)
    {
        ref readonly ServiceRecordDesc r = ref Records[rec];
        for (int i = 0; i < r.VisitCount; i++)
        {
            if (Visits[r.FirstVisit + i].Reason == reason)
            {
                return r.FirstVisit + i;
            }
        }

        return -1;
    }

    /// <summary>Первая запись с выездным вызовом вида <paramref name="kind"/> или -1.</summary>
    public int RecordWithCall(ServiceCallKind kind)
    {
        for (int r = 0; r < Records.Length; r++)
        {
            if (HasCall(r, kind))
            {
                return r;
            }
        }

        return -1;
    }

    /// <summary>Флаги вызова вида <paramref name="kind"/> записи (0 - вызова нет).</summary>
    public byte CallFlagsOf(int rec, int kind)
    {
        int i = CallOf(rec, kind);
        return i >= 0 ? Calls[i].Flags : (byte)0;
    }

    /// <summary>Уровень станции: строка <see cref="Levels"/> записи <paramref name="rec"/> уровня <paramref name="level"/> (1..4).</summary>
    public ref readonly ServiceLevelDesc LevelRow(int rec, int level) => ref Levels[Records[rec].FirstLevel + Math.Clamp(level, 1, Records[rec].LevelCount) - 1];

    /// <summary>Роль, нужная вызову вида <paramref name="kind"/> (services.md §2.2): <c>repair</c>, <c>critical</c>, <c>fire</c>.</summary>
    public static ServiceRole RoleOf(int kind) => (ServiceCallKind)kind switch
    {
        ServiceCallKind.Repair => ServiceRole.Repair,
        ServiceCallKind.Critical => ServiceRole.Critical,
        ServiceCallKind.Fire => ServiceRole.Fire,
        _ => ServiceRole.None,
    };

    /// <summary>
    /// Сумма эффекта <paramref name="effect"/> включённых апгрейдов уровня (биты <paramref name="mask"/>); для <see cref="ServiceUpgradeEffect.OnsitePct"/> - произведение процентов
    /// (100 - апгрейдов нет).
    /// </summary>
    public int UpgradeEffect(in ServiceLevelDesc l, byte mask, ServiceUpgradeEffect effect)
    {
        bool mul = effect == ServiceUpgradeEffect.OnsitePct;
        int v = mul ? 100 : 0;
        for (int i = 0; i < l.UpgradeCount && i < CityServicesDesc.MaxUpgrades; i++)
        {
            ref readonly ServiceUpgradeDesc u = ref Upgrades[l.FirstUpgrade + i];
            if ((mask & (1 << i)) != 0 && u.Effect == effect)
            {
                v = mul ? v * u.Value / 100 : v + u.Value;
            }
        }

        return v;
    }

    /// <summary>Σ <c>upkeep_day</c> включённых апгрейдов уровня.</summary>
    public long UpgradeUpkeep(in ServiceLevelDesc l, byte mask)
    {
        long v = 0;
        for (int i = 0; i < l.UpgradeCount && i < CityServicesDesc.MaxUpgrades; i++)
        {
            v += (mask & (1 << i)) != 0 ? Upgrades[l.FirstUpgrade + i].UpkeepDay : 0;
        }

        return v;
    }
}

/// <summary>
/// Станции служб (services.md §2.3, SoA ёмкости <c>max_service_sites</c>): здание + поколение, запись, уровень, апгрейды, машины, койки (кольца выписки по 16 суткам).
/// Слот свободен при <c>Building = -1</c>. Станция снесённого здания освобождается, только когда на неё не ссылается ни одна бригада (едущие доезжают и распускаются).
/// </summary>
internal sealed class SiteStore
{
    public const int ReleaseDays = 16;

    public readonly int Capacity;
    public readonly int[] Building;
    public readonly int[] Gen;
    public readonly byte[] Svc;
    public readonly byte[] Level;
    public readonly byte[] UpgradeMask;
    public readonly byte[] UnitsMax;
    public readonly byte[] Duty;
    public readonly int[] BedsUsed;
    public readonly int[] IcuUsed;
    public readonly int[] BedRelease;   // [site * 16 + day & 15]
    public readonly int[] IcuRelease;

    public SiteStore(int capacity)
    {
        Capacity = Math.Max(0, capacity);
        Building = new int[Capacity];
        Gen = new int[Capacity];
        Svc = new byte[Capacity];
        Level = new byte[Capacity];
        UpgradeMask = new byte[Capacity];
        UnitsMax = new byte[Capacity];
        Duty = new byte[Capacity];
        BedsUsed = new int[Capacity];
        IcuUsed = new int[Capacity];
        BedRelease = new int[Capacity * ReleaseDays];
        IcuRelease = new int[Capacity * ReleaseDays];
        Array.Fill(Building, -1);
    }

    /// <summary>Слот в исходное (свободен).</summary>
    public void Reset(int s)
    {
        Building[s] = -1;
        Gen[s] = 0;
        Svc[s] = 0;
        Level[s] = 0;
        UpgradeMask[s] = 0;
        UnitsMax[s] = 0;
        Duty[s] = 0;
        BedsUsed[s] = 0;
        IcuUsed[s] = 0;
        Array.Clear(BedRelease, s * ReleaseDays, ReleaseDays);
        Array.Clear(IcuRelease, s * ReleaseDays, ReleaseDays);
    }

    /// <summary>Станций в работе (занятых слотов).</summary>
    public int Used()
    {
        int n = 0;
        for (int s = 0; s < Capacity; s++)
        {
            n += Building[s] >= 0 ? 1 : 0;
        }

        return n;
    }
}

/// <summary>
/// Бригады служб (building-lifecycle.md §2.10 <c>CrewStore</c>, SoA ёмкости <c>max_service_vehicles</c>): станция, состояние, цель, участок цели, машина, сроки.
/// Кольцо <c>CrewArrivals</c> (фаза 8 -&gt; 9r) пусто на границе тиков, в сейв не входит. Все массивы - при создании; 0 Б/тик.
/// </summary>
internal sealed class CrewStore
{
    public readonly int Capacity;
    public readonly int[] Site;         // станция или -1
    public readonly byte[] State;       // CrewState
    public readonly int[] Target;       // здание вызова или -1
    public readonly int[] TargetGen;
    public readonly int[] SiteLot;      // участок здания вызова (обратная поездка, если здание исчезло) или -1
    public readonly int[] Vehicle;      // машина бригады (micro) или -1, пока не поставлена
    public readonly int[] VehicleGen;
    public readonly long[] DueTick;     // micro: таймаут поездки / конец работы; агрегат: прибытие / конец работы
    public readonly long[] ReqTick;     // тик вызова (задержка)
    public readonly byte[] Kind;        // вид вызова в работе (ServiceCallKind; 0 - нет): сроки, действие на месте, OnCall; в хэше и сейве - только в режиме city.services
    public readonly int[] Ref;          // ссылка вызова в работе (critical - житель-пациент, SR3) или -1; в хэше - только в режиме city.services
    public readonly int[] RefGen;
    public readonly RingBuffer<int> Arrivals;   // CrewArrivals: бригады, чьи машины прибыли в этом тике

    public CrewStore(int crews)
    {
        Capacity = Math.Max(0, crews);
        Site = new int[Capacity];
        State = new byte[Capacity];
        Target = new int[Capacity];
        TargetGen = new int[Capacity];
        SiteLot = new int[Capacity];
        Vehicle = new int[Capacity];
        VehicleGen = new int[Capacity];
        DueTick = new long[Capacity];
        ReqTick = new long[Capacity];
        Kind = new byte[Capacity];
        Ref = new int[Capacity];
        RefGen = new int[Capacity];
        Array.Fill(Site, -1);
        Array.Fill(Ref, -1);
        Array.Fill(Target, -1);
        Array.Fill(SiteLot, -1);
        Array.Fill(Vehicle, -1);
        Arrivals = new RingBuffer<int>(Math.Max(1, Capacity));
    }

    /// <summary>Бригад в работе (не свободных слотов).</summary>
    public int Crews()
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++)
        {
            n += State[i] != CrewState.Free ? 1 : 0;
        }

        return n;
    }

    /// <summary>Свободных слотов бригад.</summary>
    public int FreeSlots() => Capacity - Crews();

    /// <summary>Слот в исходное (свободен).</summary>
    public void Reset(int k)
    {
        Site[k] = -1;
        State[k] = CrewState.Free;
        Target[k] = -1;
        TargetGen[k] = 0;
        SiteLot[k] = -1;
        Vehicle[k] = -1;
        VehicleGen[k] = 0;
        DueTick[k] = 0;
        ReqTick[k] = 0;
        Kind[k] = 0;
        Ref[k] = -1;
        RefGen[k] = 0;
    }
}

/// <summary>
/// Службы города (services.md §2.3, §3; SR1): записи, станции, кольца вызовов (на запись два - срочные и обычные, FIFO), бригады. Есть при <c>city.lifecycle</c>
/// (ЖКХ BL - запись <c>utility</c>, заявка на ремонт - обычное кольцо этой записи); режим <c>city.services</c> лишь заменяет встроенную запись таблицами пакетов.
/// Без режима хэш пишется ровно как в BL (очередь и бригады с депо-зданием). Всё создаётся в конструкторе; 0 Б/тик.
/// </summary>
internal sealed class CityServices
{
    public readonly ServiceTable Table;
    public readonly SiteStore Sites;
    public readonly CrewStore Crews;
    public readonly RingBuffer<ServiceCall>[] Normal;   // [запись]
    public readonly RingBuffer<ServiceCall>[] Urgent;   // [запись]

    // SR2 (services.md §2.3-§2.4, §2.9): ползунок бюджета, расходы, отклик. Pct/PctPending, SvcCostDay, RespEwma - в хэше только в режиме city.services.
    public readonly byte[] Pct;               // [запись] действующий ползунок 50..150 %, дефолт 100
    public readonly byte[] PctPending;        // [запись] ожидающий (с границы суток)
    public readonly long[] SvcCostDay;        // [запись * 2 + сутки & 1] расход записи за сутки (панель бюджета)
    public readonly long[] SiteVarDay;        // [станция * 2 + сутки & 1] переменная часть расхода станции: Σ per_call обслуженных вызовов (SR3 - и per_visit)
    public readonly long[] RecCallsQueued;    // [запись] вызовов поставлено в кольца
    public readonly long[] RecCallsDone;      // [запись] прибытий к цели
    public readonly long[] RecRespTicks;      // [запись] Σ отклика (прибытие - вызов), тики
    public readonly long[] RecCallsLate;      // [запись] опозданий сверх deadline_s (в ожидании, в пути, без станции)
    public readonly ushort[] RespEwma;        // [запись * клеток + клетка] отклик, с; EWMA >> 3; 0 - нет данных; только в режиме (иначе пусто)
    public readonly ushort[] CareEwma;        // [клетка дома] стоимость ноги к службе «к ним ходят», с; EWMA >> 3; 0 - нет данных (SR3, §2.9); только в режиме
    public readonly int Cells;                // клеток сетки охвата (NeighborhoodFields, 128 м); 0 - без режима
    internal readonly int[] IdleScratch;      // [станция] первая свободная бригада на линии при разборе колец; производное, вне сейва

    public CityServices(in CityDesc cfg, BuildingTypes types, int maxBuildings, int cells)
    {
        Table = new ServiceTable(in cfg, types);
        int crews = cfg.Lifecycle.MaxServiceVehicles;
        bool mode = Table.Explicit;

        // без режима станций не больше бригад (у депо >= 1 бригады): предел max_service_sites не меняет поведение BL
        Sites = new SiteStore(mode ? cfg.Services.MaxServiceSites : crews);
        Crews = new CrewStore(crews);
        int n = Table.Count;
        Pct = new byte[n];
        PctPending = new byte[n];
        Array.Fill(Pct, (byte)100);
        Array.Fill(PctPending, (byte)100);
        SvcCostDay = new long[2 * n];
        SiteVarDay = new long[2 * Sites.Capacity];
        RecCallsQueued = new long[n];
        RecCallsDone = new long[n];
        RecRespTicks = new long[n];
        RecCallsLate = new long[n];
        Cells = mode ? Math.Max(0, cells) : 0;
        RespEwma = new ushort[n * Cells];
        CareEwma = new ushort[Cells];
        IdleScratch = new int[Sites.Capacity];
        int calls = mode ? (cfg.Services.MaxCalls > 0 ? cfg.Services.MaxCalls : Math.Max(maxBuildings / 8, 256)) : Math.Max(1, maxBuildings);
        Normal = new RingBuffer<ServiceCall>[Table.Count];
        Urgent = new RingBuffer<ServiceCall>[Table.Count];
        for (int r = 0; r < Table.Count; r++)
        {
            Normal[r] = new RingBuffer<ServiceCall>(Table.HasCalls(r, false) ? calls : 1);
            Urgent[r] = new RingBuffer<ServiceCall>(Table.HasCalls(r, true) ? calls : 1);
        }
    }

    /// <summary>Кольцо заявок ЖКХ (обычное кольцо записи <c>utility</c>); null - записи ЖКХ нет.</summary>
    public RingBuffer<ServiceCall>? UtilityQueue => Table.Utility >= 0 ? Normal[Table.Utility] : null;

    /// <summary>Здание станции бригады <paramref name="k"/> или -1.</summary>
    public int SiteBuilding(int k) => Crews.Site[k] >= 0 ? Sites.Building[Crews.Site[k]] : -1;

    /// <summary>Поколение здания станции бригады <paramref name="k"/> (0 - нет станции).</summary>
    public int SiteGen(int k) => Crews.Site[k] >= 0 ? Sites.Gen[Crews.Site[k]] : 0;

    /// <summary>
    /// Новая станция здания <paramref name="bi"/>: первый свободный слот; станции снесённых зданий без бригад перед этим освобождаются (порядок детерминирован). -1 - мест нет.
    /// </summary>
    public int AllocSite(BuildingStore b, int bi, int type)
    {
        int found = -1;
        for (int s = 0; s < Sites.Capacity; s++)
        {
            if (Sites.Building[s] >= 0 && !b.Pool.IsAlive(Sites.Building[s], Sites.Gen[s]) && !Referenced(s))
            {
                Sites.Reset(s);
            }

            if (found < 0 && Sites.Building[s] < 0)
            {
                found = s;
            }
        }

        if (found < 0)
        {
            return -1;
        }

        Sites.Reset(found);
        Sites.Building[found] = bi;
        Sites.Gen[found] = b.Pool.GenerationOf(bi);
        Sites.Svc[found] = (byte)Math.Max(0, Table.RecOf(type));
        Sites.Level[found] = (byte)Table.LevelOf(type);
        return found;
    }

    /// <summary>Есть ли свободная станция (с учётом освобождаемых при <see cref="AllocSite"/>).</summary>
    public bool CanAllocSite(BuildingStore b)
    {
        for (int s = 0; s < Sites.Capacity; s++)
        {
            if (Sites.Building[s] < 0 || (!b.Pool.IsAlive(Sites.Building[s], Sites.Gen[s]) && !Referenced(s)))
            {
                return true;
            }
        }

        return false;
    }

    private bool Referenced(int s)
    {
        for (int k = 0; k < Crews.Capacity; k++)
        {
            if (Crews.State[k] != CrewState.Free && Crews.Site[k] == s)
            {
                return true;
            }
        }

        return false;
    }
}
