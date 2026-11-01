using System;
using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>Состояние записи пожара (<see cref="FireStore.State"/>).</summary>
internal static class FireState
{
    public const byte Free = 0;      // слот свободен
    public const byte Pending = 1;   // возгорание выпало, момент суток ещё не наступил (бита Burning у здания нет)
    public const byte Burning = 2;   // горит (бит Burning у здания есть)
}

/// <summary>
/// Пожары (services.md §2.7, <c>FireStore</c>, SoA ёмкости <c>max_fires</c>): здание + поколение, состояние, момент начала, <c>Cond</c> в начале (ущерб = потеря), срок повторного вызова.
/// Одновременных очагов (ожидающих + горящих) не больше <c>fire_cap</c> (решение владельца 10.1 Б: пул и очередь вызовов не растут лавиной). Всё создаётся в конструкторе; 0 Б/тик.
/// </summary>
internal sealed class FireStore
{
    public readonly int Capacity;
    public readonly byte[] State;
    public readonly int[] Building;
    public readonly int[] Gen;
    public readonly long[] StartTick;
    public readonly ushort[] CondStart;
    public readonly long[] Retry;      // следующий тик, не раньше которого очаг без бригады вызовет пожарных снова

    public FireStore(int capacity)
    {
        Capacity = Math.Max(0, capacity);
        State = new byte[Capacity];
        Building = new int[Capacity];
        Gen = new int[Capacity];
        StartTick = new long[Capacity];
        CondStart = new ushort[Capacity];
        Retry = new long[Capacity];
        Array.Fill(Building, -1);
    }

    /// <summary>Занятых слотов (ожидающих и горящих).</summary>
    public int Used()
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++)
        {
            n += State[i] != FireState.Free ? 1 : 0;
        }

        return n;
    }

    /// <summary>Слот в исходное (свободен).</summary>
    public void Reset(int i)
    {
        State[i] = FireState.Free;
        Building[i] = -1;
        Gen[i] = 0;
        StartTick[i] = 0;
        CondStart[i] = 0;
        Retry[i] = 0;
    }

    /// <summary>Слот записи здания <paramref name="bi"/> поколения <paramref name="gen"/> или -1.</summary>
    public int IndexOf(int bi, int gen)
    {
        for (int i = 0; i < Capacity; i++)
        {
            if (State[i] != FireState.Free && Building[i] == bi && Gen[i] == gen)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// Пожар (services.md §2.7, SR4; только в режиме <c>city.services</c>). Возгорание - бросок хэшем без PRNG-потока в стадии «здания» (<see cref="Roll"/>): <c>fire_ppm</c> типа × риск
/// квартала × 2 при <c>Cond &lt; 70 %</c>, момент - случайный тик суток из другого хэша. Раз в секунду (<see cref="Step"/>, фаза 9r): ожидающий очаг загорается (бит
/// <see cref="BuildingLifeFlags.Burning"/>, случаи K4, жалоба <c>Fire</c>, срочный вызов <c>fire</c>), горящее здание теряет <c>Cond</c> (тление <c>smolder_s</c> - 5 б.п./с, затем 15),
/// после тления огонь перекидывается на дом той же клетки 128 м с шансом <c>fire_spread_ppm</c> в минуту (решение 10.1 Б), очаг без бригады вызывает пожарных повторно. Тушение - конец
/// работы бригады (<see cref="Extinguish"/>): <c>Ceil −= ущерб / 2</c>. <c>Cond = 0</c> - сгорело дотла (дальше рассыпание BL). Всё - в последовательных фазах главного потока.
/// </summary>
internal static class Fires
{
    /// <summary>Потеря <c>Cond</c> в секунду при тлении, б.п.</summary>
    public const int SmolderBpS = 5;

    /// <summary>Потеря <c>Cond</c> в секунду при открытом огне, б.п. (с 94 % до 0 за ≈ 10 минут).</summary>
    public const int FireBpS = 15;

    /// <summary>Повторный вызов пожарных для очага без бригады и без вызова в кольце, секунд.</summary>
    public const int RecallS = 30;

    /// <summary>Потолок времени тушения, секунд (§2.7: <c>60 + горело_с / 4</c>, не больше 300).</summary>
    public const int MaxOnsiteS = 300;

    private const ulong SaltIgnite = 0xF1AE_0001_9E37_79B9UL;
    private const ulong SaltMoment = 0xF1AE_0002_85EB_CA6BUL;
    private const ulong SaltSpread = 0xF1AE_0003_C2B2_AE35UL;

    /// <summary>Финализатор SplitMix64 от смеси входов: не зависит от порядка и числа потоков, в сейв не входит.</summary>
    private static ulong Mix(ulong seed, ulong a, ulong b, ulong c)
    {
        ulong x = seed ^ (a * 0x9E3779B97F4A7C15UL) ^ (b * 0xC2B2AE3D27D4EB4FUL) ^ (c * 0x165667B19E3779F9UL);
        x ^= x >> 30;
        x *= 0xbf58476d1ce4e5b9UL;
        x ^= x >> 27;
        x *= 0x94d049bb133111ebUL;
        x ^= x >> 31;
        return x;
    }

    private static bool Flammable(in BuildingTypeDesc t) => t.FirePpm > 0 && t.Kind is LotZone.Residential or LotZone.Commercial or LotZone.Industrial;

    /// <summary>Потолок одновременных очагов: <c>fire_cap</c>, но не больше пула <c>max_fires</c>.</summary>
    private static int Cap(CityWorld c, FireStore f) => Math.Min(f.Capacity, c.Cfg.Services.FireCap);

    /// <summary>
    /// Бросок возгорания (стадия «здания», раз в сутки на активное здание R/C/I): <c>ppm = fire_ppm · (100 + fire_risk_pct · min(FR₃ₓ₃, 20)) / 100</c>, ×2 при <c>Cond &lt; 70 %</c>
    /// (спека: &lt; 50 %, но BL уже бросает такие здания). Выпало - очаг с моментом начала в случайный тик суток. Потолок или пул полны - очаг не начинается, счётчик <c>FiresCapped</c>.
    /// </summary>
    public static void Roll(World w, CityWorld c, int bi, in BuildingTypeDesc t)
    {
        CityServices sv = c.Services!;
        FireStore f = sv.Fires;
        BuildingStore b = c.Buildings;
        if (f.Capacity == 0 || !Flammable(in t) || b.State[bi] != (byte)BuildingState.Active || (b.LifeFlags[bi] & BuildingLifeFlags.Burning) != 0)
        {
            return;
        }

        long ppm = t.FirePpm;
        NeighborhoodFields? nf = c.Fields;
        if (nf != null)
        {
            int lot = b.Lot[bi];
            int fr = Math.Min(20, nf.Window(nf.CellOf(c.Lots.X[lot], c.Lots.Z[lot]), NeighborhoodFields.FireRisk));
            ppm = ppm * (100 + ((long)c.Cfg.Services.FireRiskPct * fr)) / 100;
        }

        if (b.Cond[bi] < CityLife.MinorBp)
        {
            ppm *= 2;
        }

        int gen = b.Pool.GenerationOf(bi);
        ulong h = Mix(w.Seed ^ SaltIgnite, (ulong)(uint)c.Day, (uint)bi, (uint)gen);
        if ((long)(h % 1_000_000UL) >= ppm)
        {
            return;
        }

        int fi = Add(w, c, f, bi, gen);
        if (fi < 0)
        {
            return;
        }

        long dayStart = (long)c.Day * c.DayTicks;
        long start = dayStart + (long)(Mix(w.Seed ^ SaltMoment, (ulong)(uint)c.Day, (uint)bi, (uint)gen) % (ulong)Math.Max(1, c.DayTicks));
        f.StartTick[fi] = Math.Max(start, w.Tick);
        if (start <= w.Tick)
        {
            Ignite(w, c, sv, fi, false);
        }
    }

    /// <summary>Поджечь здание <paramref name="bi"/> сейчас (минуя бросок и момент суток; тесты, будущий рассказчик): очаг в пределах потолка. false - потолок, пул, не <c>Active</c> или уже горит.</summary>
    internal static bool Start(World w, CityWorld c, int bi)
    {
        CityServices? sv = c.Services;
        BuildingStore b = c.Buildings;
        if (sv == null || !sv.Table.Explicit || !b.IsAlive(bi) || b.State[bi] != (byte)BuildingState.Active || (b.LifeFlags[bi] & BuildingLifeFlags.Burning) != 0)
        {
            return false;
        }

        int fi = Add(w, c, sv.Fires, bi, b.Pool.GenerationOf(bi));
        if (fi < 0)
        {
            return false;
        }

        Ignite(w, c, sv, fi, false);
        return true;
    }

    /// <summary>Новая запись «ожидает» для здания: первый свободный слот в пределах <see cref="Cap"/>; здание уже в записи - пропуск; полно - <c>FiresCapped</c>, -1.</summary>
    private static int Add(World w, CityWorld c, FireStore f, int bi, int gen)
    {
        if (f.IndexOf(bi, gen) >= 0)
        {
            return -1;
        }

        int used = 0;
        int free = -1;
        for (int i = 0; i < f.Capacity; i++)
        {
            if (f.State[i] == FireState.Free)
            {
                free = free < 0 ? i : free;
            }
            else
            {
                used++;
            }
        }

        if (free < 0 || used >= Cap(c, f))
        {
            w.Counters.FiresCapped++;
            return -1;
        }

        f.State[free] = FireState.Pending;
        f.Building[free] = bi;
        f.Gen[free] = gen;
        f.StartTick[free] = w.Tick;
        f.CondStart[free] = 0;
        f.Retry[free] = 0;
        return free;
    }

    /// <summary>Очаг загорается (§2.7): бит <c>Burning</c>, <c>CondStart</c>, случаи K4 <c>Occupants</c> и жалоба <c>Fire</c>, срочный вызов <c>fire</c>, событие. Здание не <c>Active</c> - очаг отменён.</summary>
    private static void Ignite(World w, CityWorld c, CityServices sv, int fi, bool spread)
    {
        FireStore f = sv.Fires;
        BuildingStore b = c.Buildings;
        int bi = f.Building[fi];
        if (!b.Pool.IsAlive(bi, f.Gen[fi]) || b.State[bi] != (byte)BuildingState.Active)
        {
            f.Reset(fi);
            return;
        }

        f.State[fi] = FireState.Burning;
        f.StartTick[fi] = w.Tick;
        f.CondStart[fi] = b.Cond[bi];
        b.LifeFlags[bi] |= BuildingLifeFlags.Burning;
        w.Counters.FiresStarted++;
        if (spread)
        {
            w.Counters.FiresSpread++;
        }

        c.Kpi.OnFire(bi, f.Gen[fi], Math.Max(1, b.Occupants[bi]));
        w.EmitAt(SimEventKind.FireStarted, 0, bi, spread ? 1 : 0, BuildingPlace(c, bi));
        CallBrigade(w, c, sv, fi);
    }

    /// <summary>Срочный вызов <c>fire</c> к горящему зданию (запись с таким вызовом - первая; нет записи или кольцо полно - повтор через <see cref="RecallS"/>).</summary>
    private static void CallBrigade(World w, CityWorld c, CityServices sv, int fi)
    {
        FireStore f = sv.Fires;
        f.Retry[fi] = w.Tick + ((long)RecallS * Simulation.TicksPerSecond);
        int rec = sv.Table.RecordWithCall(ServiceCallKind.Fire);
        if (rec >= 0)
        {
            Utilities.Call(w, c, rec, ServiceCallKind.Fire, f.Building[fi]);
        }
    }

    private static EventPlace BuildingPlace(CityWorld c, int bi)
    {
        int lot = c.Buildings.Lot[bi];
        return new EventPlace(EntityKind.Building, bi, c.Buildings.Pool.GenerationOf(bi), c.Lots.X[lot], c.Lots.Z[lot]);
    }

    /// <summary>
    /// Секундный шаг (фаза 9r, тик кратен <c>Utilities.Step</c>, до таймеров бригад): ожидающие загораются в свой момент, горящее теряет <c>Cond</c>, <c>Cond &lt; 50 %</c> - заброшено (BL),
    /// <c>Cond = 0</c> - сгорело дотла; после тления - шанс перекинуться; очаг без бригады вызывает пожарных повторно. Записи снесённых зданий освобождаются.
    /// </summary>
    public static void Step(World w, CityWorld c, CityServices sv)
    {
        FireStore f = sv.Fires;
        if (f.Capacity == 0)
        {
            return;
        }

        BuildingStore b = c.Buildings;
        long smolderTicks = (long)c.Cfg.Services.SmolderS * Simulation.TicksPerSecond;
        for (int fi = 0; fi < f.Capacity; fi++)
        {
            byte st = f.State[fi];
            if (st == FireState.Free)
            {
                continue;
            }

            int bi = f.Building[fi];
            int gen = f.Gen[fi];
            if (!b.Pool.IsAlive(bi, gen))
            {
                f.Reset(fi);   // здание снесено или рассыпалось: огню нечего гореть
                continue;
            }

            if (st == FireState.Pending)
            {
                if (w.Tick >= f.StartTick[fi])
                {
                    Ignite(w, c, sv, fi, false);
                }

                continue;
            }

            if (f.StartTick[fi] == w.Tick)
            {
                continue;   // загорелось в этом тике (перекинулось): первый шаг со следующей секунды
            }

            long burned = w.Tick - f.StartTick[fi];
            int cond = Math.Max(0, b.Cond[bi] - (burned < smolderTicks ? SmolderBpS : FireBpS));
            b.Cond[bi] = (ushort)cond;
            if (cond < CityLife.AbandonBp && b.State[bi] == (byte)BuildingState.Active)
            {
                Growth.Abandon(w, c, bi);   // не функционирует: жители выселены, фирма закрыта (BL)
            }

            if (cond == 0)
            {
                Burned(w, c, f, fi, bi);
                continue;
            }

            if (burned >= smolderTicks && c.Cfg.Services.FireSpreadPpm > 0)
            {
                ulong h = Mix(w.Seed ^ SaltSpread, (ulong)(w.Tick / Simulation.TicksPerSecond), (uint)bi, (uint)gen);
                if ((long)(h % 60_000_000UL) < c.Cfg.Services.FireSpreadPpm)
                {
                    TrySpread(w, c, sv, bi, h);
                }
            }

            if (w.Tick >= f.Retry[fi] && !Covered(sv, bi, gen))
            {
                CallBrigade(w, c, sv, fi);
            }
        }
    }

    /// <summary>Сгорело дотла (<c>Cond = 0</c>): бит снят, запись свободна, счётчик, событие; дальше рассыпание BL (здание уже заброшено и удаляется суточным проходом).</summary>
    private static void Burned(World w, CityWorld c, FireStore f, int fi, int bi)
    {
        c.Buildings.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.Burning);
        f.Reset(fi);
        w.Counters.BuildingsBurned++;
        w.EmitAt(SimEventKind.BuildingBurned, 0, bi, 0, BuildingPlace(c, bi));
    }

    /// <summary>
    /// Перекидывание (решение 10.1 Б): на случайный дом той же клетки 128 м - активное здание R/C/I с <c>fire_ppm &gt; 0</c>, не горящее и без своей записи. Потолок или пул полны - отброшено
    /// (<c>FiresCapped</c>). Выбор - k-й кандидат по хэшу, проход по зданиям (редкий, O(зданий)).
    /// </summary>
    private static void TrySpread(World w, CityWorld c, CityServices sv, int from, ulong h)
    {
        FireStore f = sv.Fires;
        NeighborhoodFields? nf = c.Fields;
        BuildingStore b = c.Buildings;
        if (nf == null)
        {
            return;
        }

        if (f.Used() >= Cap(c, f))
        {
            w.Counters.FiresCapped++;
            return;
        }

        int lot = b.Lot[from];
        int cell = nf.CellOf(c.Lots.X[lot], c.Lots.Z[lot]);
        int n = 0;
        for (int i = 0; i < b.HighWater; i++)
        {
            n += Candidate(c, nf, i, from, cell) ? 1 : 0;
        }

        if (n == 0)
        {
            return;
        }

        int k = (int)((h >> 24) % (ulong)n);
        for (int i = 0; i < b.HighWater; i++)
        {
            if (Candidate(c, nf, i, from, cell) && k-- == 0)
            {
                int fi = Add(w, c, f, i, b.Pool.GenerationOf(i));
                if (fi >= 0)
                {
                    Ignite(w, c, sv, fi, true);
                }

                return;
            }
        }
    }

    private static bool Candidate(CityWorld c, NeighborhoodFields nf, int i, int from, int cell)
    {
        BuildingStore b = c.Buildings;
        if (i == from || !b.Pool.IsAlive(i) || b.State[i] != (byte)BuildingState.Active || (b.LifeFlags[i] & BuildingLifeFlags.Burning) != 0 || !Flammable(in c.Types.Rows[b.Type[i]]))
        {
            return false;
        }

        int lot = b.Lot[i];
        return nf.CellOf(c.Lots.X[lot], c.Lots.Z[lot]) == cell;
    }

    /// <summary>К горящему зданию уже едет или работает бригада, либо вызов <c>fire</c> ждёт в кольце записи.</summary>
    private static bool Covered(CityServices sv, int bi, int gen)
    {
        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity; k++)
        {
            if (u.State[k] is CrewState.ToSite or CrewState.Working && u.Kind[k] == (byte)ServiceCallKind.Fire && u.Target[k] == bi && u.TargetGen[k] == gen)
            {
                return true;
            }
        }

        int rec = sv.Table.RecordWithCall(ServiceCallKind.Fire);
        if (rec < 0)
        {
            return false;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            Util.RingBuffer<ServiceCall> ring = pass == 0 ? sv.Urgent[rec] : sv.Normal[rec];
            for (int i = 0; i < ring.Count; i++)
            {
                ref readonly ServiceCall q = ref ring.At(i);
                if (q.Kind == (byte)ServiceCallKind.Fire && q.Target == bi && q.TargetGen == gen)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Здание горит: живое того же поколения с битом <c>Burning</c>.</summary>
    public static bool IsBurning(CityWorld c, int bi, int gen) =>
        c.Buildings.Pool.IsAlive(bi, gen) && (c.Buildings.LifeFlags[bi] & BuildingLifeFlags.Burning) != 0;

    /// <summary>Время тушения (§2.7), секунды до масштаба <c>onsite_q</c>: <c>onsite_s + горело_с / 4</c>, не больше <see cref="MaxOnsiteS"/> (и не меньше <paramref name="onsiteS"/>).</summary>
    public static int OnsiteS(World w, CityServices sv, int bi, int gen, int onsiteS)
    {
        int fi = sv.Fires.IndexOf(bi, gen);
        if (fi < 0 || sv.Fires.State[fi] != FireState.Burning)
        {
            return onsiteS;
        }

        long burnedS = (w.Tick - sv.Fires.StartTick[fi]) / Simulation.TicksPerSecond;
        return (int)Math.Max(onsiteS, Math.Min(MaxOnsiteS, onsiteS + (burnedS / 4)));
    }

    /// <summary>
    /// Тушение (конец работы бригады): бит <c>Burning</c> снят, <c>Ceil −= ущерб / 2</c> (<c>ущерб = Cond в начале − Cond</c>; до капремонта выше не поднимется), <c>Cond ≤ Ceil</c>, запись
    /// свободна, счётчик и событие. Огня уже нет (сгорело, снесено) - ничего.
    /// </summary>
    public static void Extinguish(World w, CityWorld c, CityServices sv, int bi, int gen)
    {
        FireStore f = sv.Fires;
        int fi = f.IndexOf(bi, gen);
        BuildingStore b = c.Buildings;
        if (fi < 0 || f.State[fi] != FireState.Burning || !b.Pool.IsAlive(bi, gen))
        {
            return;
        }

        int damage = Math.Max(0, f.CondStart[fi] - b.Cond[bi]);
        int ceil = Math.Max(0, b.Ceil[bi] - (damage / 2));
        b.Ceil[bi] = (ushort)ceil;
        b.Cond[bi] = (ushort)Math.Min((int)b.Cond[bi], ceil);
        b.LifeFlags[bi] &= unchecked((byte)~BuildingLifeFlags.Burning);
        f.Reset(fi);
        w.Counters.FiresOut++;
        w.EmitAt(SimEventKind.FireOut, 0, bi, damage, BuildingPlace(c, bi));
    }
}
