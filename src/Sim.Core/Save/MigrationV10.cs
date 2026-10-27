using System;
using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 9 -> 10 (save-format.md §4, §12; ADR-0026, parking.md §13, задача K1): парковка как ресурс и землепользование. Чистая функция над <see cref="SaveTables"/>.
/// Мир из файла v9 - без парковки и без сохранённых полигонов землепользования: <c>PARM</c> 219-239 = нули (режима нет, как пишет <c>Create</c> без секции <c>parking</c>); блоки <c>PARK</c> и <c>LUSE</c>
/// не создаются (землепользование прежних сейвов не восстановить: seed и <c>city.landuse</c> в файле нет, <c>LandUse</c> после <c>Load</c> пусто, как и раньше);
/// <c>BTYP</c> 10-12 = нули по строкам, <c>RESD</c> 41-49 (<c>HomeFac</c>, <c>CarFac</c>, <c>CarLot</c> = -1, остальное 0), <c>VEHC</c> 42-44 (<c>ParkFac</c>, <c>ParkSearchTick</c> = -1), <c>TRIP</c> 12-13 = 0,
/// <c>CNTR</c> 157-172 = 0. Поведение и хэш не меняются (раздел хэша парковки пишется только в режиме), поэтому мир из файла w1-w9 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV10
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int cntr = t.FindBlock(SaveFormat.FourCc("CNTR"u8));
        int vehc = t.FindBlock(SaveFormat.FourCc("VEHC"u8));
        int trip = t.FindBlock(SaveFormat.FourCc("TRIP"u8));
        if (parm < 0 || cntr < 0 || vehc < 0 || trip < 0 ||
            !t.TryGetColumn(vehc, 1, out ReadOnlySpan<int> vehHigh) || vehHigh.Length != 1 || vehHigh[0] < 0 ||
            !t.TryGetColumn(trip, 1, out ReadOnlySpan<int> tripCount) || tripCount.Length != 1 || tripCount[0] < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        bool ok = true;
        ok &= t.SetColumn(parm, 219, new int[1]);
        ok &= t.SetColumn(parm, 220, new byte[1]);
        ok &= t.SetColumn(parm, 221, new byte[1]);
        for (uint id = 222; id <= 232; id++)
        {
            ok &= t.SetColumn(parm, id, new int[1]);
        }

        ok &= t.SetColumn(parm, 233, new byte[1]);
        ok &= t.SetColumn(parm, 234, new byte[1]);
        ok &= t.SetColumn(parm, 235, new int[1]);
        ok &= t.SetColumn(parm, 236, new byte[1]);
        ok &= t.SetColumn(parm, 237, new int[1]);
        ok &= t.SetColumn(parm, 238, new byte[1]);
        ok &= t.SetColumn(parm, 239, new int[1]);

        for (uint id = 157; id <= 172; id++)
        {
            ok &= t.SetColumn(cntr, id, new long[1]);
        }

        ok &= t.SetColumn(vehc, 42, Filled(vehHigh[0], -1));
        ok &= t.SetColumn(vehc, 43, new int[vehHigh[0]]);
        ok &= t.SetColumn(vehc, 44, Filled(vehHigh[0], -1L));
        ok &= t.SetColumn(trip, 12, new int[tripCount[0]]);
        ok &= t.SetColumn(trip, 13, new int[tripCount[0]]);

        int btyp = t.FindBlock(SaveFormat.FourCc("BTYP"u8));
        if (btyp >= 0)
        {
            if (!t.TryGetColumn(btyp, 1, out ReadOnlySpan<byte> kinds))
            {
                return LoadStatus.MigrationFailed;
            }

            int rows = kinds.Length;
            ok &= t.SetColumn(btyp, 10, new int[rows]);
            ok &= t.SetColumn(btyp, 11, new byte[rows]);
            ok &= t.SetColumn(btyp, 12, new int[rows]);
        }

        int resd = t.FindBlock(SaveFormat.FourCc("RESD"u8));
        if (resd >= 0)
        {
            if (!t.TryGetColumn(resd, 1, out ReadOnlySpan<int> high) || high.Length != 1 || high[0] < 0)
            {
                return LoadStatus.MigrationFailed;
            }

            int n = high[0];
            ok &= t.SetColumn(resd, 41, Filled(n, -1));
            ok &= t.SetColumn(resd, 42, new int[n]);
            ok &= t.SetColumn(resd, 43, Filled(n, -1));
            ok &= t.SetColumn(resd, 44, new int[n]);
            ok &= t.SetColumn(resd, 45, Filled(n, -1));
            ok &= t.SetColumn(resd, 46, new int[n]);
            ok &= t.SetColumn(resd, 47, new byte[n]);
            ok &= t.SetColumn(resd, 48, new long[n]);
            ok &= t.SetColumn(resd, 49, new int[n]);
        }

        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }

    private static int[] Filled(int n, int v)
    {
        var a = new int[n];
        Array.Fill(a, v);
        return a;
    }

    private static long[] Filled(int n, long v)
    {
        var a = new long[n];
        Array.Fill(a, v);
        return a;
    }
}
