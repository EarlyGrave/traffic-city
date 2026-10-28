using System;
using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Зонирование игроком (zoning-demand.md §14, Z6). <c>CommandKind.PaintZone</c> (23): участки, чей центр лежит в прямоугольнике (границы включены), по возрастанию индекса получают
/// вид (<c>ZoneKind</c>, 0 снимает зону), плотность и флаг <c>Painted</c>; здание другого вида уходит в <c>Abandoned</c> (§8.4, <see cref="Growth.Abandon"/>), здание того же вида остаётся.
/// Участки с флагом <c>Lost</c> пропускаются: у них нет геометрии (сейв её не хранит), центр не определён. Без режима жителей команда не поддерживается.
/// </summary>
internal static class Zoning
{
    /// <summary>Применяет <c>PaintZone</c>; <see cref="RejectReason.None"/> - применена.</summary>
    public static RejectReason Apply(World w, in Command cmd)
    {
        CityWorld? c = w.City;
        if (c == null)
        {
            return RejectReason.NotSupported;
        }

        PaintZoneCmd p = cmd.PaintZone;
        if (!float.IsFinite(p.X0) || !float.IsFinite(p.Z0) || !float.IsFinite(p.X1) || !float.IsFinite(p.Z1) || p.ZoneKind > (byte)LotZone.Industrial || p.Density > 1)
        {
            return RejectReason.InvalidArgument;
        }

        float x0 = MathF.Min(p.X0, p.X1);
        float x1 = MathF.Max(p.X0, p.X1);
        float z0 = MathF.Min(p.Z0, p.Z1);
        float z1 = MathF.Max(p.Z0, p.Z1);
        if (((double)(x1 - x0) * (z1 - z0)) > c.Cfg.Zoning.MaxPaintM2)
        {
            return RejectReason.ZoneRule;
        }

        LotStore l = c.Lots;
        BuildingStore b = c.Buildings;
        bool changed = false;
        for (int i = 0; i < l.Count; i++)
        {
            if ((l.Flags[i] & LotFlags.Lost) != 0 || l.X[i] < x0 || l.X[i] > x1 || l.Z[i] < z0 || l.Z[i] > z1)
            {
                continue;
            }

            int bi = l.Building[i];
            if (bi >= 0 && b.IsAlive(bi) && (byte)c.Types.Rows[b.Type[bi]].Kind != p.ZoneKind)
            {
                Growth.Abandon(w, c, bi);
            }

            l.Zone[i] = p.ZoneKind;
            l.Density[i] = p.Density;
            l.Flags[i] |= LotFlags.Painted;
            changed = true;
        }

        if (changed)
        {
            l.Version++;
        }

        w.Counters.PaintCommands++;
        return RejectReason.None;
    }
}
