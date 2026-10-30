using System;
using Sim.Core.Api;
using Sim.Core.Terrain;

namespace Sim.Core.City;

/// <summary>Биты <see cref="BuildingStore.LifeFlags"/> (building-lifecycle.md §3). <c>Requested</c> - заявка ЖКХ в очереди или у бригады (BL3); <c>Monument</c> - резерв BL4.</summary>
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
    public const int H1Bp = 3_500;           // заброшка: >= 35 % - H1, 15-35 % - H2, ниже - H3 (§2.4)
    public const int H2Bp = 1_500;
    public const int DisorderOutPenalty = 10;   // выработка фирмы в клетке с беспорядком, п.п.
    private const int LowlandRadius = 2;     // отсчётов рельефа вокруг центра участка
    private static readonly int[] _signifLevels = { 1_280, 6_400, 19_200, 38_400, 64_000 };   // пороги накопителя значимости (стационар 20/100/300/600/1000 прибытий в сутки)

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
        int pct = cond >= NormBp ? 100 : cond >= MinorBp ? 95 : 80;
        NeighborhoodFields? f = c.Fields;
        if (f != null)
        {
            int lot = c.Buildings.Lot[bi];
            if (f.Value(f.CellOf(c.Lots.X[lot], c.Lots.Z[lot]), NeighborhoodFields.Disorder) > 0)
            {
                pct -= DisorderOutPenalty;   // беспорядок в клетке (стадия вреда H2-H3, §2.4): фирма -10 %
            }
        }

        return pct;
    }

    /// <summary>
    /// Порог жребия добровольного переезда (§2.6): <paramref name="baseQ16"/> × <c>(1 − min(50 %, 2 %·h))</c> (наследие вокруг дома держит жителей) и × 2 при <c>Disorder &gt; 0</c> в окне 3×3.
    /// Число обращений к PRNG прежнее; без режима - <paramref name="baseQ16"/>.
    /// </summary>
    public static int RelocateThreshold(CityWorld c, int baseQ16, int home)
    {
        NeighborhoodFields? f = c.Fields;
        if (f == null || home < 0)
        {
            return baseQ16;
        }

        int lot = c.Buildings.Lot[home];
        int cell = f.CellOf(c.Lots.X[lot], c.Lots.Z[lot]);
        long h = f.Window(cell, NeighborhoodFields.Heritage);
        long q = (long)baseQ16 * (1000 - Math.Min(500L, 20 * h)) / 1000;
        if (f.Window(cell, NeighborhoodFields.Disorder) > 0)
        {
            q *= 2;
        }

        return (int)Math.Min(q, 65_536L);
    }

    /// <summary>Уровень значимости 0-5 по накопителю (§2.6): пороги стационара 20 / 100 / 300 / 600 / 1 000 прибытий в сутки при утечке <c>2^-6</c>.</summary>
    public static int Level(int signif)
    {
        int lv = 0;
        while (lv < _signifLevels.Length && signif >= _signifLevels[lv])
        {
            lv++;
        }

        return lv;
    }

    /// <summary>Потолок суточных прибытий в накопитель значимости (§2.6).</summary>
    public const int ArrivalsCap = 4_000;

    /// <summary>Прибытие ноги в здание-цель (§2.6): +1 к сегодняшнему слоту; внешние места и мёртвые слоты не считаются. Micro и агрегат - одна точка (<c>CompleteLeg</c>).</summary>
    public static void OnArrival(CityWorld c, int dest)
    {
        BuildingStore b = c.Buildings;
        if (dest >= 0 && b.IsAlive(dest))
        {
            b.ArrivalsDay[Economy.Slot(dest, c.Day & 1)]++;
        }
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
    public static bool Day(World w, CityWorld c, int bi, in BuildingTypeDesc t) => DayCore(w, c, bi, in t);

    /// <summary>
    /// Конец суточного шага здания (после экономики фирмы, когда состояние на сегодня окончательное): сглаживание значимости и вклад в поля квартала (§2.6).
    /// Стройка (<c>Constructing</c>) и не-R/C/I виды не участвуют.
    /// </summary>
    public static void Finish(CityWorld c, int bi, in BuildingTypeDesc t)
    {
        if (t.Kind is not (LotZone.Residential or LotZone.Commercial or LotZone.Industrial))
        {
            return;
        }

        var st = (BuildingState)c.Buildings.State[bi];
        if (st is not (BuildingState.Active or BuildingState.Abandoned))
        {
            return;
        }

        Signif(c, bi);
        if (c.Fields != null)
        {
            Contribute(c, bi, in t, c.Fields.Next, true);
        }
    }

    /// <summary>Сглаживание значимости раз в сутки (§2.6): <c>Signif += min(A, 4000) − (Signif &gt;&gt; sig_shift)</c> по вчерашнему слоту прибытий.</summary>
    private static void Signif(CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        int a = Math.Min(b.ArrivalsDay[Economy.Slot(bi, (c.Day + 1) & 1)], ArrivalsCap);
        b.Signif[bi] += a - (b.Signif[bi] >> c.Cfg.Lifecycle.SigShift);
    }

    /// <summary>
    /// Вклад здания в поля квартала (§2.4, §2.6) в <paramref name="target"/> (накопитель или пересчёт с нуля в тесте): активное - <c>Heritage += уровень</c>;
    /// заброшенное, не сносимое - стадия вреда по <c>Cond</c> (H1 ≥ 35 %, H2 15-35 %, H3 &lt; 15 %): очки <c>вес · ceil(Capacity / 8)</c>. <paramref name="effects"/> - ещё и жалобы/случаи K4
    /// у жителей клетки с беспорядком (только в проходе, не в пересчёте).
    /// </summary>
    internal static void Contribute(CityWorld c, int bi, in BuildingTypeDesc t, int[] target, bool effects)
    {
        BuildingStore b = c.Buildings;
        NeighborhoodFields f = c.Fields!;
        int lot = b.Lot[bi];
        int cell = f.CellOf(c.Lots.X[lot], c.Lots.Z[lot]);
        if (b.State[bi] == (byte)BuildingState.Active)
        {
            int lv = Level(b.Signif[bi]);
            if (lv > 0)
            {
                NeighborhoodFields.AddTo(target, cell, NeighborhoodFields.Heritage, lv);
            }

            if (effects && t.Kind == LotZone.Residential && b.Occupants[bi] > 0)
            {
                // беспорядок в клетке: жалобы жителей; при пожарной опасности (H3) ещё и случаи K4
                int n = Math.Max(1, b.Occupants[bi] / 4);
                int gen = b.Pool.GenerationOf(bi);
                if (f.Value(cell, NeighborhoodFields.FireRisk) > 0)
                {
                    c.Kpi.OnBlight(bi, gen, n);
                }
                else if (f.Value(cell, NeighborhoodFields.Disorder) > 0)
                {
                    c.Kpi.Complaints.Add(ComplaintKind.Blight, bi, gen, n);
                }
            }

            return;
        }

        if ((b.LifeFlags[bi] & BuildingLifeFlags.Demolishing) != 0)
        {
            return;   // снос уже заказан: вред не копится
        }

        int size = (t.Capacity + 7) / 8;
        int cond = b.Cond[bi];
        if (cond >= H1Bp)
        {
            NeighborhoodFields.AddTo(target, cell, NeighborhoodFields.Blight, 2 * size);
        }
        else if (cond >= H2Bp)
        {
            NeighborhoodFields.AddTo(target, cell, NeighborhoodFields.Blight, 4 * size);
            NeighborhoodFields.AddTo(target, cell, NeighborhoodFields.Disorder, 4 * size);
        }
        else
        {
            NeighborhoodFields.AddTo(target, cell, NeighborhoodFields.Blight, 6 * size);
            NeighborhoodFields.AddTo(target, cell, NeighborhoodFields.Disorder, 6 * size);
            NeighborhoodFields.AddTo(target, cell, NeighborhoodFields.FireRisk, 6 * size);
        }
    }

    /// <summary>Пересчёт полей с нуля по текущим зданиям в <paramref name="target"/> (длина как у <c>Fields.Cur</c>; для проверки двойного буфера в тестах).</summary>
    internal static void RecomputeFields(CityWorld c, int[] target)
    {
        Array.Clear(target);
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (b.IsAlive(i) && b.State[i] is (byte)BuildingState.Active or (byte)BuildingState.Abandoned)
            {
                ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[i]];
                if (t.Kind is LotZone.Residential or LotZone.Commercial or LotZone.Industrial)
                {
                    Contribute(c, i, in t, target, false);
                }
            }
        }
    }

    private static bool DayCore(World w, CityWorld c, int bi, in BuildingTypeDesc t)
    {
        BuildingStore b = c.Buildings;
        bool abandoned = b.State[bi] == (byte)BuildingState.Abandoned;
        if (t.Kind is not (LotZone.Residential or LotZone.Commercial or LotZone.Industrial or LotZone.Service))
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

        Utilities.Request(w, c, bi);   // заявка ЖКХ (§2.10): Cond < repair_request_bp, Ceil - Cond >= 5 %
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
        HeritageLoss(c, bi);
        Growth.Abandon(w, c, bi);
        b.LifeFlags[bi] |= BuildingLifeFlags.Demolishing;
        b.Version++;   // клиент видит «сносится»
        for (int r = b.HeadRes[bi]; r >= 0; r = res.NextHome[r])
        {
            res.Flags[r] |= ResidentFlags.Leaving;
        }

        w.Counters.BuildingsDemolished++;
    }

    /// <summary>Стадия вреда заброшки по <c>Cond</c> (§2.4): 1 - H1 (≥ 35 %), 2 - H2 (15-35 %), 3 - H3 (&lt; 15 %).</summary>
    public static byte HarmStage(int cond) => cond >= H1Bp ? (byte)1 : cond >= H2Bp ? (byte)2 : (byte)3;

    /// <summary>Цена капремонта (§2.9, вариант Б): <c>Capacity · restore_per_unit · (2 − Cond / 10 000)</c> - от 1× до 2× базы; чем запущеннее дом, тем дороже.</summary>
    public static long RestoreCost(CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        long baseCost = (long)c.Types.Rows[b.Type[bi]].Capacity * c.Cfg.Lifecycle.RestorePerUnit;
        return baseCost * ((2 * Full) - b.Cond[bi]) / Full;
    }

    /// <summary>
    /// Капремонт (§2.9), после валидатора и до проводки цены: <c>Cond = Ceil = 100 %</c>, значимость, возраст и статус не трогаются. Заброшенное (<c>Cond</c> уже &gt;= 50 %, валидатор) -
    /// заселяется заново (<see cref="Resettle"/>, третий путь для заброшки). Заявка ЖКХ, если была, остаётся в очереди и снимается при выезде (здание не нуждается в ремонте).
    /// </summary>
    public static void Restore(World w, CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        b.Cond[bi] = Full;
        b.Ceil[bi] = Full;
        b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.WornNotified);
        if (b.State[bi] == (byte)BuildingState.Abandoned)
        {
            Resettle(w, c, bi);
        }

        w.Counters.Restorations++;
    }

    /// <summary>
    /// Снос значимого (§2.5, уровень ≥ 2): разовый проход по зданиям в радиусе <c>heritage_loss_radius_m</c> - жилым активным <c>incidents += Occupants · уровень / 2</c> (случаи K4)
    /// и жалоба <c>HeritageLost</c> у сносимого здания. O(зданий) на команду, редко.
    /// </summary>
    private static void HeritageLoss(CityWorld c, int bi)
    {
        BuildingStore b = c.Buildings;
        int lv = Level(b.Signif[bi]);
        if (lv < 2)
        {
            return;
        }

        int lot = b.Lot[bi];
        float x = c.Lots.X[lot];
        float z = c.Lots.Z[lot];
        float r = c.Cfg.Lifecycle.HeritageLossRadiusM;
        float r2 = r * r;
        long incidents = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            if (i == bi || !b.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || b.Occupants[i] <= 0
                || c.Types.Rows[b.Type[i]].Kind != LotZone.Residential)
            {
                continue;
            }

            int l = b.Lot[i];
            float dx = c.Lots.X[l] - x;
            float dz = c.Lots.Z[l] - z;
            if ((dx * dx) + (dz * dz) <= r2)
            {
                incidents += (long)b.Occupants[i] * lv / 2;
            }
        }

        if (incidents > 0)
        {
            c.Kpi.OnHeritageLost(bi, b.Pool.GenerationOf(bi), (int)Math.Min(incidents, int.MaxValue));
        }
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
