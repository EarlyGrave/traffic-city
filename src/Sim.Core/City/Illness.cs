using System;
using Sim.Core.Api;
using Sim.Core.Demand;

namespace Sim.Core.City;

/// <summary>
/// Болезнь и «к ним ходят» (services.md §2.5-§2.6, ADR-0041, SR3; только в режиме <c>city.services</c>): байт жителя <see cref="CityResidents.Ill"/>, бросок хэшем без PRNG-потока в плане дня,
/// тур <see cref="TourKind.Care"/> к станции с местом, приём (койка - счётчик станции с кольцом выписки), вызов <c>critical</c> к дому, приём в реанимацию, заглушка исхода «житель уезжает».
/// <para>
/// Длительность: при заболевании <c>days = 2 · ill_days</c> (столько болеет без лечения - «дольше болеют, до ×2»); приём снимает <c>ill_days</c>: <c>days = max(1, days − ill_days)</c>.
/// Раз в сутки в плане дня <c>days−−</c>, 0 - здоров. Реанимация сверх мест - приём без сокращения (×2).
/// </para>
/// Связи житель ↔ больница нет. Всё - в последовательных фазах главного потока; 0 Б/тик.
/// </summary>
internal static class Illness
{
    public const int Light = 1;
    public const int Inpatient = 2;
    public const int Critical = 3;
    public const byte Treated = 128;
    private const int DaysMask = 31;
    private const int SevShift = 5;

    /// <summary>Желаемое прибытие к врачу: 09:00 (слот 108) плюс сдвиг жителя <c>Seq % 24</c> (§2.6).</summary>
    public const int CareSlot = 108;

    /// <summary>Исход критического без скорой или с опозданием (§10.3, §10.4): v1 - заглушка «уезжает»; SR7 добавит «умирает» (поведение данными).</summary>
    public enum CriticalOutcome : byte
    {
        Leaves = 0,
    }

    public static int Days(byte ill) => ill & DaysMask;

    public static int Severity(byte ill) => (ill >> SevShift) & 3;

    public static bool IsTreated(byte ill) => (ill & Treated) != 0;

    public static byte Make(int sev, int days, bool treated) => (byte)((treated ? Treated : 0) | (sev << SevShift) | Math.Clamp(days, 0, DaysMask));

    /// <summary>Длительность тяжести <paramref name="sev"/> при лечении (<c>ill_days</c>), сутки.</summary>
    public static int BaseDays(in CityServicesDesc s, int sev) => sev switch
    {
        Light => s.IllDaysLight,
        Inpatient => s.IllDaysInpatient,
        _ => s.IllDaysCritical,
    };

    /// <summary>
    /// Бросок (§2.5): <c>u = Mix64(seed ^ day·K1 ^ r·K2 ^ gen) mod 10⁶</c> (финализатор SplitMix64) - не зависит от порядка и числа потоков, в сейв не входит.
    /// </summary>
    public static int RollPpm(ulong seed, int day, int r, int gen)
    {
        ulong x = seed ^ ((ulong)(uint)day * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)r * 0xC2B2AE3D27D4EB4FUL) ^ (uint)gen;
        x ^= x >> 30;
        x *= 0xbf58476d1ce4e5b9UL;
        x ^= x >> 27;
        x *= 0x94d049bb133111ebUL;
        x ^= x >> 31;
        return (int)(x % 1_000_000UL);
    }

    /// <summary>Тяжесть по броску <paramref name="u"/> (ppm): сначала критическая, затем стационар, затем лёгкая; пороги × <paramref name="pct"/> / 100. 0 - здоров.</summary>
    public static int SeverityOf(int u, in CityServicesDesc s, int pct)
    {
        long crit = (long)s.IllCriticalPpm * pct / 100;
        long inp = crit + ((long)s.IllInpatientPpm * pct / 100);
        long light = inp + ((long)s.IllLightPpm * pct / 100);
        return u < crit ? Critical : u < inp ? Inpatient : u < light ? Light : 0;
    }

    /// <summary>
    /// План дня больного (из <c>DayPlanner.Build</c>, житель дома, раз в сутки). Болел - <c>days−−</c> (0 - здоров, план обычный); здоров - бросок. Больной вместо всех туров: критический -
    /// вызов скорой в день заболевания, дальше дома без туров; лёгкий и стационарный без приёма - один тур <see cref="TourKind.Care"/> (§2.6); принятый - дома. true - обычных туров нет.
    /// </summary>
    public static bool PlanDay(World w, CityWorld c, int r, int day, ref int n, ref int cursor)
    {
        CityResidents res = c.Residents;
        ref readonly CityServicesDesc cfg = ref c.Cfg.Services;
        byte ill = res.Ill[r];
        if (ill != 0)
        {
            int d = Days(ill) - 1;
            if (d <= 0)
            {
                res.Ill[r] = 0;
                res.Flags[r] = (byte)(res.Flags[r] & ~ResidentFlags.CareRefused);
                return false;
            }

            ill = (byte)((ill & ~DaysMask) | d);
            res.Ill[r] = ill;
        }
        else
        {
            int gen = res.Pool.GenerationOf(r);
            int sev = SeverityOf(RollPpm(w.Seed, day, r, gen), in cfg, DisorderPct(c, res.Home[r]));
            if (sev == 0)
            {
                return false;
            }

            Onset(w, c, r, sev);
            if (sev == Critical)
            {
                return true;
            }

            ill = res.Ill[r];
        }

        int s = Severity(ill);
        if (s != Critical && !IsTreated(ill))
        {
            PlanCare(w, c, r, day, s, ref n, ref cursor);
        }

        return true;
    }

    /// <summary>Заболевание тяжести <paramref name="sev"/>: <c>days = 2 · ill_days</c> (без лечения), счётчик; критический - вызов скорой к дому.</summary>
    internal static void Onset(World w, CityWorld c, int r, int sev)
    {
        CityResidents res = c.Residents;
        res.Ill[r] = Make(sev, 2 * BaseDays(in c.Cfg.Services, sev), false);
        w.Counters.IllStarted++;
        if (sev == Critical)
        {
            CallAmbulance(w, c, r, res.Pool.GenerationOf(r));
        }
    }

    /// <summary>Множитель частоты, %: <c>disorder_ill_pct</c> при <c>Disorder &gt; 0</c> в окне 3×3 клетки дома (BL), иначе 100.</summary>
    private static int DisorderPct(CityWorld c, int home)
    {
        NeighborhoodFields? f = c.Fields;
        if (f == null || home < 0)
        {
            return 100;
        }

        int lot = c.Buildings.Lot[home];
        return f.Window(f.CellOf(c.Lots.X[lot], c.Lots.Z[lot]), NeighborhoodFields.Disorder) > 0 ? c.Cfg.Services.DisorderIllPct : 100;
    }

    /// <summary>Вызов <c>critical</c> к дому (первая запись с этим вызовом); записи нет или кольцо полно - сразу исход §10.3 (скорой нет).</summary>
    private static void CallAmbulance(World w, CityWorld c, int r, int gen)
    {
        CityServices sv = c.Services!;
        int rec = sv.Table.RecordWithCall(ServiceCallKind.Critical);
        int home = c.Residents.Home[r];
        if (rec < 0 || home < 0 || !Utilities.Call(w, c, rec, ServiceCallKind.Critical, home, r, gen))
        {
            Lose(w, c, r, gen);
        }
    }

    /// <summary>
    /// Исход критического без скорой или с опозданием (решения владельца 10.3, 10.4; <see cref="CriticalOutcome.Leaves"/>): житель уезжает (<c>Leaving</c>, отъезд в ближайшей точке «дома»),
    /// случай K4 и жалоба <c>NoAmbulance</c> у дома. Житель уже не тот, не критический или принят - ничего.
    /// </summary>
    public static void Lose(World w, CityWorld c, int r, int gen)
    {
        CityResidents res = c.Residents;
        if (r < 0 || !res.Pool.IsAlive(r, gen))
        {
            return;
        }

        byte ill = res.Ill[r];
        if (Severity(ill) != Critical || IsTreated(ill))
        {
            return;
        }

        res.Flags[r] |= ResidentFlags.Leaving;
        int home = res.Home[r];
        if (home >= 0)
        {
            c.Kpi.OnNoAmbulance(home, c.Buildings.Pool.GenerationOf(home));
        }

        w.Counters.ResidentsLostNoAmbulance++;
    }

    /// <summary>
    /// Выбор станции и запись (§2.6): станции записей с поводом тяжести и ролью <c>min_role</c>, открытые к желаемому прибытию, с местом (<c>VisitsDay &lt; capacity_day'</c>, для стационара
    /// <c>BedsUsed &lt; beds'</c>), не дальше <c>max_cost_s</c>; минимум стоимости лучшего способа (<c>Choice.LegCost</c>), равенство - меньший индекс станции. Нет - отказ.
    /// </summary>
    private static void PlanCare(World w, CityWorld c, int r, int day, int sev, ref int n, ref int cursor)
    {
        CityResidents res = c.Residents;
        CityServices sv = c.Services!;
        SiteStore s = sv.Sites;
        BuildingStore b = c.Buildings;
        int home = res.Home[r];
        if (home < 0)
        {
            return;
        }

        int homeLot = b.Lot[home];
        ServiceVisitReason reason = sev == Light ? ServiceVisitReason.Light : ServiceVisitReason.Inpatient;
        int desired = CareSlot + (res.Seq[r] % 24);
        int period = c.PeriodOf(c.TickOfSlot(day, desired));
        bool hasCar = (res.Flags[r] & ResidentFlags.HasCar) != 0;
        Skims sk = c.Skims;
        sk.Prepare(w);
        int parity = day & 1;
        int best = -1;
        int bestCost = int.MaxValue;
        for (int i = 0; i < s.Capacity; i++)
        {
            int bi = s.Building[i];
            if (bi < 0 || !b.Pool.IsAlive(bi, s.Gen[i]) || b.State[bi] != (byte)BuildingState.Active)
            {
                continue;
            }

            int rec = s.Svc[i];
            int vi = sv.Table.VisitOf(rec, reason);
            if (vi < 0)
            {
                continue;
            }

            ref readonly ServiceVisitDesc vd = ref sv.Table.Visits[vi];
            ref readonly ServiceLevelDesc l = ref sv.Table.LevelRow(rec, s.Level[i]);
            if ((vd.MinRole != ServiceRole.None && (l.Roles & vd.MinRole) == 0) || desired < l.OpenSlot || desired >= l.CloseSlot)
            {
                continue;
            }

            if (b.VisitsDay[Economy.Slot(bi, parity)] >= VisitCap(sv, i) || (sev == Inpatient && s.BedsUsed[i] >= BedsCap(sv, i)))
            {
                continue;
            }

            int cost = BestCost(c, sk, hasCar, homeLot, b.Lot[bi], period);
            if (cost == DetLogit.NoCost || (vd.MaxCostS > 0 && cost > vd.MaxCostS * 10L))
            {
                continue;
            }

            if (cost < bestCost)
            {
                best = i;
                bestCost = cost;
            }
        }

        if (best < 0)
        {
            Refuse(w, c, r);
            return;
        }

        int station = s.Building[best];
        int before = n;
        int stay = Math.Max(1, (int)sv.Table.LevelRow(s.Svc[best], s.Level[best]).StaySlots);
        DayPlanner.AddTour(w, c, r, day, TourKind.Care, station, desired, stay, ref n, ref cursor);
        if (n == before)
        {
            Refuse(w, c, r);   // ни один способ не доступен или тур не помещается в сутки
            return;
        }

        b.VisitsDay[Economy.Slot(station, parity)]++;   // запись на приём
        res.Flags[r] = (byte)(res.Flags[r] & ~ResidentFlags.CareRefused);
    }

    private static int BestCost(CityWorld c, Skims sk, bool hasCar, int fromLot, int toLot, int period)
    {
        int best = DetLogit.NoCost;
        for (int m = 0; m < Skims.Modes; m++)
        {
            int v = Choice.LegCost(c, sk, hasCar, fromLot, toLot, (TravelMode)m, period);
            if (v != DetLogit.NoCost && (best == DetLogit.NoCost || v < best))
            {
                best = v;
            }
        }

        return best;
    }

    /// <summary>Отказ (§2.6): счётчик; второй подряд (флаг <c>CareRefused</c> уже стоит) - случай K4 и жалоба <c>NoCare</c> у дома. Приёма нет - болезнь длиннее.</summary>
    private static void Refuse(World w, CityWorld c, int r)
    {
        CityResidents res = c.Residents;
        w.Counters.CareRefused++;
        int home = res.Home[r];
        if ((res.Flags[r] & ResidentFlags.CareRefused) != 0 && home >= 0)
        {
            c.Kpi.OnNoCare(home, c.Buildings.Pool.GenerationOf(home));
        }

        res.Flags[r] |= ResidentFlags.CareRefused;
    }

    /// <summary><c>capacity_day'</c> станции: (<c>capacity_day</c> + апгрейды <c>visit_capacity</c>) × ползунок / 100 (§2.4).</summary>
    public static int VisitCap(CityServices sv, int site) => Scaled(sv, site, ServiceUpgradeEffect.VisitCapacity);

    /// <summary><c>beds'</c> станции: (<c>beds</c> + апгрейды <c>beds</c>) × ползунок / 100.</summary>
    public static int BedsCap(CityServices sv, int site) => Scaled(sv, site, ServiceUpgradeEffect.Beds);

    /// <summary><c>icu'</c> станции: (<c>icu</c> + апгрейды <c>icu</c>) × ползунок / 100.</summary>
    public static int IcuCap(CityServices sv, int site) => Scaled(sv, site, ServiceUpgradeEffect.Icu);

    private static int Scaled(CityServices sv, int site, ServiceUpgradeEffect e)
    {
        int rec = sv.Sites.Svc[site];
        ref readonly ServiceLevelDesc l = ref sv.Table.LevelRow(rec, sv.Sites.Level[site]);
        int v = e switch
        {
            ServiceUpgradeEffect.VisitCapacity => l.CapacityDay,
            ServiceUpgradeEffect.Beds => l.Beds,
            _ => l.Icu,
        };
        return (int)((long)(v + sv.Table.UpgradeEffect(in l, sv.Sites.UpgradeMask[site], e)) * sv.Pct[rec] / 100);
    }

    /// <summary>Станция здания <paramref name="bi"/> (живое, то же поколение, <c>Active</c>) или -1.</summary>
    private static int SiteOf(CityWorld c, CityServices sv, int bi)
    {
        BuildingStore b = c.Buildings;
        if (bi < 0 || !b.IsAlive(bi) || b.State[bi] != (byte)BuildingState.Active)
        {
            return -1;
        }

        int gen = b.Pool.GenerationOf(bi);
        SiteStore s = sv.Sites;
        for (int i = 0; i < s.Capacity; i++)
        {
            if (s.Building[i] == bi && s.Gen[i] == gen)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Прибытие тура <see cref="TourKind.Care"/> (<c>CompleteLeg</c>, micro и агрегат - один путь, §2.6): станция жива - приём (<c>Treated</c>; стационар - койка до выписки, коек нет - отказ),
    /// переменная часть расхода <c>per_visit</c>, счётчик, стоимость ноги в <c>CareEwma</c> клетки дома. Станции нет - отказ. Возвращает длительность визита, слоты.
    /// </summary>
    public static int OnCareArrived(World w, CityWorld c, int r, int station, int costDs)
    {
        CityResidents res = c.Residents;
        CityServices? sv = c.Services;
        if (sv == null)
        {
            return 1;
        }

        int site = SiteOf(c, sv, station);
        byte ill = res.Ill[r];
        int sev = Severity(ill);
        if (ill == 0 || IsTreated(ill) || sev == Critical)
        {
            return 1;
        }

        if (site < 0)
        {
            Refuse(w, c, r);
            return 1;
        }

        SiteStore s = sv.Sites;
        int rec = s.Svc[site];
        int stay = Math.Max(1, (int)sv.Table.LevelRow(rec, s.Level[site]).StaySlots);
        if (sev == Inpatient && s.BedsUsed[site] >= BedsCap(sv, site))
        {
            Refuse(w, c, r);   // койки заняли раньше, пока ехал
            return stay;
        }

        int days = Math.Max(1, Days(ill) - BaseDays(in c.Cfg.Services, sev));
        if (sev == Inpatient)
        {
            s.BedsUsed[site]++;
            s.BedRelease[(site * SiteStore.ReleaseDays) + ReleaseSlot(c.Day, days)]++;
        }

        res.Ill[r] = Make(sev, days, true);
        int vi = sv.Table.VisitOf(rec, sev == Light ? ServiceVisitReason.Light : ServiceVisitReason.Inpatient);
        if (vi >= 0)
        {
            sv.SiteVarDay[(site * 2) + (c.Day & 1)] += sv.Table.Visits[vi].PerVisit;
        }

        w.Counters.CareVisits++;
        AddCareEwma(c, sv, res.Home[r], costDs);
        return stay;
    }

    /// <summary>Слот кольца выписки (16 суток): сутки <paramref name="day"/> + <paramref name="days"/> (не дальше 15).</summary>
    public static int ReleaseSlot(int day, int days) => (day + Math.Min(days, SiteStore.ReleaseDays - 1)) & (SiteStore.ReleaseDays - 1);

    private static void AddCareEwma(CityWorld c, CityServices sv, int home, int costDs)
    {
        NeighborhoodFields? f = c.Fields;
        if (f == null || sv.Cells == 0 || home < 0 || !c.Buildings.IsAlive(home))
        {
            return;
        }

        int lot = c.Buildings.Lot[home];
        int cell = f.CellOf(c.Lots.X[lot], c.Lots.Z[lot]);
        int v = Math.Clamp(costDs / 10, 1, ushort.MaxValue);
        int e = sv.CareEwma[cell];
        sv.CareEwma[cell] = (ushort)(e == 0 ? v : Math.Clamp(e + ((v - e) >> 3), 1, ushort.MaxValue));
    }

    /// <summary>
    /// Доставка критического на станцию скорой (§2.3: база = медцентр, роль <c>critical</c>): приём в реанимацию (<c>Treated</c>, <c>IcuUsed++</c>, выписка через <c>days</c>); мест нет - всё
    /// равно принят без сокращения болезни (×2), счётчик <c>IcuOverflow</c>.
    /// </summary>
    public static void AdmitIcu(World w, CityWorld c, CityServices sv, int site, int r, int gen)
    {
        CityResidents res = c.Residents;
        if (r < 0 || !res.Pool.IsAlive(r, gen))
        {
            return;
        }

        byte ill = res.Ill[r];
        if (Severity(ill) != Critical || IsTreated(ill))
        {
            return;
        }

        SiteStore s = sv.Sites;
        bool over = s.IcuUsed[site] >= IcuCap(sv, site);
        int days = over ? Math.Max(1, Days(ill)) : Math.Max(1, Days(ill) - BaseDays(in c.Cfg.Services, Critical));
        s.IcuUsed[site]++;
        s.IcuRelease[(site * SiteStore.ReleaseDays) + ReleaseSlot(c.Day, days)]++;
        res.Ill[r] = Make(Critical, days, true);
        w.Counters.IcuAdmitted++;
        if (over)
        {
            w.Counters.IcuOverflow++;
        }
    }

    /// <summary>Пациент ещё ждёт: живой житель того же поколения, критический, не принят.</summary>
    public static bool Waiting(CityWorld c, int r, int gen)
    {
        CityResidents res = c.Residents;
        return r >= 0 && res.Pool.IsAlive(r, gen) && Severity(res.Ill[r]) == Critical && !IsTreated(res.Ill[r]);
    }

    /// <summary>Граница суток (режим, после <c>Day++</c>): выписка коек и реанимации слота новых суток.</summary>
    public static void ReleaseDay(CityWorld c, CityServices sv)
    {
        SiteStore s = sv.Sites;
        int d = c.Day & (SiteStore.ReleaseDays - 1);
        for (int i = 0; i < s.Capacity; i++)
        {
            int idx = (i * SiteStore.ReleaseDays) + d;
            s.BedsUsed[i] -= s.BedRelease[idx];
            s.IcuUsed[i] -= s.IcuRelease[idx];
            s.BedRelease[idx] = 0;
            s.IcuRelease[idx] = 0;
        }
    }
}
