using System;
using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>Итог разбора <c>PaintZone</c>: сколько участков и клеток получат зону, сколько зданий уйдёт в <c>Abandoned</c> (общий для превью и применения).</summary>
internal struct ZonePlan
{
    public float X0, Z0, X1, Z1;   // упорядоченный прямоугольник
    public int Lots;               // участки под командой (центр внутри, не Lost)
    public int Changed;            // из них реально меняются (вид, плотность или флаг Painted)
    public int Cells;              // клетки 8 м этих участков
    public int Abandon;            // здания другого вида, уходящие в Abandoned
    public int RectCells;          // клетки прямоугольника (ceil по осям)
}

/// <summary>
/// Зонирование игроком (zoning-demand.md §14, Z6). <c>CommandKind.PaintZone</c> (23): участки, чей центр лежит в прямоугольнике (границы включены), по возрастанию индекса получают
/// вид (<c>ZoneKind</c>, 0 снимает зону), плотность и флаг <c>Painted</c>; здание другого вида уходит в <c>Abandoned</c> (§8.4, <see cref="Growth.Abandon"/>), здание того же вида остаётся.
/// Участки с флагом <c>Lost</c> пропускаются: у них нет геометрии (сейв её не хранит), центр не определён. Без режима жителей команда не поддерживается.
/// Проверка и обход участков общие для применения, превью (UI-C1) и обратной записи (<see cref="Scan"/>, <see cref="Check"/>, <see cref="BuildInverse"/>).
/// <c>R0</c> бит0 (<see cref="PaintZoneFlags.RestoreUnpainted"/>) - обратная запись: участкам не ставится <c>Painted</c> (они были нарисованы не игроком).
/// </summary>
internal static class Zoning
{
    private const int StateCount = 16;

    /// <summary>Общий валидатор превью, применения и обратной записи.</summary>
    public static RejectReason Check(World w, in PaintZoneCmd p, out ZonePlan plan)
    {
        plan = default;
        CityWorld? c = w.City;
        if (c == null)
        {
            return RejectReason.NotSupported;
        }

        if (!float.IsFinite(p.X0) || !float.IsFinite(p.Z0) || !float.IsFinite(p.X1) || !float.IsFinite(p.Z1) || p.ZoneKind > (byte)LotZone.Industrial || p.Density > 1 ||
            (p.R0 & ~PaintZoneFlags.RestoreUnpainted) != 0)
        {
            return RejectReason.InvalidArgument;
        }

        plan.X0 = MathF.Min(p.X0, p.X1);
        plan.X1 = MathF.Max(p.X0, p.X1);
        plan.Z0 = MathF.Min(p.Z0, p.Z1);
        plan.Z1 = MathF.Max(p.Z0, p.Z1);
        double dx = plan.X1 - plan.X0;
        double dz = plan.Z1 - plan.Z0;
        if ((dx * dz) > c.Cfg.Zoning.MaxPaintM2)
        {
            return RejectReason.ZoneRule;
        }

        plan.RectCells = (int)(Math.Min(Math.Ceiling(dx / 8.0), 1 << 15) * Math.Min(Math.Ceiling(dz / 8.0), 1 << 15));
        return RejectReason.None;
    }

    /// <summary>Применяет <c>PaintZone</c>; <see cref="RejectReason.None"/> - применена.</summary>
    public static RejectReason Apply(World w, in Command cmd)
    {
        RejectReason why = Check(w, in cmd.PaintZone, out ZonePlan plan);
        if (why != RejectReason.None)
        {
            return why;
        }

        CityWorld c = w.City!;
        Scan(w, c, in cmd.PaintZone, ref plan, apply: true, default, out _);
        w.Counters.PaintCommands++;
        return RejectReason.None;
    }

    /// <summary>
    /// Обход участков под прямоугольником (один для превью и применения: счётчики совпадают по построению). <paramref name="apply"/> - мутировать; иначе только считать.
    /// <paramref name="marks"/> - необязательный выход для подсветки: сколько влезло (<paramref name="marksTotal"/> - сколько всего).
    /// </summary>
    public static void Scan(World w, CityWorld c, in PaintZoneCmd p, ref ZonePlan plan, bool apply, Span<PaintLotMark> marks, out int marksTotal)
    {
        LotStore l = c.Lots;
        BuildingStore b = c.Buildings;
        bool painted = (p.R0 & PaintZoneFlags.RestoreUnpainted) == 0;
        int lots = 0;
        int changed = 0;
        int cells = 0;
        int abandon = 0;
        int written = 0;
        for (int i = 0; i < l.Count; i++)
        {
            if (!InRect(l, i, plan.X0, plan.Z0, plan.X1, plan.Z1))
            {
                continue;
            }

            lots++;
            cells += l.Orient[i] == LotOrient.Legacy ? 1 : Math.Max(1, l.CellsF[i] * l.CellsD[i]);
            byte state = PaintLotMarkState.Zoned;
            int bi = l.Building[i];
            if (bi >= 0 && b.IsAlive(bi) && (byte)c.Types.Rows[b.Type[bi]].Kind != p.ZoneKind && c.Types.Rows[b.Type[bi]].Kind != LotZone.Service)
            {
                if (b.State[bi] != (byte)BuildingState.Abandoned)
                {
                    abandon++;
                    state = PaintLotMarkState.ZonedAbandon;
                }

                if (apply)
                {
                    Growth.Abandon(w, c, bi);
                }
            }

            byte newFlags = painted ? (byte)(l.Flags[i] | LotFlags.Painted) : (byte)(l.Flags[i] & ~LotFlags.Painted);
            if (l.Zone[i] != p.ZoneKind || l.Density[i] != p.Density || l.Flags[i] != newFlags)
            {
                changed++;
            }
            else if (state == PaintLotMarkState.Zoned)
            {
                state = PaintLotMarkState.Unchanged;
            }

            if (written < marks.Length)
            {
                marks[written] = new PaintLotMark { Lot = i, State = state, X = l.X[i], Z = l.Z[i] };
            }

            written++;
            if (apply)
            {
                l.Zone[i] = p.ZoneKind;
                l.Density[i] = p.Density;
                l.Flags[i] = newFlags;
            }
        }

        if (apply && lots > 0)
        {
            l.Version++;
        }

        plan.Lots = lots;
        plan.Changed = changed;
        plan.Cells = cells;
        plan.Abandon = abandon;
        marksTotal = written;
    }

    private static bool InRect(LotStore l, int i, float x0, float z0, float x1, float z1) =>
        (l.Flags[i] & LotFlags.Lost) == 0 && l.X[i] >= x0 && l.X[i] <= x1 && l.Z[i] >= z0 && l.Z[i] <= z1;

    private static int StateOf(LotStore l, int i) =>
        l.Zone[i] | (l.Density[i] << 2) | ((l.Flags[i] & LotFlags.Painted) != 0 ? 8 : 0);

    private static PaintZoneCmd FromState(int s, float x0, float z0, float x1, float z1) => new()
    {
        X0 = x0,
        Z0 = z0,
        X1 = x1,
        Z1 = z1,
        ZoneKind = (byte)(s & 3),
        Density = (byte)((s >> 2) & 1),
        R0 = (s & 8) != 0 ? (byte)0 : PaintZoneFlags.RestoreUnpainted,
    };

    /// <summary>
    /// Обратная запись (player-tools.md §8.1): сначала весь прямоугольник возвращается в самое частое прежнее состояние участков (вид, плотность, Painted), затем по команде
    /// на каждый участок с другим состоянием (прямоугольник нулевой площади в центре участка). Возвращает, сколько команд нужно; пишет в <paramref name="dst"/> сколько влезло.
    /// Здания, ушедшие в <c>Abandoned</c>, не воскресают (undo логический).
    /// </summary>
    public static int BuildInverse(World w, in PaintZoneCmd p, Span<Command> dst)
    {
        if (Check(w, in p, out ZonePlan plan) != RejectReason.None)
        {
            return 0;
        }

        LotStore l = w.City!.Lots;
        Span<int> count = stackalloc int[StateCount];
        int lots = 0;
        for (int i = 0; i < l.Count; i++)
        {
            if (InRect(l, i, plan.X0, plan.Z0, plan.X1, plan.Z1))
            {
                count[StateOf(l, i)]++;
                lots++;
            }
        }

        if (lots == 0)
        {
            return 0;
        }

        int dom = 0;
        for (int s = 1; s < StateCount; s++)
        {
            if (count[s] > count[dom])
            {
                dom = s;
            }
        }

        int n = 0;
        Put(dst, ref n, Command.MakePaintZone(FromState(dom, plan.X0, plan.Z0, plan.X1, plan.Z1)));
        for (int i = 0; i < l.Count; i++)
        {
            if (InRect(l, i, plan.X0, plan.Z0, plan.X1, plan.Z1) && StateOf(l, i) != dom)
            {
                Put(dst, ref n, Command.MakePaintZone(FromState(StateOf(l, i), l.X[i], l.Z[i], l.X[i], l.Z[i])));
            }
        }

        return n;
    }

    private static void Put(Span<Command> dst, ref int n, in Command c)
    {
        if (n < dst.Length)
        {
            dst[n] = c;
        }

        n++;
    }
}
