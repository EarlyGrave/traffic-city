using System;
using Sim.Core.Api;
using Sim.Core.Gen;
using Sim.Core.Network;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Хранилище светофоров (core-api 6.7, intersections.md 6): плотные массивы по слотам; слот = индекс в
/// <c>SignalStates</c> и значение <c>NodeData.Signal</c>. Автомат фаз - <see cref="SignalPhase"/>.
/// Все массивы выделены в конструкторе, дальше аллокаций нет.
/// </summary>
internal sealed class SignalStore
{
    public const int MaxPhases = SignalMasks.MaxPhases;
    public const int MaxCycleTicks = 65535;

    public readonly int Capacity;
    public int Count;

    public readonly int[] Node;
    public readonly SignalMode[] Mode;
    public readonly byte[] PhaseCount;
    public readonly ushort[] GreenTicks;     // [Capacity * 8]
    public readonly ushort[] YellowTicks;
    public readonly ushort[] AllRedTicks;
    public readonly ushort[] OffsetTicks;
    public readonly byte[] Phase;
    public readonly SignalInterval[] Interval;
    public readonly ushort[] TicksLeft;
    public readonly ulong[] PhaseMask;       // [Capacity * 8], бит = connector - Node.FirstConnector

    public SignalStore(int capacity)
    {
        Capacity = Math.Max(0, capacity);
        Node = new int[Capacity];
        Mode = new SignalMode[Capacity];
        PhaseCount = new byte[Capacity];
        GreenTicks = new ushort[Capacity * MaxPhases];
        YellowTicks = new ushort[Capacity];
        AllRedTicks = new ushort[Capacity];
        OffsetTicks = new ushort[Capacity];
        Phase = new byte[Capacity];
        Interval = new SignalInterval[Capacity];
        TicksLeft = new ushort[Capacity];
        PhaseMask = new ulong[Capacity * MaxPhases];
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

    /// <summary>Длительность цикла в тиках (Σ Green_k + PhaseCount·(Yellow + AllRed)).</summary>
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
    /// План по умолчанию (intersections.md 6.2): зелёный 24 с при &lt;= 2 фазах, 16 с при 3, 12 с при &gt;= 4;
    /// жёлтый/all-red - <c>Rules.DefaultYellowTicks/DefaultAllRedTicks</c> (если оба 0 - 3 с и 1 с); смещение 0.
    /// </summary>
    public static SetSignalPlanCmd DefaultPlan(int phases, in RulesDesc rules)
    {
        ushort green = (ushort)(phases <= 2 ? 480 : phases == 3 ? 320 : 240);
        int yellow = rules.DefaultYellowTicks;
        int allRed = rules.DefaultAllRedTicks;
        if (yellow <= 0 && allRed <= 0)
        {
            yellow = 60;
            allRed = 20;
        }

        return new SetSignalPlanCmd
        {
            Mode = SignalMode.Fixed,
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
        };
    }

    /// <summary>
    /// Проверка плана (6.3): режим известен; для <see cref="SignalMode.Fixed"/> - PhaseCount совпадает с числом осей узла,
    /// Green_k &gt;= 1, Σ цикл &lt;= 65535. Для Off/FlashYellow параметры цикла не проверяются.
    /// </summary>
    public static bool IsValid(in SetSignalPlanCmd p, int axes)
    {
        if (p.Mode == SignalMode.Off || p.Mode == SignalMode.FlashYellow)
        {
            return true;
        }

        if (p.Mode != SignalMode.Fixed || p.PhaseCount != axes || axes < 1 || axes > MaxPhases)
        {
            return false;
        }

        int cycle = axes * (p.YellowTicks + p.AllRedTicks);
        for (int k = 0; k < axes; k++)
        {
            ushort g = GreenOf(in p, k);
            if (g < 1)
            {
                return false;
            }

            cycle += g;
        }

        return cycle <= MaxCycleTicks;
    }

    /// <summary>Записывает план в слот и пересчитывает начальное состояние по <c>OffsetTicks</c>. План должен быть валиден.</summary>
    public void ApplyPlan(int slot, in SetSignalPlanCmd p)
    {
        Mode[slot] = p.Mode;
        YellowTicks[slot] = p.YellowTicks;
        AllRedTicks[slot] = p.AllRedTicks;
        OffsetTicks[slot] = p.OffsetTicks;
        if (p.Mode == SignalMode.Fixed)
        {
            for (int k = 0; k < MaxPhases; k++)
            {
                GreenTicks[(slot * MaxPhases) + k] = k < PhaseCount[slot] ? GreenOf(in p, k) : (ushort)0;
            }

            ResetState(slot);
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

    /// <summary>
    /// Выделяет слот узлу: маски фаз по геометрии (<see cref="SignalMasks"/>), план <paramref name="plan"/>
    /// (его PhaseCount должен совпасть с числом осей, иначе false - вызывающий подставляет план по умолчанию),
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
        if (!SignalMasks.Build(w.Net, node, masks, out int axes) || !IsValid(in plan, axes))
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

        ApplyPlan(slot, in plan);
        w.Net.Nodes[node].Signal = slot;
        w.SignalCount = Count;
        return true;
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
            Node[slot] = Node[last];
            Mode[slot] = Mode[last];
            PhaseCount[slot] = PhaseCount[last];
            YellowTicks[slot] = YellowTicks[last];
            AllRedTicks[slot] = AllRedTicks[last];
            OffsetTicks[slot] = OffsetTicks[last];
            Phase[slot] = Phase[last];
            Interval[slot] = Interval[last];
            TicksLeft[slot] = TicksLeft[last];
            for (int k = 0; k < MaxPhases; k++)
            {
                GreenTicks[(slot * MaxPhases) + k] = GreenTicks[(last * MaxPhases) + k];
                PhaseMask[(slot * MaxPhases) + k] = PhaseMask[(last * MaxPhases) + k];
            }

            w.Net.Nodes[Node[slot]].Signal = slot;
        }

        Count = last;
        w.SignalCount = Count;
        w.SignalStates[last] = default;
    }

    /// <summary>
    /// Заводит светофоры по <c>w.SignalPlanSeeds</c> (вызывается интеграцией после построения сети и connector'ов, один раз).
    /// Seed с PhaseCount, не совпавшим с числом осей, или узел без построимых масок: план по умолчанию / пропуск.
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
                Mode = SignalMode.Fixed,
                PhaseCount = s.PhaseCount,
                OffsetTicks = s.OffsetTicks,
                Green0 = s.Green0,
                Green1 = s.Green1,
                YellowTicks = s.YellowTicks,
                AllRedTicks = s.AllRedTicks,
            };
            if (!TryAdd(w, s.Node, in plan, out _))
            {
                int axes = AxesOf(w, s.Node);
                if (axes < 1)
                {
                    continue;
                }

                SetSignalPlanCmd def = DefaultPlan(axes, in w.Rules);
                def.OffsetTicks = s.OffsetTicks;
                TryAdd(w, s.Node, in def, out _);
            }
        }

        w.SignalCount = Count;
        return Count;
    }
}
