using System;
using System.Numerics;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Peds;

/// <summary>
/// Пешеходная фаза светофора по вызову (ADR-0020, pedestrians.md §6, intersections.md §6.5): статика переходов слота (<c>SigX</c>), <c>Walk/Clear</c>, удержание оси, вызов оси пешеходом,
/// публикация сигнала перехода (<c>CrossingSignals</c>, биты 2-3). Вызывается из <c>SignalPhase</c> (фаза 4a) по слотам по возрастанию, однопоточно. Без аллокаций.
/// <para>
/// <c>Adaptive</c>: <c>Walk</c> стартует в первый тик <c>Main</c> своей оси при вызове (при длинном зелёном без координации - после головного старта машин, <see cref="HeadStartTicks"/>) либо позже (поздний вызов, если успевает <c>Walk + Clear</c>), пока идут <c>Walk</c>/<c>Clear</c>, ось
/// не завершает <c>Main</c>. <c>Fixed</c>: <c>Walk</c> в первый тик зелёного оси при вызове, длительность <c>min(Walk, Green - Clear)</c> не меньше 3 с, иначе на весь зелёный.
/// </para>
/// </summary>
internal static class PedSignals
{
    /// <summary>Сигнал перехода: биты 2-3 <c>CrossingSignals</c>.</summary>
    public const byte DontWalk = 0;
    public const byte Walk = 1;
    public const byte Clear = 2;

    /// <summary>Минимальная длительность <c>Walk</c> у <c>Fixed</c>: короче нельзя - переход нерегулируемый внутри зелёного (3 с).</summary>
    private const int MinFixedWalkTicks = 60;

    public static bool Enabled => true;

    public static bool IsWalk(CrossingState xs, int x) => ((xs.CrossingSignals[x] >> 2) & 3) == Walk;

    /// <summary>Публикует состояние автомата перехода (биты 0-1), сохраняя сигнал.</summary>
    public static void PublishState(CrossingState xs, int x) =>
        xs.CrossingSignals[x] = (byte)((xs.CrossingSignals[x] & 0xC) | xs.XState[x]);

    private static void PublishSignal(CrossingState xs, int x, byte signal) =>
        xs.CrossingSignals[x] = (byte)((xs.CrossingSignals[x] & 0x3) | (signal << 2));

    // ------------------------------------------------------------------ статика слотов

    /// <summary>Пересобирает <c>SigX</c> слотов, если изменилась сеть или число светофоров (производное, вне хэша и сейва).</summary>
    public static void EnsureTables(World w)
    {
        SignalStore s = w.Signals;
        if (s.SigXVersion == w.Net.Version && s.SigXBuiltCount == s.Count)
        {
            return;
        }

        PedWorld pw = w.Peds!;
        PedNetwork pn = pw.Net;
        int clearSpeed = Math.Max(1, pw.Cfg.ClearSpeedMmPerTick);
        for (int slot = 0; slot < s.Count; slot++)
        {
            ref readonly NodeData nd = ref w.Net.Nodes[s.Node[slot]];
            int n = 0;
            int arms = Math.Min(nd.ArmCount, s.ArmCountOf[slot]);
            for (int a = 0; a < arms && n < SignalStore.XSlots; a++)
            {
                int x = nd.FirstArm + a;
                if (x >= pn.MidXBase || pn.XEdge[x] < 0)
                {
                    continue;
                }

                int idx = (slot * SignalStore.XSlots) + n;
                int axisA = s.ArmAxis[(slot * SignalStore.ArmCap) + a];
                int best = -1;
                if (axisA >= 0 && s.PhaseCount[slot] >= 2)
                {
                    ulong conn = pn.XIn[x] | pn.XOut[x];
                    int bestCount = int.MaxValue;
                    for (int j = 0; j < s.PhaseCount[slot]; j++)
                    {
                        if (j == axisA)
                        {
                            continue;
                        }

                        int c = BitOperations.PopCount(s.PhaseMask[(slot * SignalStore.MaxPhases) + j] & conn);
                        if (c < bestCount)
                        {
                            bestCount = c;
                            best = j;
                        }
                    }
                }

                int len = pn.Edges[pn.XEdge[x]].LenMm;
                s.SigXIdx[idx] = x;
                s.SigXAxis[idx] = (sbyte)best;
                s.SigXClear[idx] = (ushort)Math.Clamp(((long)len + clearSpeed - 1) / clearSpeed, 1, ushort.MaxValue);
                n++;
            }

            s.SigXCount[slot] = (byte)n;
        }

        s.SigXVersion = w.Net.Version;
        s.SigXBuiltCount = s.Count;
    }

    /// <summary>Минимум для координации (intersections.md §6.7): <c>Walk + Clear_max</c> по переходам узла <paramref name="node"/>, тики; 0 без <c>peds</c>.</summary>
    public static int CoordMinGreenTicks(World w, int node)
    {
        PedWorld? pw = w.Peds;
        if (pw == null)
        {
            return 0;
        }

        PedNetwork pn = pw.Net;
        ref readonly NodeData nd = ref w.Net.Nodes[node];
        int clearSpeed = Math.Max(1, pw.Cfg.ClearSpeedMmPerTick);
        int maxClear = 0;
        for (int a = 0; a < nd.ArmCount; a++)
        {
            int x = nd.FirstArm + a;
            if (x < pn.XEdge.Length && pn.XEdge[x] >= 0)
            {
                maxClear = Math.Max(maxClear, (pn.Edges[pn.XEdge[x]].LenMm + clearSpeed - 1) / clearSpeed);
            }
        }

        return maxClear == 0 ? 0 : pw.Cfg.WalkTicks + maxClear;
    }

    // ------------------------------------------------------------------ удержание и вызов

    /// <summary>true: у перехода текущей зелёной оси идёт <c>Walk</c> или <c>Clear</c> - ось не завершает <c>Main</c> (состояние конца прошлого тика).</summary>
    public static bool Hold(World w, SignalStore s, int slot)
    {
        if (s.Interval[slot] != SignalInterval.Green || s.Sub[slot] != 0)
        {
            return false;
        }

        CrossingState xs = w.Peds!.X;
        int k = s.Phase[slot];
        int n = s.SigXCount[slot];
        int b = slot * SignalStore.XSlots;
        for (int j = 0; j < n; j++)
        {
            // Clear держит зелёный, пока на переходе есть пешеходы: когда все сошли, остаток таймера зелёную ось не задерживает.
            if (s.SigXAxis[b + j] == k && (s.XWalkLeft[b + j] != 0 || (s.XClearLeft[b + j] != 0 && xs.OnCross[s.SigXIdx[b + j]] > 0)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Окно машин после <c>Walk + Clear</c>, тики: поворачивающие через переход своей зелёной оси успевают проехать (сверх MaxGreen).</summary>
    private const int OvertimeCarTicks = 160;

    /// <summary>
    /// Головной старт машин, тики (Adaptive без координации): <c>Walk</c> начинается не в первый тик зелёного, а через 3 с. Очередь поворачивающих на стоп-линии успевает войти в узел до заявки перехода;
    /// иначе <c>Walk + Clear</c> (до 19 с из 24 с MaxGreen) закрывал бы переход для поворотов на весь зелёный, и голова полосы пропускала целый цикл (доля ожиданий машин >= 60 с).
    /// </summary>
    private const int HeadStartTicks = 60;

    /// <summary>Предел продления последнего тика <c>Walk</c> ради обязавшихся машин, в длительностях <c>Walk</c> от заявки (медленный левый поворот по длинному connector'у занимает зону 10+ с).</summary>
    private const int ExtendWalkFactor = 4;

    /// <summary>
    /// Предел продления зелёного оси <paramref name="k"/> ради машин, ждавших пешеходов: <c>Walk + Clear_max + CarWindowTicks</c> по переходам этой оси, обслуженным в текущем зелёном (0 - не было).
    /// </summary>
    public static int OvertimeTicks(World w, SignalStore s, int slot, int k)
    {
        int n = s.SigXCount[slot];
        int b = slot * SignalStore.XSlots;
        int clear = 0;
        for (int j = 0; j < n; j++)
        {
            if (s.SigXAxis[b + j] == k && ((s.XServed[slot] >> j) & 1) != 0)
            {
                clear = Math.Max(clear, s.SigXClear[b + j]);
            }
        }

        return clear == 0 ? 0 : w.Peds!.Cfg.WalkTicks + clear + OvertimeCarTicks;
    }

    /// <summary>Биты осей, которых ждут пешеходы (очередь у перехода без <c>Walk</c>/<c>Clear</c>; для текущей зелёной оси <c>Walk</c> стартует сам).</summary>
    public static int CallMask(World w, SignalStore s, int slot)
    {
        CrossingState xs = w.Peds!.X;
        int n = s.SigXCount[slot];
        int b = slot * SignalStore.XSlots;
        bool main = s.Interval[slot] == SignalInterval.Green && s.Sub[slot] == 0;
        int k = s.Phase[slot];
        int call = 0;
        for (int j = 0; j < n; j++)
        {
            int axis = s.SigXAxis[b + j];
            if (axis < 0 || xs.QCount[s.SigXIdx[b + j]] == 0 || s.XWalkLeft[b + j] != 0 || s.XClearLeft[b + j] != 0)
            {
                continue;
            }

            if (main && axis == k)
            {
                continue;
            }

            call |= 1 << axis;
        }

        return call;
    }

    // ------------------------------------------------------------------ шаг слота

    /// <summary>
    /// Шаг <c>Walk/Clear</c> слота после шага автомата светофора этого тика: счётчики, старт <c>Walk</c>, публикация сигналов переходов. Для не цветного режима сигнал сбрасывается в <c>DontWalk</c>
    /// (переходы такого узла нерегулируемые).
    /// </summary>
    public static void StepSlot(World w, SignalStore s, int slot)
    {
        CrossingState xs = w.Peds!.X;
        PedDesc cfg = w.Peds.Cfg;
        int n = s.SigXCount[slot];
        int b = slot * SignalStore.XSlots;
        SignalMode mode = s.Mode[slot];
        if (mode != SignalMode.Fixed && mode != SignalMode.Adaptive)
        {
            for (int j = 0; j < n; j++)
            {
                s.XWalkLeft[b + j] = 0;
                s.XClearLeft[b + j] = 0;
                PublishSignal(xs, s.SigXIdx[b + j], DontWalk);
            }

            s.XServed[slot] = 0;
            return;
        }

        bool machine = s.Machine[slot] != 0;
        bool main = s.Interval[slot] == SignalInterval.Green && s.Sub[slot] == 0;
        int k = s.Phase[slot];
        bool greenStart = main && (machine ? s.GreenElapsed[slot] == 1 : s.TicksLeft[slot] == s.GreenTicks[(slot * SignalStore.MaxPhases) + k]);
        byte served = s.XServed[slot];
        for (int j = 0; j < n; j++)
        {
            int idx = b + j;
            int x = s.SigXIdx[idx];
            int axis = s.SigXAxis[idx];
            if (axis < 0)
            {
                PublishSignal(xs, x, Walk);   // нет встречной оси (один фазовый набор): переход свободен
                continue;
            }

            int walk = s.XWalkLeft[idx];
            int clear = s.XClearLeft[idx];
            int bit = 1 << j;
            if (!main || k != axis)
            {
                walk = 0;
                clear = 0;
                served &= (byte)~bit;
            }
            else
            {
                if (greenStart)
                {
                    served &= (byte)~bit;
                    walk = 0;
                    clear = 0;
                }

                if (walk > 0)
                {
                    if (walk > 1 || !ExtendWalk(w, s, slot, idx, x, in cfg))
                    {
                        walk--;
                    }

                    if (walk == 0)
                    {
                        clear = s.SigXClear[idx];
                    }
                }
                else if (clear > 0)
                {
                    clear--;
                }

                // Зелёная ось «отдыхает» дольше MaxGreen (встречного спроса нет, иначе Main уже закончился): вызова оси нет, а следующего greenStart без спроса не будет -
                // обслуженный переход и поздний вызов не должны ждать его вечно (корень «пешеходы часами у переходов», L14).
                // Но не при вызове другой оси (L15): иначе непрерывный поток пешеходов раз за разом продлевает зелёный и морит встречную ось; при вызове Main кончается, пешеход ждёт своего greenStart.
                bool rest = s.Mode[slot] == SignalMode.Adaptive && s.GreenElapsed[slot] >= s.GreenTicks[(slot * SignalStore.MaxPhases) + k] && s.PendingOther[slot] == 0;
                if (walk == 0 && clear == 0 && ((served & bit) == 0 || rest) && xs.QCount[x] > 0)
                {
                    int dur = WalkTicks(w, s, slot, idx, k, greenStart, in cfg);
                    if (dur > 0)
                    {
                        walk = dur;
                        served |= (byte)bit;
                    }
                }
            }

            s.XWalkLeft[idx] = (ushort)walk;
            s.XClearLeft[idx] = (ushort)clear;
            PublishSignal(xs, x, walk > 0 ? Walk : (clear > 0 ? Clear : DontWalk));
        }

        s.XServed[slot] = served;
    }

    /// <summary>
    /// true: последний тик <c>Walk</c> продлевается (pedestrians.md §5.10). Переход заявлен (<c>Claimed</c>: новые машины не допускаются), но на нём ещё есть обязавшиеся - допущенные до начала <c>Walk</c> медленные
    /// машины не успевают освободить зону за 5 с, и пешеход иначе ждёт следующего зелёного (голодание). Продление ограничено <c>2 * Walk</c> от заявки; <c>Fixed</c> - только если до конца зелёного остаётся <c>Clear</c>.
    /// </summary>
    private static bool ExtendWalk(World w, SignalStore s, int slot, int idx, int x, in PedDesc cfg)
    {
        CrossingState xs = w.Peds!.X;
        if (xs.XState[x] != XStateCode.Claimed || xs.QCount[x] == 0 || w.Tick - xs.ClosedSince[x] >= ExtendWalkFactor * cfg.WalkTicks)
        {
            return false;
        }

        if (s.Mode[slot] == SignalMode.Adaptive)
        {
            return true;   // Hold держит Main, пока идёт Walk
        }

        int left = s.Machine[slot] != 0
            ? s.GreenTicks[(slot * SignalStore.MaxPhases) + s.Phase[slot]] - s.GreenElapsed[slot]
            : s.TicksLeft[slot];
        return left > s.SigXClear[idx] + 1;
    }

    /// <summary>Длительность <c>Walk</c>, которую можно начать сейчас, или 0 (нельзя).</summary>
    private static int WalkTicks(World w, SignalStore s, int slot, int idx, int k, bool greenStart, in PedDesc cfg)
    {
        int clear = s.SigXClear[idx];
        int green = s.GreenTicks[(slot * SignalStore.MaxPhases) + k];
        if (s.Mode[slot] == SignalMode.Fixed)
        {
            if (!greenStart)
            {
                return 0;
            }

            int d = Math.Min(cfg.WalkTicks, green - clear);
            return d >= MinFixedWalkTicks ? d : green;   // короче 3 с нельзя: на весь зелёный (нерегулируемый внутри зелёного)
        }

        // Головной старт машин: только без координации (окна считают Walk с первого тика) и если зелёный вмещает задержку + Walk + Clear (короткий зелёный второстепенной оси - Walk сразу, иначе пешеход не дождётся).
        bool headStart = s.CycleTicks[slot] == 0 && green >= HeadStartTicks + cfg.WalkTicks + clear;
        if (greenStart && !headStart)
        {
            return cfg.WalkTicks;
        }

        if (headStart && s.GreenElapsed[slot] < HeadStartTicks)
        {
            return 0;
        }

        // Поздний вызов: Walk + Clear помещаются до max-out оси и (в координации) до силового конца своего окна.
        int need = cfg.WalkTicks + clear;
        if (s.GreenElapsed[slot] >= green && s.PendingOther[slot] != 0)
        {
            return 0;   // L15: зелёный уже за MaxGreen и другая ось ждёт - Main кончается, Walk не продлевает его
        }

        if (s.GreenElapsed[slot] + need > green && s.GreenElapsed[slot] < green)
        {
            return 0;   // не помещается до max-out: ждёт его (при отдыхе зелёного дольше MaxGreen Walk всё равно стартует, Hold держит Main на Walk + Clear)
        }

        if (s.CycleTicks[slot] > 0 && s.PhaseCount[slot] == 2)
        {
            s.WindowClock(slot, w.Tick, out int owner, out _, out int remaining);
            if (owner != k || remaining < need + s.YellowTicks[slot])
            {
                return 0;
            }
        }

        return cfg.WalkTicks;
    }
}
