using System;
using Sim.Core.City;

namespace Sim.Core.Api;

// Городские службы (services.md §2.3-§2.9, §7, SR2-SR3): сводка записи, вызовы для маркеров, охват, доступность врача. Вне тика, только чтение.
public sealed partial class Simulation
{
    /// <summary>Клеток слоя охвата (<see cref="GetServiceCoverage"/>, сетка BL 128 м); 0 - без режима <c>city.services</c>.</summary>
    public int ServiceCellCount => _disposed ? 0 : _w.City?.Services?.Cells ?? 0;

    /// <summary>Число записей служб (индексы <c>svc</c> для запросов и <see cref="CommandKind.SetServiceBudget"/>); 0 - служб нет.</summary>
    public int ServiceRecordCount => _disposed ? 0 : _w.City?.Services?.Table.Count ?? 0;

    /// <summary>Сводка записи <paramref name="svc"/>: вызовы, отклик, станции и машины, ползунок, расход за сутки. false - нет служб или записи.</summary>
    public bool GetServiceStats(int svc, out ServiceStats stats)
    {
        stats = default;
        CityWorld? c = _disposed ? null : _w.City;
        CityServices? sv = c?.Services;
        if (c == null || sv == null || (uint)svc >= (uint)sv.Table.Count)
        {
            return false;
        }

        stats.CallsQueued = sv.RecCallsQueued[svc];
        stats.CallsDone = sv.RecCallsDone[svc];
        stats.CallsLate = sv.RecCallsLate[svc];
        stats.RespTicksTotal = sv.RecRespTicks[svc];
        stats.AvgRespTicks = stats.CallsDone > 0 ? stats.RespTicksTotal / stats.CallsDone : 0;
        stats.CostToday = sv.SvcCostDay[(svc * 2) + (c.Day & 1)];
        stats.CostYesterday = sv.SvcCostDay[(svc * 2) + ((c.Day + 1) & 1)];
        stats.Waiting = sv.Normal[svc].Count + sv.Urgent[svc].Count;
        stats.Pct = sv.Pct[svc];
        stats.PctPending = sv.PctPending[svc];
        SiteStore s = sv.Sites;
        for (int i = 0; i < s.Capacity; i++)
        {
            int bi = s.Building[i];
            if (bi >= 0 && s.Svc[i] == svc && c.Buildings.Pool.IsAlive(bi, s.Gen[i]) && c.Buildings.State[bi] == (byte)BuildingState.Active)
            {
                stats.Stations++;
                stats.UnitsMax += s.UnitsMax[i];
                stats.BedsUsed += s.BedsUsed[i];
                stats.IcuUsed += s.IcuUsed[i];
                if (sv.Table.Explicit)
                {
                    stats.Beds += Illness.BedsCap(sv, i);
                    stats.Icu += Illness.IcuCap(sv, i);
                    stats.VisitsToday += c.Buildings.VisitsDay[Economy.Slot(bi, c.Day & 1)];
                }
            }
        }

        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity; k++)
        {
            byte st = u.State[k];
            if (st is CrewState.Free or CrewState.OffDuty || (uint)u.Site[k] >= (uint)s.Capacity || s.Svc[u.Site[k]] != svc)
            {
                continue;
            }

            stats.UnitsOnLine++;
            stats.UnitsBusy += st == CrewState.Idle ? 0 : 1;
        }

        return true;
    }

    /// <summary>
    /// Вызовы для маркеров (§7 C3): сначала ждущие в кольцах (по записям: срочное, обычное), затем машины к цели и на месте. Пишет не больше <c>dst.Length</c>, возвращает записанное.
    /// </summary>
    public int GetServiceCalls(Span<ServiceCallInfo> dst)
    {
        CityWorld? c = _disposed ? null : _w.City;
        CityServices? sv = c?.Services;
        if (c == null || sv == null)
        {
            return 0;
        }

        int n = 0;
        for (int r = 0; r < sv.Table.Count; r++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Util.RingBuffer<ServiceCall> ring = pass == 0 ? sv.Urgent[r] : sv.Normal[r];
                for (int i = 0; i < ring.Count && n < dst.Length; i++)
                {
                    ref readonly ServiceCall q = ref ring.At(i);
                    if (Call(c, q.Target, q.TargetGen, q.Tick, q.Kind, q.Svc, 0, pass == 0, ref dst[n]))
                    {
                        n++;
                    }
                }
            }
        }

        CrewStore u = sv.Crews;
        for (int k = 0; k < u.Capacity && n < dst.Length; k++)
        {
            byte st = u.State[k];
            if (st is not (CrewState.ToSite or CrewState.Working) || (uint)u.Site[k] >= (uint)sv.Sites.Capacity)
            {
                continue;
            }

            int rec = sv.Sites.Svc[u.Site[k]];
            int ci = sv.Table.CallOf(rec, u.Kind[k]);
            bool urgent = ci >= 0 && (sv.Table.Calls[ci].Flags & ServiceCallFlags.Urgent) != 0;
            if (Call(c, u.Target[k], u.TargetGen[k], u.ReqTick[k], u.Kind[k], (byte)rec, st == CrewState.ToSite ? (byte)1 : (byte)2, urgent, ref dst[n]))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Очагов сейчас (ожидающих момента и горящих); 0 - без режима <c>city.services</c> или огня нет.</summary>
    public int ActiveFireCount => _disposed ? 0 : _w.City?.Services?.Fires.Used() ?? 0;

    /// <summary>Горящие здания для маркеров (§2.7, SR4): по слотам пула пожаров; ожидающие момента не выдаются. Возвращает число записанных.</summary>
    public int GetFires(Span<FireInfo> dst)
    {
        CityWorld? c = _disposed ? null : _w.City;
        CityServices? sv = c?.Services;
        if (c == null || sv == null)
        {
            return 0;
        }

        FireStore f = sv.Fires;
        long smolder = (long)c.Cfg.Services.SmolderS * TicksPerSecond;
        int n = 0;
        for (int i = 0; i < f.Capacity && n < dst.Length; i++)
        {
            int bi = f.Building[i];
            if (f.State[i] != FireState.Burning || !c.Buildings.Pool.IsAlive(bi, f.Gen[i]))
            {
                continue;
            }

            int lot = c.Buildings.Lot[bi];
            ref FireInfo o = ref dst[n++];
            o.X = c.Lots.X[lot];
            o.Z = c.Lots.Z[lot];
            o.Building = bi;
            o.AgeTicks = (int)Math.Clamp(_w.Tick - f.StartTick[i], 0, int.MaxValue);
            o.CondStart = f.CondStart[i];
            o.Cond = c.Buildings.Cond[bi];
            o.Smoldering = _w.Tick - f.StartTick[i] < smolder ? (byte)1 : (byte)0;
        }

        return n;
    }

    private bool Call(CityWorld c, int bi, int gen, long tick, byte kind, byte svc, byte enRoute, bool urgent, ref ServiceCallInfo o)
    {
        if (!c.Buildings.Pool.IsAlive(bi, gen))
        {
            return false;
        }

        int lot = c.Buildings.Lot[bi];
        o.X = c.Lots.X[lot];
        o.Z = c.Lots.Z[lot];
        o.Building = bi;
        o.AgeTicks = (int)Math.Clamp(_w.Tick - tick, 0, int.MaxValue);
        o.Kind = kind;
        o.Svc = svc;
        o.EnRoute = enRoute;
        o.Urgent = urgent ? (byte)1 : (byte)0;
        return true;
    }

    /// <summary>
    /// Доступность врача по клеткам 128 м (§2.6, SR3): <c>CareEwma</c> - EWMA стоимости ноги к врачу от домов клетки, секунды; 0 - приёмов не было. Длина <paramref name="perCell"/>
    /// не меньше <see cref="ServiceCellCount"/>. Возвращает число клеток (0 - без режима).
    /// </summary>
    public int GetCareCoverage(Span<ushort> perCell)
    {
        CityServices? sv = _disposed ? null : _w.City?.Services;
        if (sv == null || sv.Cells == 0 || perCell.Length < sv.Cells)
        {
            return 0;
        }

        sv.CareEwma.AsSpan(0, sv.Cells).CopyTo(perCell);
        return sv.Cells;
    }

    /// <summary>
    /// Охват записи <paramref name="svc"/> по клеткам 128 м (§2.9), секунды отклика: <c>RespEwma</c>, пустые клетки - оценка от ближайшей работающей станции записи (манхэттен ×1.3 на 8 м/с + 60 с,
    /// без обращения к скимам вне тика); станций нет - 0. Длина <paramref name="perCell"/> не меньше <see cref="ServiceCellCount"/>. Возвращает число клеток (0 - без режима или записи).
    /// </summary>
    public int GetServiceCoverage(int svc, Span<ushort> perCell)
    {
        CityWorld? c = _disposed ? null : _w.City;
        CityServices? sv = c?.Services;
        NeighborhoodFields? f = c?.Fields;
        if (c == null || sv == null || f == null || sv.Cells == 0 || (uint)svc >= (uint)sv.Table.Count || perCell.Length < sv.Cells)
        {
            return 0;
        }

        SiteStore s = sv.Sites;
        int cells = sv.Cells;
        for (int cell = 0; cell < cells; cell++)
        {
            ushort v = sv.RespEwma[(svc * cells) + cell];
            if (v == 0)
            {
                float x = f.CenterX(cell);
                float z = f.CenterZ(cell);
                float best = float.MaxValue;
                for (int i = 0; i < s.Capacity; i++)
                {
                    int bi = s.Building[i];
                    if (bi < 0 || s.Svc[i] != svc || !c.Buildings.Pool.IsAlive(bi, s.Gen[i]) || c.Buildings.State[bi] != (byte)BuildingState.Active)
                    {
                        continue;
                    }

                    int lot = c.Buildings.Lot[bi];
                    best = MathF.Min(best, MathF.Abs(c.Lots.X[lot] - x) + MathF.Abs(c.Lots.Z[lot] - z));
                }

                v = best == float.MaxValue ? (ushort)0 : (ushort)Math.Clamp(((int)best * 13 / 10 / 8) + 60, 1, ushort.MaxValue);
            }

            perCell[cell] = v;
        }

        return cells;
    }
}
