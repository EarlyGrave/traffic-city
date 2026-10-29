using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Save;

// world_version 14 (new-game-scratch.md §2.1-§2.5, ADR-0037, задача Y3): внешние подключения города.
//   PARM 259-261 (число подключений, затухание, въезд машиной), 262-277 (по 4 поля на подключение 0..3); без секции outside - нули.
//   Блок OUTS (есть <=> секция outside): внешние места, занято, счётчики въезда. Виртуальные участки узлов - производное, не сохраняются.
internal static partial class SaveSchema
{
    private static readonly string[] _outsideConnFields = { "Node", "Jobs", "JobCostTicks", "ShareQ16" };

    private static ColumnDef[] OutsideParams()
    {
        var cols = new ColumnDef[3 + (CityOutsideDesc.Max * _outsideConnFields.Length)];
        cols[0] = Col.Scalar<int>(259, "CityOutsideCount", w => w.Town.Outside.Count, (c, v) => c.Desc.Town.Outside.Count = v, "0");
        cols[1] = Col.Scalar<int>(260, "CityOutsideJobsDecayQ16", w => w.Town.Outside.JobsDecayQ16, (c, v) => c.Desc.Town.Outside.JobsDecayQ16 = v, "0");
        cols[2] = Col.Scalar<byte>(261, "CityOutsideArrivalByCar", w => w.Town.Outside.ArrivalByCar, (c, v) => c.Desc.Town.Outside.ArrivalByCar = v, "0");
        for (int k = 0; k < CityOutsideDesc.Max; k++)
        {
            for (int f = 0; f < _outsideConnFields.Length; f++)
            {
                int kk = k;
                int ff = f;
                cols[3 + (k * _outsideConnFields.Length) + f] = Col.Scalar<int>(
                    (uint)(262 + (k * _outsideConnFields.Length) + f),
                    "CityOutside" + k + _outsideConnFields[f],
                    w => ConnField(w.Town.Outside.Get(kk), ff),
                    (c, v) =>
                    {
                        OutsideConnDesc d = c.Desc.Town.Outside.Get(kk);
                        SetConnField(ref d, ff, v);
                        c.Desc.Town.Outside.Set(kk, d);
                    },
                    "0");
            }
        }

        return cols;
    }

    private static int ConnField(in OutsideConnDesc d, int f) => f switch
    {
        0 => d.Node,
        1 => d.Jobs,
        2 => d.JobCostTicks,
        _ => d.ShareQ16,
    };

    private static void SetConnField(ref OutsideConnDesc d, int f, int v)
    {
        switch (f)
        {
            case 0:
                d.Node = v;
                break;
            case 1:
                d.Jobs = v;
                break;
            case 2:
                d.JobCostTicks = v;
                break;
            default:
                d.ShareQ16 = v;
                break;
        }
    }

    private static readonly Func<World, int> _outN = static w => w.City!.Outside!.Count;

    // ---- OUTS: внешние подключения (есть <=> секция outside) ----
    private static BlockDef OutsideBlock() => new("OUTS", "внешние подключения: внешние места, занято жителями, въезд за сутки и за час, всего доехало, не дождались выезда (new-game-scratch.md §2)", new[]
    {
        Col.Array<int>(1, "Jobs", w => w.City!.Outside!.Jobs, _outN, "CityOutsideCount"),
        Col.Array<int>(2, "Taken", w => w.City!.Outside!.Taken, _outN, "CityOutsideCount"),
        Col.Array<int>(3, "ArrivalsDay", w => w.City!.Outside!.ArrivalsDay, _outN, "CityOutsideCount"),
        Col.Array<int>(4, "ArrivalsHour", w => w.City!.Outside!.ArrivalsHour, _outN, "CityOutsideCount"),
        Col.Scalar<long>(5, "Arrivals", w => w.City!.Outside!.Arrivals, (c, v) => c.W.City!.Outside!.Arrivals = v),
        Col.Scalar<long>(6, "ArrivalTimeouts", w => w.City!.Outside!.ArrivalTimeouts, (c, v) => c.W.City!.Outside!.ArrivalTimeouts = v),
    })
    {
        After = c => SaveValidator.ValidateOutside(c),
        PresentText = "секция outside",
        Present = static w => w.City?.Outside != null,
    };
}
