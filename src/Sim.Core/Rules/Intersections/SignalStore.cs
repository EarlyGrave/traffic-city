using System;
using System.Numerics;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Хранилище светофоров (core-api 6.7, intersections.md 6): плотные массивы по слотам; слот = индекс в
/// <c>SignalStates</c> и значение <c>NodeData.Signal</c>. Автомат фаз - <see cref="SignalPhase"/>.
/// Все массивы выделены в конструкторе, дальше аллокаций нет. 2.5 (ADR-0018): режим <see cref="SignalMode.Adaptive"/> и «стрелка»
/// (<c>Fixed</c> с флагом <see cref="SignalPlanFlags.Arrows"/>) ведёт «автомат с подфазами» (<see cref="Machine"/>): текущая маска
/// <see cref="GreenMask"/>, следующая <see cref="NextMask"/>, переход между любыми масками - жёлтый уходящим, all-red, вход в следующую.
/// </summary>
internal sealed class SignalStore
{
    public const int MaxPhases = SignalMasks.MaxPhases;
    public const int MaxCycleTicks = 65535;

    /// <summary>Рукавов на узел в массивах слота (= <see cref="NetworkStore.MaxArms"/>).</summary>
    public const int ArmCap = NetworkStore.MaxArms;

    /// <summary>Входных полос светофора, которые читают детекторы (спека: рукавов ≤ 8 по ≤ 4 полосы).</summary>
    public const int MaxSigLanes = 32;

    /// <summary>Инвариант красного (intersections.md 5.6): прямой оси со спросом красный не дольше 52 с.</summary>
    public const int MaxRedTicks = 1040;

    // Дефолты Adaptive (RulesDesc, тики).
    public const int DefaultMinGreenTicks = 140;
    public const int DefaultMaxGreenTicks = 480;
    public const int DefaultGapTicks = 50;
    public const int DefaultArrowTriggerTicks = 80;
    public const int DefaultArrowMinTicks = 60;
    public const int DefaultArrowMaxTicks = 120;
    public const int DefaultArrowGapTicks = 40;

    public readonly int Capacity;
    public int Count;

    public readonly int[] Node;
    public readonly SignalMode[] Mode;
    public readonly byte[] PhaseCount;
    public readonly ushort[] GreenTicks;     // [Capacity * 8]; Adaptive: MaxGreen оси
    public readonly ushort[] YellowTicks;
    public readonly ushort[] AllRedTicks;
    public readonly ushort[] OffsetTicks;
    public readonly byte[] Phase;            // ось
    public readonly SignalInterval[] Interval;
    public readonly ushort[] TicksLeft;
    public readonly ulong[] PhaseMask;       // [Capacity * 8], бит = connector - Node.FirstConnector

    // --- 2.5: параметры Adaptive / стрелки (из плана) ---
    public readonly ushort[] MinGreenTicks;
    public readonly ushort[] GapTicks;
    public readonly ushort[] ArrowTriggerTicks;
    public readonly ushort[] ArrowMinTicks;
    public readonly ushort[] ArrowMaxTicks;
    public readonly ushort[] ArrowGapTicks;
    public readonly ushort[] CycleTicks;
    public readonly byte[] Flags;            // SignalPlanFlags

    // --- 2.5: состояние автомата с подфазами (в хэше только для Machine != 0) ---
    public readonly byte[] Machine;          // 0 - прежний Fixed (Phase/Interval/TicksLeft), 1 - Adaptive или Fixed со стрелкой
    public readonly byte[] Sub;              // 0 Main, 1 Arrow
    public readonly ulong[] GreenMask;       // открытая маска (P)
    public readonly ulong[] NextMask;        // маска, в которую идёт переход (Q); вне перехода = GreenMask
    public readonly byte[] NextAxis;
    public readonly byte[] NextSub;
    public readonly ushort[] NextArrowArms;
    public readonly ushort[] ArrowArms;      // рукава текущей стрелки (биты)
    public readonly ushort[] GreenElapsed;
    public readonly ushort[] GapLeft;
    public readonly ushort[] ArmLeftYield;   // [Capacity * ArmCap]
    public readonly int[] ArmLeftHead;       // [Capacity * ArmCap]
    public readonly ushort[] SpillTicks;     // 2.5 S3 (6.8): тиков подряд, когда весь спрос зелёной оси упёрся в стоящий выезд
    public long SpillOutEntries;             // монотонный счётчик досрочных концов зелёного из-за забитого выезда (SignalPhase переносит в SimCounters.SpillOuts)
    public long ArrowEntries;                // монотонный счётчик включений стрелки (SignalPhase переносит его в SimCounters)

    // --- 2.5: статика слота (производное от сети, вне хэша) ---
    public readonly ulong[] ArmMask;         // [Capacity * ArmCap] все connector'ы рукава
    public readonly ulong[] LeftMask;        // Left + UTurn
    public readonly ulong[] RightMask;
    public readonly sbyte[] ArmAxis;         // -1 - у рукава нет connector'ов
    public readonly sbyte[] ArmOpp;          // встречный рукав той же оси или -1
    public readonly ushort[] HasArrow;       // биты рукавов, у которых возможна стрелка
    public readonly byte[] ArmCountOf;
    public readonly byte[] SigLaneCount;
    public readonly int[] SigLane;           // [Capacity * MaxSigLanes]
    public readonly byte[] SigLaneArm;
    public readonly byte[] SigLaneAxis;
    public readonly bool[] SigLaneLeft;      // среди connector'ов полосы есть Left/UTurn

    // ---- пешеходная фаза (ADR-0020, pedestrians.md §6; объявлено в P2, логика - P3): состояние, в хэше (P3) и сейве SIGS ----
    public const int XSlots = 8;             // переходов оси узла на слот SigX
    public readonly ushort[] XWalkLeft;      // [c * XSlots] осталось тиков Walk
    public readonly ushort[] XClearLeft;     // [c * XSlots] осталось тиков Clear
    public readonly byte[] XServed;          // [c] маска переходов, обслуженных в этом зелёном

    // ---- статика пешеходной фазы (производное от сети и PhaseMask, вне хэша и сейва; PedSignals.EnsureTables) ----
    public readonly byte[] SigXCount;        // [c] переходов узла слота (<= XSlots)
    public readonly byte[] PendingOther;     // [c] 1: в этом тике у зелёной оси есть вызов другой оси/стрелки (пишет StepAdaptive до чтения PedSignals.StepSlot; вне хэша и сейва)
    public readonly int[] SigXIdx;           // [c * XSlots] индекс перехода (слот рукава)
    public readonly sbyte[] SigXAxis;        // [c * XSlots] ось, в зелёном которой переход обслуживается (-1: нет встречной оси)
    public readonly ushort[] SigXClear;      // [c * XSlots] длительность Clear, тики
    public int SigXVersion = -1;             // NetworkStore.Version, для которой собраны таблицы
    public int SigXBuiltCount = -1;

    private readonly int _defaultArrowGap;

    public SignalStore(int capacity, int arrowGapTicks = DefaultArrowGapTicks)
    {
        _defaultArrowGap = Math.Clamp(arrowGapTicks > 0 ? arrowGapTicks : DefaultArrowGapTicks, 1, ushort.MaxValue);
        Capacity = Math.Max(0, capacity);
        int c = Capacity;
        Node = new int[c];
        Mode = new SignalMode[c];
        PhaseCount = new byte[c];
        GreenTicks = new ushort[c * MaxPhases];
        YellowTicks = new ushort[c];
        AllRedTicks = new ushort[c];
        OffsetTicks = new ushort[c];
        Phase = new byte[c];
        Interval = new SignalInterval[c];
        TicksLeft = new ushort[c];
        PhaseMask = new ulong[c * MaxPhases];
        MinGreenTicks = new ushort[c];
        GapTicks = new ushort[c];
        ArrowTriggerTicks = new ushort[c];
        ArrowMinTicks = new ushort[c];
        ArrowMaxTicks = new ushort[c];
        ArrowGapTicks = new ushort[c];
        CycleTicks = new ushort[c];
        Flags = new byte[c];
        Machine = new byte[c];
        Sub = new byte[c];
        GreenMask = new ulong[c];
        NextMask = new ulong[c];
        NextAxis = new byte[c];
        NextSub = new byte[c];
        NextArrowArms = new ushort[c];
        ArrowArms = new ushort[c];
        GreenElapsed = new ushort[c];
        GapLeft = new ushort[c];
        SpillTicks = new ushort[c];
        ArmLeftYield = new ushort[c * ArmCap];
        ArmLeftHead = new int[c * ArmCap];
        ArmMask = new ulong[c * ArmCap];
        LeftMask = new ulong[c * ArmCap];
        RightMask = new ulong[c * ArmCap];
        ArmAxis = new sbyte[c * ArmCap];
        ArmOpp = new sbyte[c * ArmCap];
        HasArrow = new ushort[c];
        ArmCountOf = new byte[c];
        SigLaneCount = new byte[c];
        SigLane = new int[c * MaxSigLanes];
        SigLaneArm = new byte[c * MaxSigLanes];
        SigLaneAxis = new byte[c * MaxSigLanes];
        SigLaneLeft = new bool[c * MaxSigLanes];
        XWalkLeft = new ushort[c * XSlots];
        XClearLeft = new ushort[c * XSlots];
        XServed = new byte[c];
        SigXCount = new byte[c];
        PendingOther = new byte[c];
        SigXIdx = new int[c * XSlots];
        SigXAxis = new sbyte[c * XSlots];
        SigXClear = new ushort[c * XSlots];
    }

    public static ushort GreenOf(in SetSignalPlanCmd p, int k) => k switch
    {
        0 => p.Green0,
        1 => p.Green1,
        2 => p.Green2,
        3 => p.Green3,
        4 => p.Green4,
        5 => p.Green5,
        6 => p.Green6,
        _ => p.Green7,
    };

    private static void SetGreen(ref SetSignalPlanCmd p, int k, ushort g)
    {
        switch (k)
        {
            case 0: p.Green0 = g; break;
            case 1: p.Green1 = g; break;
            case 2: p.Green2 = g; break;
            case 3: p.Green3 = g; break;
            case 4: p.Green4 = g; break;
            case 5: p.Green5 = g; break;
            case 6: p.Green6 = g; break;
            default: p.Green7 = g; break;
        }
    }

    /// <summary>Длительность цикла в тиках (Σ Green_k + PhaseCount·(Yellow + AllRed)); для Fixed без стрелки - точная.</summary>
    public int CycleOf(int slot)
    {
        int sum = 0;
        int pc = PhaseCount[slot];
        for (int k = 0; k < pc; k++)
        {
            sum += GreenTicks[(slot * MaxPhases) + k];
        }

        return sum + (pc * (YellowTicks[slot] + AllRedTicks[slot]));
    }

    /// <summary>
    /// <c>MaxGreen</c> оси по умолчанию для Adaptive (intersections.md 5.6, 6.5): 2 оси - <c>SignalMaxGreenTicks</c> (24 с); больше осей - так, чтобы
    /// красный прямого не превышал <see cref="MaxRedTicks"/> (но не меньше <c>MinGreen</c>).
    /// </summary>
    public static int DefaultMaxGreen(int axes, int minGreen, int yellow, int allRed, in RulesDesc rules)
    {
        int maxGreen = rules.SignalMaxGreenTicks > 0 ? rules.SignalMaxGreenTicks : DefaultMaxGreenTicks;
        if (axes <= 2)
        {
            return maxGreen;
        }

        int byRed = (MaxRedTicks - (2 * axes * (yellow + allRed))) / (axes - 1);
        return Math.Max(minGreen, Math.Min(maxGreen, byRed));
    }

    /// <summary>
    /// План по умолчанию (intersections.md 6.2, 6.5; 2.5): <c>Adaptive</c> со стрелкой по спросу (без координации); Green_k = MaxGreen оси;
    /// <paramref name="adaptive"/> = false - прежний <c>Fixed</c>: зелёный 24 с при &lt;= 2 фазах, 16 с при 3, 12 с при &gt;= 4, без стрелки.
    /// Жёлтый/all-red - <c>Rules.DefaultYellowTicks/DefaultAllRedTicks</c> (если оба 0 - 3 с и 1 с); смещение 0.
    /// </summary>
    public static SetSignalPlanCmd DefaultPlan(int phases, in RulesDesc rules, bool adaptive = true)
    {
        int yellow = rules.DefaultYellowTicks;
        int allRed = rules.DefaultAllRedTicks;
        if (yellow <= 0 && allRed <= 0)
        {
            yellow = 60;
            allRed = 20;
        }

        ushort green;
        if (adaptive)
        {
            int minGreen = rules.SignalMinGreenTicks > 0 ? rules.SignalMinGreenTicks : DefaultMinGreenTicks;
            green = (ushort)Math.Clamp(DefaultMaxGreen(phases, minGreen, yellow, allRed, in rules), 1, ushort.MaxValue);
        }
        else
        {
            green = (ushort)(phases <= 2 ? 480 : phases == 3 ? 320 : 240);
        }

        return new SetSignalPlanCmd
        {
            Mode = adaptive ? SignalMode.Adaptive : SignalMode.Fixed,
            PhaseCount = (byte)phases,
            Green0 = green,
            Green1 = green,
            Green2 = green,
            Green3 = green,
            Green4 = green,
            Green5 = green,
            Green6 = green,
            Green7 = green,
            YellowTicks = (ushort)Math.Clamp(yellow, 0, ushort.MaxValue),
            AllRedTicks = (ushort)Math.Clamp(allRed, 0, ushort.MaxValue),
            Flags = adaptive ? SignalPlanFlags.Arrows : (byte)0,
        };
    }

    /// <summary>План по умолчанию для узла <paramref name="node"/>: <see cref="DefaultPlan(int, in RulesDesc, bool)"/> + надбавка all-red на очистку коробки (<see cref="ClearanceExtraTicks"/>, ADR-0028).</summary>
    internal static SetSignalPlanCmd DefaultPlan(World w, int node, int phases, bool adaptive = true)
    {
        SetSignalPlanCmd p = DefaultPlan(phases, in w.Rules, adaptive);
        p.AllRedTicks = (ushort)Math.Clamp(p.AllRedTicks + ClearanceExtraTicks(w.Net, node, w.Builder.Options), 0, ushort.MaxValue);
        return p;
    }

    /// <summary>
    /// Надбавка all-red узла на очистку коробки (ADR-0028): all-red по инженерной формуле <c>r = (W + L) / v</c>; базовый all-red (1 с) соответствует коробке
    /// <c>W0 = 2 * NodeSetback</c> (14 м, до ADR-0028), поэтому <c>r(W) = r0 + (W - W0) / v</c>. <c>W = 2 * NodeData.Radius</c> - самый длинный проезд «прямо» через узел
    /// (сумма отступов противоположных рукавов), <c>v</c> - наименьший лимит скорости рукавов узла (самый медленный подход очищает коробку дольше всех).
    /// Тиков - вверх до целого. Коробка не шире <c>W0</c> (в том числе <c>corner_radius = 0</c>) - 0: поведение побитово прежнее. Узел без рукавов - 0.
    /// </summary>
    internal static int ClearanceExtraTicks(NetworkStore net, int node, in NetworkOptions o)
    {
        if ((uint)node >= (uint)net.NodeCount)
        {
            return 0;
        }

        ref readonly NodeData nd = ref net.Nodes[node];
        float box = 2f * nd.Radius;
        float box0 = 2f * o.NodeSetback;
        if (!(box > box0) || nd.FirstArm < 0 || nd.ArmCount <= 0)
        {
            return 0;
        }

        float v = float.MaxValue;
        for (int a = 0; a < nd.ArmCount; a++)
        {
            float lim = net.Segments[net.NodeArms[nd.FirstArm + a]].SpeedLimit;
            if (lim < v)
            {
                v = lim;
            }
        }

        if (!(v > 0f) || v == float.MaxValue)
        {
            return 0;
        }

        return DetMath.ToIntSat(MathF.Ceiling((box - box0) / v / PhysicsParams.DefaultDt));
    }

    /// <summary>
    /// Подставляет дефолты <c>RulesDesc</c> вместо нулевых параметров (6.5): Adaptive - Green_k (MaxGreen), MinGreen, Gap, ArrowTrigger/Min/Max;
    /// Fixed со стрелкой - ArrowMax. Остальное - как в команде.
    /// </summary>
    public static SetSignalPlanCmd Resolve(in SetSignalPlanCmd cmd, int axes, in RulesDesc rules)
    {
        SetSignalPlanCmd p = cmd;
        if (p.Mode == SignalMode.Adaptive)
        {
            if (p.MinGreenTicks == 0)
            {
                p.MinGreenTicks = (ushort)Math.Clamp(rules.SignalMinGreenTicks > 0 ? rules.SignalMinGreenTicks : DefaultMinGreenTicks, 1, ushort.MaxValue);
            }

            if (p.GapTicks == 0)
            {
                p.GapTicks = (ushort)Math.Clamp(rules.SignalGapTicks > 0 ? rules.SignalGapTicks : DefaultGapTicks, 1, ushort.MaxValue);
            }

            if (p.ArrowTriggerTicks == 0)
            {
                p.ArrowTriggerTicks = (ushort)Math.Clamp(rules.SignalArrowTriggerTicks > 0 ? rules.SignalArrowTriggerTicks : DefaultArrowTriggerTicks, 1, ushort.MaxValue);
            }

            if (p.ArrowMinTicks == 0)
            {
                p.ArrowMinTicks = (ushort)Math.Clamp(rules.SignalArrowMinTicks > 0 ? rules.SignalArrowMinTicks : DefaultArrowMinTicks, 1, ushort.MaxValue);
            }

            int dg = DefaultMaxGreen(axes, p.MinGreenTicks, p.YellowTicks, p.AllRedTicks, in rules);
            for (int k = 0; k < axes && k < MaxPhases; k++)
            {
                if (GreenOf(in p, k) == 0)
                {
                    SetGreen(ref p, k, (ushort)Math.Clamp(dg, 1, ushort.MaxValue));
                }
            }
        }

        if (p.ArrowMaxTicks == 0 && (p.Mode == SignalMode.Adaptive || (p.Mode == SignalMode.Fixed && (p.Flags & SignalPlanFlags.Arrows) != 0)))
        {
            p.ArrowMaxTicks = (ushort)Math.Clamp(rules.SignalArrowMaxTicks > 0 ? rules.SignalArrowMaxTicks : DefaultArrowMaxTicks, 1, ushort.MaxValue);
            if (p.Mode == SignalMode.Adaptive && p.ArrowMinTicks > p.ArrowMaxTicks)
            {
                p.ArrowMaxTicks = p.ArrowMinTicks;
            }
        }

        return p;
    }

    /// <summary>
    /// Проверка плана (6.3, 6.5) после <see cref="Resolve"/>: режим известен; Fixed - PhaseCount = числу осей, Green_k &gt;= 1, цикл &lt;= 65535;
    /// Adaptive - ещё 1 &lt;= MinGreen &lt;= MaxGreen_k, Gap &gt;= 1, ArrowMin &lt;= ArrowMax, <c>CycleTicks == 0</c> (координация - S3) и при
    /// <paramref name="enforceRed"/> инвариант красного (5.6). Для Off/FlashYellow параметры цикла не проверяются.
    /// </summary>
    public static bool IsValid(in SetSignalPlanCmd p, int axes, bool enforceRed = false, int pedGreenTicks = 0)
    {
        if (p.Mode == SignalMode.Off || p.Mode == SignalMode.FlashYellow)
        {
            return true;
        }

        if ((p.Mode != SignalMode.Fixed && p.Mode != SignalMode.Adaptive) || p.PhaseCount != axes || axes < 1 || axes > MaxPhases)
        {
            return false;
        }

        bool arrows = (p.Flags & SignalPlanFlags.Arrows) != 0;
        int yar = p.YellowTicks + p.AllRedTicks;
        int cycle = axes * yar;
        for (int k = 0; k < axes; k++)
        {
            ushort g = GreenOf(in p, k);
            if (g < 1)
            {
                return false;
            }

            cycle += g;
        }

        if (p.Mode == SignalMode.Fixed)
        {
            if (arrows)
            {
                cycle += axes * (p.ArrowMaxTicks + yar);
            }

            return cycle <= MaxCycleTicks;
        }

        if (p.MinGreenTicks < 1 || p.GapTicks < 1 || p.ArrowMinTicks > p.ArrowMaxTicks)
        {
            return false;
        }

        if (p.CycleTicks != 0)
        {
            // Координация (6.7): 2 оси, C чётное, окно C/2 вмещает MinGreen, стрелку и два перехода. Инвариант красного даёт каркас окон, отдельно не проверяется.
            int arrowMax = arrows ? p.ArrowMaxTicks : 0;
            return axes == 2 && (p.CycleTicks & 1) == 0 && (p.CycleTicks / 2) >= Math.Max((int)p.MinGreenTicks, pedGreenTicks) + arrowMax + (2 * yar) && MinGreenFits(in p, axes);
        }

        for (int k = 0; k < axes; k++)
        {
            if (p.MinGreenTicks > GreenOf(in p, k))
            {
                return false;
            }
        }

        if (!enforceRed)
        {
            return true;
        }

        int arrowEff = arrows && axes == 2 ? p.ArrowMaxTicks : 0;
        int total = 0;
        for (int k = 0; k < axes; k++)
        {
            total += GreenOf(in p, k) + arrowEff;
        }

        for (int k = 0; k < axes; k++)
        {
            int red = arrowEff + (total - GreenOf(in p, k) - arrowEff) + (2 * axes * yar);
            if (red > MaxRedTicks)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MinGreenFits(in SetSignalPlanCmd p, int axes)
    {
        for (int k = 0; k < axes; k++)
        {
            if (p.MinGreenTicks > GreenOf(in p, k))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Записывает (разрешённый и проверенный) план в слот и пересчитывает начальное состояние по <c>OffsetTicks</c>.</summary>
    public void ApplyPlan(int slot, in SetSignalPlanCmd p, long tick = 0)
    {
        Mode[slot] = p.Mode;
        YellowTicks[slot] = p.YellowTicks;
        AllRedTicks[slot] = p.AllRedTicks;
        OffsetTicks[slot] = p.OffsetTicks;
        MinGreenTicks[slot] = p.MinGreenTicks;
        GapTicks[slot] = p.GapTicks;
        ArrowTriggerTicks[slot] = p.ArrowTriggerTicks;
        ArrowMinTicks[slot] = p.ArrowMinTicks;
        ArrowMaxTicks[slot] = p.ArrowMaxTicks;
        ArrowGapTicks[slot] = (ushort)_defaultArrowGap;
        CycleTicks[slot] = p.CycleTicks;
        Flags[slot] = p.Flags;
        Machine[slot] = 0;
        XServed[slot] = 0;
        for (int j = 0; j < XSlots; j++)
        {
            XWalkLeft[(slot * XSlots) + j] = 0;
            XClearLeft[(slot * XSlots) + j] = 0;
        }

        if (p.Mode == SignalMode.Fixed || p.Mode == SignalMode.Adaptive)
        {
            for (int k = 0; k < MaxPhases; k++)
            {
                GreenTicks[(slot * MaxPhases) + k] = k < PhaseCount[slot] ? GreenOf(in p, k) : (ushort)0;
            }

            if (p.Mode == SignalMode.Adaptive)
            {
                Machine[slot] = 1;
                ResetAdaptive(slot, tick);
            }
            else if ((p.Flags & SignalPlanFlags.Arrows) != 0 && p.ArrowMaxTicks > 0 && HasArrow[slot] != 0)
            {
                Machine[slot] = 1;
                ResetFixedArrow(slot);
            }
            else
            {
                ResetState(slot);
            }
        }
        else
        {
            Phase[slot] = 0;
            Interval[slot] = p.Mode == SignalMode.Off ? SignalInterval.Off : SignalInterval.Yellow;
            TicksLeft[slot] = 0;
        }
    }

    /// <summary>Позиция в цикле <c>p = Offset % Cycle</c>, проход по интервалам с начала цикла (O(3·фаз)).</summary>
    private void ResetState(int slot)
    {
        int cycle = CycleOf(slot);
        int pos = cycle > 0 ? OffsetTicks[slot] % cycle : 0;
        int pc = PhaseCount[slot];
        for (int k = 0; k < pc; k++)
        {
            for (int iv = 0; iv < 3; iv++)
            {
                int len = iv == 0 ? GreenTicks[(slot * MaxPhases) + k] : iv == 1 ? YellowTicks[slot] : AllRedTicks[slot];
                if (pos < len)
                {
                    Phase[slot] = (byte)k;
                    Interval[slot] = (SignalInterval)iv;
                    TicksLeft[slot] = (ushort)(len - pos);
                    return;
                }

                pos -= len;
            }
        }

        Phase[slot] = 0;
        Interval[slot] = SignalInterval.Green;
        TicksLeft[slot] = GreenTicks[slot * MaxPhases];
    }

    /// <summary>Общая часть начального состояния автомата: <c>Main</c> оси 0, зелёный, счётчики 0, рукава без спроса.</summary>
    private void ResetMachineCommon(int slot)
    {
        Phase[slot] = 0;
        Sub[slot] = 0;
        Interval[slot] = SignalInterval.Green;
        TicksLeft[slot] = 0;
        GreenMask[slot] = PhaseMask[slot * MaxPhases];
        NextMask[slot] = GreenMask[slot];
        NextAxis[slot] = 0;
        NextSub[slot] = 0;
        NextArrowArms[slot] = 0;
        ArrowArms[slot] = 0;
        GreenElapsed[slot] = 0;
        GapLeft[slot] = GapTicks[slot];
        SpillTicks[slot] = 0;
        for (int a = 0; a < ArmCap; a++)
        {
            ArmLeftYield[(slot * ArmCap) + a] = 0;
            ArmLeftHead[(slot * ArmCap) + a] = -1;
        }
    }

    /// <summary>Начальное состояние Adaptive; в координации (6.7) - ось и позиция в окне по часам <c>(tick + Offset) mod C</c>.</summary>
    private void ResetAdaptive(int slot, long tick)
    {
        ResetMachineCommon(slot);
        if (CycleTicks[slot] > 0 && PhaseCount[slot] == 2)
        {
            WindowClock(slot, tick, out int owner, out int sinceStart, out _);
            Phase[slot] = (byte)owner;
            NextAxis[slot] = (byte)owner;
            GreenMask[slot] = PhaseMask[(slot * MaxPhases) + owner];
            NextMask[slot] = GreenMask[slot];
            GreenElapsed[slot] = (ushort)sinceStart;
        }
    }

    /// <summary>
    /// Часы координации слота (6.7): <c>phi = (tick + Offset) mod C</c>; окно оси <c>k</c> - <c>[k*C/2, (k+1)*C/2)</c>. Принудительный конец зелёного предыдущей оси
    /// начинается за <c>Yellow + AllRed</c> до старта окна, поэтому «владелец» считается по <c>phi + yar</c>: <paramref name="owner"/> - ось, чей зелёный должен идти,
    /// <paramref name="sinceStart"/> - тиков от начала её окна (в сдвинутой шкале), <paramref name="remaining"/> - до следующей границы.
    /// </summary>
    public void WindowClock(int slot, long tick, out int owner, out int sinceStart, out int remaining)
    {
        int c = CycleTicks[slot];
        int half = c / 2;
        int yar = YellowTicks[slot] + AllRedTicks[slot];
        int ph = (int)((tick + OffsetTicks[slot] + yar) % c);
        owner = ph < half ? 0 : 1;
        sinceStart = ph - (owner * half);
        remaining = half - sinceStart;
    }

    /// <summary>
    /// <c>Fixed</c> со стрелкой: начало цикла, затем <c>Offset</c> «тихих» тиков того же автомата (позиция в цикле = <c>Offset mod Cycle</c> без
    /// пересчёта длины цикла). Счётчик включений стрелки не затрагивается.
    /// </summary>
    private void ResetFixedArrow(int slot)
    {
        ResetMachineCommon(slot);
        GapLeft[slot] = 0;
        long arrows = ArrowEntries;
        int n = OffsetTicks[slot];
        for (int t = 0; t < n; t++)
        {
            SignalPhase.StepFixedArrow(this, slot);
        }

        ArrowEntries = arrows;
    }

    /// <summary>Маска стрелки для множества рукавов <paramref name="arms"/> (6.5): один рукав - его манёвры + правые встречного, иначе левые+развороты+правые.</summary>
    public ulong ArrowMaskOf(int slot, int arms)
    {
        int b = slot * ArmCap;
        if (BitOperations.PopCount((uint)arms) == 1)
        {
            int a = BitOperations.TrailingZeroCount(arms);
            ulong m = ArmMask[b + a];
            int opp = ArmOpp[b + a];
            return opp >= 0 ? m | RightMask[b + opp] : m;
        }

        ulong r = 0;
        for (int a = 0; a < ArmCount(slot); a++)
        {
            if (((arms >> a) & 1) != 0)
            {
                r |= LeftMask[b + a] | RightMask[b + a];
            }
        }

        return r;
    }

    public int ArmCount(int slot) => ArmCountOf[slot];

    /// <summary>Биты рукавов оси <paramref name="axis"/> (по <see cref="ArmAxis"/>).</summary>
    public int ArmsOfAxis(int slot, int axis)
    {
        int b = slot * ArmCap;
        int r = 0;
        for (int a = 0; a < ArmCountOf[slot]; a++)
        {
            if (ArmAxis[b + a] == axis)
            {
                r |= 1 << a;
            }
        }

        return r;
    }

    /// <summary>Средняя задержка равномерного прибытия (routing.md 1.1) по плану слота для оси; 0 - плана нет (вызывающий берёт дефолт).</summary>
    public float StaticDelaySeconds(int slot, int axis)
    {
        int pc = PhaseCount[slot];
        if ((uint)axis >= (uint)pc)
        {
            return -1f;
        }

        int yar = YellowTicks[slot] + AllRedTicks[slot];
        if (Mode[slot] == SignalMode.Fixed)
        {
            int cycle = CycleOf(slot);
            if (cycle <= 0)
            {
                return -1f;
            }

            return Routing.RoutingCosts.SignalDelay(cycle / 20f, GreenTicks[(slot * MaxPhases) + axis] / 20f);
        }

        if (Mode[slot] == SignalMode.Adaptive)
        {
            if (CycleTicks[slot] > 0 && pc == 2)
            {
                // Координация (6.9): C и окно C/2 - Yellow - AllRed.
                return Routing.RoutingCosts.SignalDelay(CycleTicks[slot] / 20f, ((CycleTicks[slot] / 2) - yar) / 20f);
            }

            int cycle = 0;
            for (int k = 0; k < pc; k++)
            {
                cycle += ((MinGreenTicks[slot] + GreenTicks[(slot * MaxPhases) + k]) / 2) + yar;
            }

            int g = (MinGreenTicks[slot] + GreenTicks[(slot * MaxPhases) + axis]) / 2;
            return cycle > 0 ? Routing.RoutingCosts.SignalDelay(cycle / 20f, g / 20f) : -1f;
        }

        return -1f;
    }

    /// <summary>
    /// Выделяет слот узлу: маски фаз по геометрии (<see cref="SignalMasks"/>), статика Adaptive (рукава, входные полосы), план <paramref name="plan"/>
    /// (разрешённый <see cref="Resolve"/>; его PhaseCount должен совпасть с числом осей, иначе false - вызывающий подставляет план по умолчанию),
    /// <c>Node.Signal = slot</c>. Не меняет <c>Node.Control</c>.
    /// </summary>
    public bool TryAdd(World w, int node, in SetSignalPlanCmd plan, out int slot)
    {
        slot = -1;
        if (Count >= Capacity || (uint)node >= (uint)w.Net.NodeCount || w.Net.Nodes[node].Signal >= 0)
        {
            return false;
        }

        Span<ulong> masks = stackalloc ulong[MaxPhases];
        if (!SignalMasks.Build(w.Net, node, masks, out int axes))
        {
            return false;
        }

        SetSignalPlanCmd resolved = Resolve(in plan, axes, in w.Rules);
        if (!IsValid(in resolved, axes))
        {
            return false;
        }

        slot = Count++;
        Node[slot] = node;
        PhaseCount[slot] = (byte)axes;
        for (int k = 0; k < MaxPhases; k++)
        {
            PhaseMask[(slot * MaxPhases) + k] = masks[k];
        }

        BuildStatics(w, slot, node);
        ApplyPlan(slot, in resolved, w.Tick);
        XServed[slot] = 0;
        for (int j = 0; j < XSlots; j++)
        {
            XWalkLeft[(slot * XSlots) + j] = 0;
            XClearLeft[(slot * XSlots) + j] = 0;
        }

        SigXVersion = -1;
        w.Net.Nodes[node].Signal = slot;
        w.SignalCount = Count;
        return true;
    }

    /// <summary>Текущий план слота в виде команды (параметры, не состояние автомата): для перезапуска слота после перестройки узла (4.3, T3).</summary>
    internal SetSignalPlanCmd ReadPlan(int slot)
    {
        var p = new SetSignalPlanCmd
        {
            Node = Node[slot],
            Mode = Mode[slot],
            OffsetTicks = OffsetTicks[slot],
            PhaseCount = PhaseCount[slot],
            YellowTicks = YellowTicks[slot],
            AllRedTicks = AllRedTicks[slot],
            MinGreenTicks = MinGreenTicks[slot],
            GapTicks = GapTicks[slot],
            ArrowTriggerTicks = ArrowTriggerTicks[slot],
            ArrowMinTicks = ArrowMinTicks[slot],
            ArrowMaxTicks = ArrowMaxTicks[slot],
            CycleTicks = CycleTicks[slot],
            Flags = Flags[slot],
        };
        for (int k = 0; k < MaxPhases; k++)
        {
            SetGreen(ref p, k, GreenTicks[(slot * MaxPhases) + k]);
        }

        return p;
    }

    /// <summary>Номера фаз рукавов слота: для каждого сегмента-рукава узла - фаза, чьей маске принадлежат его connector'ы (-1 - нет). Сравнение до и после перестройки узла.</summary>
    internal void ArmPhases(World w, int slot, int node, Span<int> segs, Span<sbyte> phase, out int count)
    {
        NetworkStore net = w.Net;
        ref readonly NodeData nd = ref net.Nodes[node];
        count = 0;
        int conns = Math.Min(nd.ConnectorCount, SignalMasks.MaxMaskedConnectors);
        for (int c = 0; c < conns; c++)
        {
            int seg = net.Lanes[net.Lanes[nd.FirstConnector + c].FromLane].Segment;
            int at = -1;
            for (int k = 0; k < count; k++)
            {
                if (segs[k] == seg)
                {
                    at = k;
                    break;
                }
            }

            if (at < 0 && count < segs.Length)
            {
                at = count++;
                segs[at] = seg;
                phase[at] = -1;
            }

            if (at < 0)
            {
                continue;
            }

            for (int k = 0; k < PhaseCount[slot]; k++)
            {
                if ((PhaseMask[(slot * MaxPhases) + k] & (1UL << c)) != 0)
                {
                    phase[at] = (sbyte)k;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Слот узла после перестройки connector'ов (4.3, T3; player-tools.md §5 п. 4): маски фаз и статика заново; если оси узла те же (те же рукава в тех же фазах), план сохраняется
    /// (<paramref name="oldSegs"/>/<paramref name="oldPhase"/> - <see cref="ArmPhases"/> до перестройки), иначе - план по умолчанию для новых осей с прежними режимом и временами.
    /// Результат: 0 - план сохранён, 1 - сброшен (событие <c>SignalPlanReset</c> - у вызывающего), 2 - светофор снят (узел больше не может быть светофором).
    /// </summary>
    internal int Rebind(World w, int slot, int node, ReadOnlySpan<int> oldSegs, ReadOnlySpan<sbyte> oldPhase, int oldCount)
    {
        Span<ulong> masks = stackalloc ulong[MaxPhases];
        if (!SignalMasks.Build(w.Net, node, masks, out int axes))
        {
            Remove(w, slot);
            return 2;
        }

        SetSignalPlanCmd plan = ReadPlan(slot);
        bool same = axes == PhaseCount[slot];
        int result = 0;
        PhaseCount[slot] = (byte)axes;
        for (int k = 0; k < MaxPhases; k++)
        {
            PhaseMask[(slot * MaxPhases) + k] = masks[k];
        }

        if (same)
        {
            Span<int> segs = stackalloc int[NetworkStore.MaxArms];
            Span<sbyte> phase = stackalloc sbyte[NetworkStore.MaxArms];
            ArmPhases(w, slot, node, segs, phase, out int n);
            same = n == oldCount;
            for (int k = 0; k < n && same; k++)
            {
                int j = -1;
                for (int q = 0; q < oldCount; q++)
                {
                    if (oldSegs[q] == segs[k])
                    {
                        j = q;
                        break;
                    }
                }

                same = j >= 0 && oldPhase[j] == phase[k];
            }
        }

        if (!same)
        {
            SetSignalPlanCmd def = DefaultPlan(axes, in w.Rules, adaptive: plan.Mode == SignalMode.Adaptive);
            def.Mode = plan.Mode;
            def.YellowTicks = plan.YellowTicks;
            def.AllRedTicks = plan.AllRedTicks;
            def.OffsetTicks = plan.OffsetTicks;
            if (plan.Mode == SignalMode.Off || plan.Mode == SignalMode.FlashYellow)
            {
                def.Flags = 0;
            }

            plan = def;
            result = 1;
        }

        plan.PhaseCount = (byte)axes;
        SetSignalPlanCmd resolved = Resolve(in plan, axes, in w.Rules);
        if (!IsValid(in resolved, axes))
        {
            resolved = Resolve(DefaultPlan(w, node, axes), axes, in w.Rules);
            result = 1;
        }

        BuildStatics(w, slot, node);
        ApplyPlan(slot, in resolved, w.Tick);
        SigXVersion = -1;
        return result;
    }

    /// <summary>Загрузка сейва: статика (производное от сети и <c>PhaseMask</c>) всех слотов тем же кодом, что при <see cref="TryAdd"/>.</summary>
    internal void RebuildStatics(World w)
    {
        for (int s = 0; s < Count; s++)
        {
            BuildStatics(w, s, Node[s]);
        }
    }

    /// <summary>Статика слота (6.5): маски рукавов, оси, встречные, возможность стрелки, список входных полос узла.</summary>
    private void BuildStatics(World w, int slot, int node)
    {
        NetworkStore net = w.Net;
        ref readonly NodeData nd = ref net.Nodes[node];
        int arms = Math.Min(nd.ArmCount, ArmCap);
        int b = slot * ArmCap;
        ArmCountOf[slot] = (byte)arms;
        for (int a = 0; a < ArmCap; a++)
        {
            ArmMask[b + a] = 0;
            LeftMask[b + a] = 0;
            RightMask[b + a] = 0;
            ArmAxis[b + a] = -1;
            ArmOpp[b + a] = -1;
        }

        int conns = Math.Min(nd.ConnectorCount, SignalMasks.MaxMaskedConnectors);
        for (int c = 0; c < conns; c++)
        {
            ref readonly LaneData cl = ref net.Lanes[nd.FirstConnector + c];
            int fromSeg = net.Lanes[cl.FromLane].Segment;
            for (int a = 0; a < arms; a++)
            {
                if (net.NodeArms[nd.FirstArm + a] != fromSeg)
                {
                    continue;
                }

                ulong bit = 1UL << c;
                ArmMask[b + a] |= bit;
                Maneuver man = ManeuverOf(w, in cl);
                if (man == Maneuver.Left || man == Maneuver.UTurn)
                {
                    LeftMask[b + a] |= bit;
                }
                else if (man == Maneuver.Right)
                {
                    RightMask[b + a] |= bit;
                }

                break;
            }
        }

        int axes = PhaseCount[slot];
        for (int a = 0; a < arms; a++)
        {
            for (int k = 0; k < axes; k++)
            {
                if ((ArmMask[b + a] & PhaseMask[(slot * MaxPhases) + k]) != 0)
                {
                    ArmAxis[b + a] = (sbyte)k;
                    break;
                }
            }
        }

        ushort hasArrow = 0;
        for (int a = 0; a < arms; a++)
        {
            if (ArmAxis[b + a] < 0)
            {
                continue;
            }

            int opp = -1;
            int sameAxis = 0;
            for (int o = 0; o < arms; o++)
            {
                if (o != a && ArmAxis[b + o] == ArmAxis[b + a])
                {
                    opp = o;
                    sameAxis++;
                }
            }

            if (sameAxis == 1)
            {
                ArmOpp[b + a] = (sbyte)opp;
                if (axes == 2 && LeftMask[b + a] != 0)
                {
                    hasArrow |= (ushort)(1 << a);
                }
            }
        }

        HasArrow[slot] = hasArrow;

        // Входные полосы узла (по рукавам): Normal-полосы направления «к узлу», чьи connector'ы принадлежат этому узлу.
        int n = 0;
        for (int a = 0; a < arms && n < MaxSigLanes; a++)
        {
            if (ArmAxis[b + a] < 0)
            {
                continue;
            }

            ref readonly SegmentData sd = ref net.Segments[net.NodeArms[nd.FirstArm + a]];
            int first;
            int cnt;
            if (sd.NodeB == node)
            {
                first = sd.FirstLaneAB;
                cnt = sd.LanesAB;
            }
            else if (sd.NodeA == node)
            {
                first = sd.FirstLaneBA;
                cnt = sd.LanesBA;
            }
            else
            {
                continue;
            }

            for (int li = 0; li < cnt && n < MaxSigLanes; li++)
            {
                int lane = first + li;
                ref readonly LaneData ld = ref net.Lanes[lane];
                if (ld.NextCount <= 0 || net.Lanes[net.LaneNext[ld.FirstNext]].Node != node)
                {
                    continue;
                }

                bool left = false;
                for (int k = 0; k < ld.NextCount; k++)
                {
                    Maneuver man = ManeuverOf(w, in net.Lanes[net.LaneNext[ld.FirstNext + k]]);
                    if (man == Maneuver.Left || man == Maneuver.UTurn)
                    {
                        left = true;
                    }
                }

                int idx = (slot * MaxSigLanes) + n;
                SigLane[idx] = lane;
                SigLaneArm[idx] = (byte)a;
                SigLaneAxis[idx] = (byte)ArmAxis[b + a];
                SigLaneLeft[idx] = left;
                n++;
            }
        }

        SigLaneCount[slot] = (byte)n;
    }

    private static Maneuver ManeuverOf(World w, in LaneData connector)
    {
        int g = connector.ConflictGroup;
        return g >= 0 && g < w.Connectors.Count ? (Maneuver)w.Connectors.Maneuver[g] : Maneuver.Straight;
    }

    /// <summary>Число осей (фаз по умолчанию) узла или -1, если маски построить нельзя.</summary>
    public static int AxesOf(World w, int node)
    {
        Span<ulong> masks = stackalloc ulong[MaxPhases];
        return SignalMasks.Build(w.Net, node, masks, out int axes) ? axes : -1;
    }

    /// <summary>
    /// swap-remove слота: у переставленного узла обновляется <c>Node.Signal</c>, <c>LaneSignals</c> connector'ов
    /// удалённого узла -> <c>None</c>. Не меняет <c>Node.Control</c>.
    /// </summary>
    public void Remove(World w, int slot)
    {
        int node = Node[slot];
        ref readonly NodeData nd = ref w.Net.Nodes[node];
        for (int c = 0; c < nd.ConnectorCount; c++)
        {
            w.LaneSignals[nd.FirstConnector + c] = (byte)LaneSignal.None;
        }

        w.Net.Nodes[node].Signal = -1;
        int last = Count - 1;
        if (slot != last)
        {
            CopySlot(slot, last);
            w.Net.Nodes[Node[slot]].Signal = slot;
        }

        Count = last;
        w.SignalCount = Count;
        w.SignalStates[last] = default;
        XServed[last] = 0;
        for (int j = 0; j < XSlots; j++)
        {
            XWalkLeft[(last * XSlots) + j] = 0;
            XClearLeft[(last * XSlots) + j] = 0;
        }

        SigXVersion = -1;
    }

    private void CopySlot(int dst, int src)
    {
        Node[dst] = Node[src];
        Mode[dst] = Mode[src];
        PhaseCount[dst] = PhaseCount[src];
        YellowTicks[dst] = YellowTicks[src];
        AllRedTicks[dst] = AllRedTicks[src];
        OffsetTicks[dst] = OffsetTicks[src];
        Phase[dst] = Phase[src];
        Interval[dst] = Interval[src];
        TicksLeft[dst] = TicksLeft[src];
        MinGreenTicks[dst] = MinGreenTicks[src];
        GapTicks[dst] = GapTicks[src];
        ArrowTriggerTicks[dst] = ArrowTriggerTicks[src];
        ArrowMinTicks[dst] = ArrowMinTicks[src];
        ArrowMaxTicks[dst] = ArrowMaxTicks[src];
        ArrowGapTicks[dst] = ArrowGapTicks[src];
        CycleTicks[dst] = CycleTicks[src];
        Flags[dst] = Flags[src];
        Machine[dst] = Machine[src];
        Sub[dst] = Sub[src];
        GreenMask[dst] = GreenMask[src];
        NextMask[dst] = NextMask[src];
        NextAxis[dst] = NextAxis[src];
        NextSub[dst] = NextSub[src];
        NextArrowArms[dst] = NextArrowArms[src];
        ArrowArms[dst] = ArrowArms[src];
        GreenElapsed[dst] = GreenElapsed[src];
        GapLeft[dst] = GapLeft[src];
        SpillTicks[dst] = SpillTicks[src];
        XServed[dst] = XServed[src];
        for (int j = 0; j < XSlots; j++)
        {
            XWalkLeft[(dst * XSlots) + j] = XWalkLeft[(src * XSlots) + j];
            XClearLeft[(dst * XSlots) + j] = XClearLeft[(src * XSlots) + j];
        }

        SigXVersion = -1;   // таблицы пешеходной фазы пересоберутся
        HasArrow[dst] = HasArrow[src];
        ArmCountOf[dst] = ArmCountOf[src];
        SigLaneCount[dst] = SigLaneCount[src];
        for (int k = 0; k < MaxPhases; k++)
        {
            GreenTicks[(dst * MaxPhases) + k] = GreenTicks[(src * MaxPhases) + k];
            PhaseMask[(dst * MaxPhases) + k] = PhaseMask[(src * MaxPhases) + k];
        }

        for (int a = 0; a < ArmCap; a++)
        {
            int d = (dst * ArmCap) + a;
            int s = (src * ArmCap) + a;
            ArmLeftYield[d] = ArmLeftYield[s];
            ArmLeftHead[d] = ArmLeftHead[s];
            ArmMask[d] = ArmMask[s];
            LeftMask[d] = LeftMask[s];
            RightMask[d] = RightMask[s];
            ArmAxis[d] = ArmAxis[s];
            ArmOpp[d] = ArmOpp[s];
        }

        for (int k = 0; k < MaxSigLanes; k++)
        {
            int d = (dst * MaxSigLanes) + k;
            int s = (src * MaxSigLanes) + k;
            SigLane[d] = SigLane[s];
            SigLaneArm[d] = SigLaneArm[s];
            SigLaneAxis[d] = SigLaneAxis[s];
            SigLaneLeft[d] = SigLaneLeft[s];
        }
    }

    /// <summary>
    /// Заводит светофоры по <c>w.SignalPlanSeeds</c> (вызывается интеграцией после построения сети и connector'ов, один раз).
    /// Seed с PhaseCount, не совпавшим с числом осей, или узел без построимых масок: план по умолчанию / пропуск.
    /// Seed с <c>Adaptive</c> - режим <see cref="SignalMode.Adaptive"/> (Green0/Green1 - MaxGreen осей, остальное - дефолты <c>RulesDesc</c>, стрелка по спросу).
    /// Устанавливает <c>w.SignalCount = Count</c>. Возвращает число заведённых светофоров.
    /// </summary>
    public int InitFromSeeds(World w)
    {
        while (Count > 0)
        {
            Remove(w, Count - 1);
        }

        SignalPlanSeed[] seeds = w.SignalPlanSeeds;
        for (int i = 0; i < seeds.Length; i++)
        {
            SignalPlanSeed s = seeds[i];
            if ((uint)s.Node >= (uint)w.Net.NodeCount)
            {
                continue;
            }

            var plan = new SetSignalPlanCmd
            {
                Mode = s.Adaptive ? SignalMode.Adaptive : SignalMode.Fixed,
                PhaseCount = s.PhaseCount,
                OffsetTicks = s.OffsetTicks,
                Green0 = s.Green0,
                Green1 = s.Green1,
                YellowTicks = s.YellowTicks,
                AllRedTicks = s.AllRedTicks,
                Flags = s.Adaptive ? SignalPlanFlags.Arrows : (byte)0,
                CycleTicks = s.CycleTicks,
            };
            if (!TryAdd(w, s.Node, in plan, out _))
            {
                int axes = AxesOf(w, s.Node);
                if (axes < 1)
                {
                    continue;
                }

                SetSignalPlanCmd def = DefaultPlan(w, s.Node, axes, s.Adaptive);
                def.OffsetTicks = s.OffsetTicks;
                def.CycleTicks = axes == 2 ? s.CycleTicks : (ushort)0;
                TryAdd(w, s.Node, in def, out _);
            }
        }

        w.SignalCount = Count;
        return Count;
    }
}
