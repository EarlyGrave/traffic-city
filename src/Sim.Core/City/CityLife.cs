using System;
using Sim.Core.Api;
using Sim.Core.Terrain;

namespace Sim.Core.City;

/// <summary>Биты <see cref="BuildingStore.LifeFlags"/> (building-lifecycle.md §3). <c>Monument</c>, <c>Requested</c> - резерв BL3/BL4.</summary>
internal static class BuildingLifeFlags
{
    public const byte Monument = 1;
    public const byte Requested = 2;
    public const byte Demolishing = 4;
    public const byte WornNotified = 8;
}

/// <summary>
/// Жизнь здания (building-lifecycle.md §2.2-§2.5, BL1): износ и потолок раз в сутки в стадии «здания», пороги (выработка, жалобы, случаи K4, переезд, заброшенность), заброшенное
/// здание стоит и рассыпается, снос игроком, повторное заселение. Только в режиме <c>city.lifecycle</c> (<c>Cfg.Lifecycle.Enabled</c>); без него ни одна ветка не исполняется.
/// Всё - в последовательных фазах главного потока (дневной проход, команды фазы 1), без PRNG, кроме жребия переезда в <c>Market.ResidentDay</c>; 0 Б/тик.
/// </summary>
internal static class CityLife
{
    public const int Full = 10_000;          // 100 %, б.п.
    public const int NormBp = 9_000;         // >= 90 % - норма
    public const int MinorBp = 7_000;        // 70-90 % - мелкие сбои; >= 70 % - повторное заселение
    public const int RelocateBp = 6_000;     // 50-60 % - переезд жителей
    public const int AbandonBp = 5_000;      // < 50 % - не функционирует, ремонту не подлежит
    public const int LowlandSiteQ8 = 320;    // +25 % износа на низине
    public const int SiteQ8 = 256;
    private const int LowlandRadius = 2;     // отсчётов рельефа вокруг центра участка

    /// <summary>Новое здание (стройка или стартовое): <c>Cond = Ceil = 100 %</c>, возраст - с текущих суток.</summary>
    public static void OnCreate(CityWorld c, int bi)
    {
        if (!c.Cfg.Lifecycle.Enabled)
        {
            return;
        }

        BuildingStore b = c.Buildings;
        b.Cond[bi] = Full;
        b.Ceil[bi] = Full;
        b.BornDay[bi] = c.Day;
        b.LifeFlags[bi] = 0;
    }

    /// <summary>Конец стройки (§2.2): здание новое - <c>Cond = Ceil = 100 %</c>, возраст с суток <c>Complete</c>.</summary>
    public static void OnComplete(CityWorld c, int bi) => OnCreate(c, bi);

    /// <summary>Выработка C/I, % (§2.2): 100 при <c>Cond</c> &gt;= 90 %, 95 при 70-90 %, ниже - 80. Без режима - 100 (буст отложен, §2.7).</summary>
    public static int OutPct(CityWorld c, int bi)
    {
        if (!c.Cfg.Lifecycle.Enabled)
        {
            return 100;
        }

        int cond = c.Buildings.Cond[bi];
        return cond >= NormBp ? 100 : cond >= MinorBp ? 95 : 80;
    }

    /// <summary>Дом в полосе переезда (§2.2, 50-60 %): житель раз в сутки с шансом <c>worn_relocate</c> пробует переехать.</summary>
    public static bool InRelocateBand(CityWorld c, int home)
    {
        BuildingStore b = c.Buildings;
        int cond = b.Cond[home];
        return b.State[home] == (byte)BuildingState.Active && cond >= AbandonBp && cond < RelocateBp;
    }

    /// <summary>
    /// Суточный шаг здания в стадии «здания» (§2.2), до экономики фирмы. Износ и потолок - у <c>Active</c> и <c>Abandoned</c>; пороги - у <c>Active</c>.
    /// true - экономику фирмы сегодня пропустить (заброшено сейчас или раньше).
    /// </summary>
    public static bool Day(World w, CityWorld c, int bi, in BuildingTypeDesc t)
    {
        BuildingStore b = c.Buildings;
        bool abandoned = b.State[bi] == (byte)BuildingState.Abandoned;
        if (t.Kind is not (LotZone.Residential or LotZone.Commercial or LotZone.Industrial))
        {
            return false;
        }

        int loadQ8 = abandoned ? 256 * c.Cfg.Lifecycle.AbandonedDecayPct / 100 : LoadQ8(c, bi, in t);
        int siteQ8 = IsLowland(w, c, b.Lot[bi]) ? LowlandSiteQ8 : SiteQ8;
        int ceil = Math.Max(0, b.Ceil[bi] - t.CeilDropBpDay);
        long drop = ((long)t.DecayBpDay * loadQ8 * siteQ8) >> 16;
        int cond = (int)Math.Max(0L, Math.Min(b.Cond[bi] - drop, ceil));
        b.Ceil[bi] = (ushort)ceil;
        b.Cond[bi] = (ushort)cond;
        if (abandoned)
        {
            return true;
        }

        int gen = b.Pool.GenerationOf(bi);
        if (cond >= NormBp)
        {
            b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.WornNotified);
        }
        else if ((b.LifeFlags[bi] & BuildingLifeFlags.WornNotified) == 0)
        {
            b.LifeFlags[bi] |= BuildingLifeFlags.WornNotified;   // жалоба один раз при пересечении 90 % вниз
            c.Kpi.Complaints.Add(ComplaintKind.BuildingWorn, bi, gen, Metrics.Complaints.MinCount);
        }

        if (cond < AbandonBp)
        {
            Growth.Abandon(w, c, bi);   // не функционирует, ремонту не подлежит (решение владельца 2026-10-30)
            return true;
        }

        if (cond < MinorBp)
        {
            int incidents = b.Occupants[bi] / 4;   // перебои: случаи K4 Occupants/4 в сутки и жалоба
            if (incidents > 0)
            {
                c.Kpi.OnBuildingWorn(bi, gen, incidents);
            }
        }

        return false;
    }

    /// <summary>
    /// Множитель загрузки (§2.2), Q8: <c>256 + 128 · clamp(load‰ − 900, 0, 100) / 100</c>; <c>load</c> - занятость к вместимости (R, I) или вчерашние визиты к дневной вместимости (C).
    /// </summary>
    public static int LoadQ8(CityWorld c, int bi, in BuildingTypeDesc t)
    {
        BuildingStore b = c.Buildings;
        long num;
        long den;
        if (t.Kind == LotZone.Commercial)
        {
            num = b.VisitsDay[Economy.Slot(bi, (c.Day + 1) & 1)];
            den = (long)t.ShopCapacity * Math.Max(1, t.ShiftSlots / 12);
        }
        else
        {
            num = b.Occupants[bi];
            den = t.Capacity;
        }

        if (den <= 0)
        {
            return 256;
        }

        long permille = num * 1000 / den;
        long over = Math.Clamp(permille - 900, 0L, 100L);
        return (int)(256 + (128 * over / 100));
    }

    /// <summary>
    /// «Низина» (§2.2): грунт центра участка не выше уровня воды ближайшего мокрого отсчёта в радиусе 2 отсчётов плюс <c>lowland_m</c>. Без рельефа - false.
    /// Считается на месте (раз в сутки на здание, ≤ 25 отсчётов), не кэшируется: производное, вне сейва и хэша.
    /// </summary>
    public static bool IsLowland(World w, CityWorld c, int lot)
    {
        TerrainStore? t = w.Terrain;
        if (t == null || (uint)lot >= (uint)c.Lots.X.Length)
        {
            return false;
        }

        int center = t.NearestSample(c.Lots.X[lot], c.Lots.Z[lot]);
        int cx = center % t.SamplesX;
        int cz = center / t.SamplesX;
        int best = -1;
        int bestD = int.MaxValue;
        for (int dz = -LowlandRadius; dz <= LowlandRadius; dz++)
        {
            int z = cz + dz;
            if (z < 0 || z >= t.SamplesZ)
            {
                continue;
            }

            for (int dx = -LowlandRadius; dx <= LowlandRadius; dx++)
            {
                int x = cx + dx;
                if (x < 0 || x >= t.SamplesX)
                {
                    continue;
                }

                int j = (z * t.SamplesX) + x;
                int d = (dx * dx) + (dz * dz);
                if (t.Water[j] > t.Ground[j] && d < bestD)
                {
                    best = j;
                    bestD = d;
                }
            }
        }

        return best >= 0 && t.Ground[center] <= t.Water[best] + c.Cfg.Lifecycle.LowlandCm;
    }

    /// <summary>
    /// Снос игроком (§2.5), после валидатора и до проводки цены: заброшенность (если ещё нет), флаг <c>Demolishing</c>, оставшимся жителям - <c>Leaving</c>.
    /// Удаление - ближайший суточный <c>RemoveAbandoned</c> с прежними проверками ссылок.
    /// </summary>
    public static void Demolish(World w, CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        CityResidents res = c.Residents;
        Growth.Abandon(w, c, bi);
        b.LifeFlags[bi] |= BuildingLifeFlags.Demolishing;
        b.Version++;   // клиент видит «сносится»
        for (int r = b.HeadRes[bi]; r >= 0; r = res.NextHome[r])
        {
            res.Flags[r] |= ResidentFlags.Leaving;
        }

        w.Counters.BuildingsDemolished++;
    }

    /// <summary>Заброшенное здание к удалению (§2.3): снос игроком или рассыпалось (<c>Cond = 0</c>).</summary>
    public static bool ToRemove(CityWorld c, int bi) =>
        (c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Demolishing) != 0 || c.Buildings.Cond[bi] == 0;

    /// <summary>Перед удалением (§2.5): рассыпавшееся без сноса - дешёвая очистка из казны (<c>rubble_cost_pct</c> от <c>demolish_cost</c>, поток <c>Construction</c>).</summary>
    public static void OnRemove(World w, CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        if ((b.LifeFlags[bi] & BuildingLifeFlags.Demolishing) != 0)
        {
            return;
        }

        long rubble = (long)c.Types.Rows[b.Type[bi]].DemolishCost * c.Cfg.Lifecycle.RubbleCostPct / 100;
        if (rubble > 0)
        {
            c.Ledger.Transfer(c.Ledger.CityAccount, c.Ledger.ExternalAccount, rubble, FlowKind.Construction);
        }

        w.Counters.BuildingsCrumbled++;
    }

    /// <summary>
    /// Заброшенное здание на участке годится для повторного заселения (§2.3): вид типа = зона участка, <c>Cond</c> &gt;= 70 %, не сносится, пустое (жители и работники ушли).
    /// Участок <c>Lost</c> отсекает вызывающий.
    /// </summary>
    public static bool Resettable(CityWorld c, int bi, LotZone zone)
    {
        BuildingStore b = c.Buildings;
        return b.IsAlive(bi) && b.State[bi] == (byte)BuildingState.Abandoned && c.Types.Rows[b.Type[bi]].Kind == zone && b.Cond[bi] >= MinorBp
            && (b.LifeFlags[bi] & BuildingLifeFlags.Demolishing) == 0 && b.Occupants[bi] == 0;
    }

    /// <summary>
    /// Повторное заселение (§2.3): <c>Abandoned -> Active</c> без стройки. Фирма: старый счёт закрывается за город (как при сносе), стартовый счёт, все места открыты.
    /// Жильё пустое - заселяет рынок. Состояние, потолок и возраст сохраняются.
    /// </summary>
    public static void Resettle(World w, CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
        if (t.Kind != LotZone.Residential)
        {
            int acct = c.Ledger.AccountOfBuilding(bi);
            c.Ledger.Transfer(acct, c.Ledger.ExternalAccount, b.Account[bi], FlowKind.Emigration);
            c.Ledger.Transfer(c.Ledger.ExternalAccount, acct, c.Cfg.Economy.InitialFirmBalance, FlowKind.Endowment);
            b.Jobs[bi] = t.Capacity;
        }

        b.RevenueEwma[bi] = 0;
        b.ProfitEwma[bi] = 0;
        b.PayrollEwma[bi] = 0;
        b.LossDays[bi] = 0;
        b.VacantDays[bi] = 0;
        b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.WornNotified);
        b.State[bi] = (byte)BuildingState.Active;
        b.StateTick[bi] = w.Tick;
        b.Version++;
        c.Lots.Version++;
        Parking.ParkingBuild.OnBuildingActive(w, c, bi);
        w.Counters.BuildingsResettled++;
    }

    /// <summary>Биты <see cref="BuildingRenderData.R1"/> (§3, снапшот): <c>Requested 2</c>, <c>Demolishing 4</c>; <c>Monument 1</c>, <c>Boost 8</c> - резерв.</summary>
    public static ushort RenderFlags(CityWorld c, int bi) =>
        (ushort)(c.Buildings.LifeFlags[bi] & (BuildingLifeFlags.Requested | BuildingLifeFlags.Demolishing));
}
