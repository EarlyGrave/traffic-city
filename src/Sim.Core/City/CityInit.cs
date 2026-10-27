using System;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Demand;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Создание города (zoning-demand.md §2.5): нарезка участков, автозонирование, здания и начальное население. Всё по индексу, PRNG только <c>RngStream.Demand</c> в фиксированном
/// порядке (на каждый участок и каждого жителя - постоянное число обращений). Начальные балансы - проводки <c>External -&gt;</c> (CZ2). Работа назначается простым сопоставлением
/// «первая фирма со свободным местом в той же дорожной компоненте» - до рынка Z4 (§8.1); не нашедшие работу встают в очередь ищущих.
/// </summary>
internal static class CityInit
{
    public static CreateStatus Run(World w)
    {
        CityWorld c = w.City!;
        if (!c.LotGen.BuildAll(w))
        {
            return CreateStatus.InvalidCity;   // max_lots меньше числа участков сети
        }

        if (c.Cfg.Zoning.Auto)
        {
            AutoZone(w, c);
            Populate(w, c);
        }

        if (c.Parking != null)
        {
            Parking.ParkingBuild.CreateAll(w, c);   // обочины участков и стоянки активных зданий (parking.md §3)
            if (c.Cfg.Zoning.Auto)
            {
                Parking.HomeAssign.AssignInitial(w, c);   // домашние места начального населения; кому не хватило - HasCar снимается (§4)
            }
        }

        c.Lots.Version++;
        c.RebuildWheel(-1);   // колесо жителей: начальные события Plan (Z2)
        Accessibility.Init(w);   // производные скимов, поле доступности и LS_ref (zoning-demand.md §6.7)
        if (c.Cfg.Zoning.Auto)
        {
            AssignJobs(w, c);   // начальная работа - тем же рынком, что и днём (§2.5 п. 3, §8.1)
        }

        BudgetFlows.InitCreated(w, c);   // казна (city-economy.md §3): вклад, LV = T, содержание, оценка дохода; без city_budget ничего не делает

        return CreateStatus.Ok;
    }

    /// <summary>Вид зоны ячеек <c>zone_m</c> по удалению от центра, доли <c>c_center</c>/<c>i_edge</c>/<c>unzoned</c> (§2.5 п. 2).</summary>
    private static void AutoZone(World w, CityWorld c)
    {
        LotStore l = c.Lots;
        CityGrid g = c.Grid;
        float zoneM = w.Demand.ZoneSize > 1f ? w.Demand.ZoneSize : 500f;
        float width = g.CellsX * g.CellM;
        float height = g.CellsZ * g.CellM;
        int zx = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling(width / zoneM)));
        int zz = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling(height / zoneM)));
        var cellOf = new int[l.Count];
        var occupied = new bool[zx * zz];
        for (int i = 0; i < l.Count; i++)
        {
            int cx = Math.Clamp(DetMath.ToIntSat(MathF.Floor((l.X[i] - g.OriginX) / zoneM)), 0, zx - 1);
            int cz = Math.Clamp(DetMath.ToIntSat(MathF.Floor((l.Z[i] - g.OriginZ) / zoneM)), 0, zz - 1);
            cellOf[i] = (cz * zx) + cx;
            occupied[cellOf[i]] = true;
        }

        float midX = g.OriginX + (width * 0.5f);
        float midZ = g.OriginZ + (height * 0.5f);
        int n = 0;
        for (int k = 0; k < occupied.Length; k++)
        {
            n += occupied[k] ? 1 : 0;
        }

        var keys = new long[n];
        int m = 0;
        for (int k = 0; k < occupied.Length; k++)
        {
            if (!occupied[k])
            {
                continue;
            }

            float dx = g.OriginX + (((k % zx) + 0.5f) * zoneM) - midX;
            float dz = g.OriginZ + (((k / zx) + 0.5f) * zoneM) - midZ;
            keys[m++] = (DetMath.ToLongSat((dx * dx) + (dz * dz)) * 4L << 24) + k;
        }

        Array.Sort(keys);
        int nC = (int)(((long)n * c.Cfg.Zoning.CCenterShareQ16) + 65535 >> 16);
        int nI = (int)(((long)n * c.Cfg.Zoning.IEdgeShareQ16) + 65535 >> 16);
        nC = Math.Min(nC, n);
        nI = Math.Min(nI, n - nC);
        var zoneOfCell = new byte[occupied.Length];
        for (int k = 0; k < n; k++)
        {
            int cell = (int)(keys[k] & ((1L << 24) - 1));
            zoneOfCell[cell] = (byte)(k < nC ? LotZone.Commercial : (k >= n - nI ? LotZone.Industrial : LotZone.Residential));
        }

        uint unzoned = (uint)c.Cfg.Zoning.UnzonedShareQ16;
        for (int i = 0; i < l.Count; i++)
        {
            bool empty = (w.RngDemand.NextUInt() >> 16) < unzoned;
            l.Zone[i] = empty ? (byte)LotZone.None : zoneOfCell[cellOf[i]];
            l.Density[i] = w.Net.Segments[l.Seg[i]].Kind == RoadKind.Arterial ? (byte)1 : (byte)0;
        }
    }

    private static void Populate(World w, CityWorld c)
    {
        LotStore l = c.Lots;
        BuildingStore b = c.Buildings;
        CityResidents r = c.Residents;
        CityDesc cfg = c.Cfg;
        long housing = 0;
        for (int i = 0; i < l.Count; i++)
        {
            if (l.Zone[i] == (byte)LotZone.None)
            {
                continue;
            }

            int type = c.Types.Find((LotZone)l.Zone[i], l.Density[i], (int)(w.RngDemand.NextUInt() & 0x7FFFFFFF));
            if (type < 0)
            {
                continue;
            }

            if (!b.TryCreate(i, type, BuildingState.Active, 0, out int bi))
            {
                break;
            }

            l.Building[i] = bi;
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[type];
            if (t.Kind == LotZone.Residential)
            {
                housing += t.Capacity;
            }
            else
            {
                b.Jobs[bi] = t.Capacity;
                c.Ledger.Transfer(c.Ledger.ExternalAccount, c.Ledger.AccountOfBuilding(bi), cfg.Economy.InitialFirmBalance, FlowKind.Endowment);
            }
        }

        // Жители: на каждое жилое здание - доля заполнения, распределённая нарастающим итогом (без остатков), по возрастанию индекса здания.
        long target = (housing * cfg.Residents.InitialFillQ16) >> 16;
        long seen = 0;
        long given = 0;
        for (int bi = 0; bi < b.HighWater; bi++)
        {
            ref readonly BuildingTypeDesc t = ref c.Types.Rows[b.Type[bi]];
            if (t.Kind != LotZone.Residential)
            {
                continue;
            }

            seen += t.Capacity;
            long want = (seen * target / Math.Max(1L, housing)) - given;
            for (long k = 0; k < want; k++)
            {
                if (!r.TryCreate(out int ri))
                {
                    return;
                }

                given++;
                r.Home[ri] = bi;
                r.NextHome[ri] = b.HeadRes[bi];
                b.HeadRes[bi] = ri;
                b.Occupants[bi]++;
                byte flags = 0;
                if ((w.RngDemand.NextUInt() >> 16) < (uint)cfg.Residents.CarOwnershipQ16)
                {
                    flags |= ResidentFlags.HasCar;
                }

                if ((w.RngDemand.NextUInt() >> 16) < (uint)cfg.Residents.LabourShareQ16)
                {
                    flags |= ResidentFlags.Labour;
                }

                r.Flags[ri] = flags;
                r.State[ri] = (byte)ResidentState.AtHome;
                int planSlot = cfg.Residents.PlanSlot + w.RngDemand.NextInt(0, 13);
                r.EvTick[ri] = c.TickOfSlot(0, planSlot);
                r.EvKind[ri] = ResidentEvent.Plan;
                c.Ledger.Transfer(c.Ledger.ExternalAccount, Ledger.AccountOfResident(ri), cfg.Economy.InitialResidentBalance, FlowKind.Endowment);
            }
        }
    }

    /// <summary>Начальная работа: рабочие в порядке индекса жителя подбирают фирму рынком (§8.1); не нашедшие встают в очередь ищущих.</summary>
    private static void AssignJobs(World w, CityWorld c)
    {
        CityResidents r = c.Residents;
        for (int ri = 0; ri < r.HighWater; ri++)
        {
            if (!r.IsAlive(ri) || (r.Flags[ri] & ResidentFlags.Labour) == 0)
            {
                continue;
            }

            int firm = c.Market.PickFirm(w, c, ri, -1, out _);
            if (firm >= 0)
            {
                c.Market.Employ(w, c, ri, firm);
            }
            else
            {
                Market.StartSeeking(c, ri);
            }
        }
    }
}
